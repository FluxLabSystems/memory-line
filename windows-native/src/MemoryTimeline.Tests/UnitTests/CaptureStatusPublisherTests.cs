using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="CaptureStatusPublisher"/> over the REAL
/// <see cref="SyncOutboxRepository"/> and the EF InMemory provider (unique
/// database per test instance) with a mocked
/// <see cref="IPendingEventRepository"/>. Covers the stage → status mapping the
/// phone consumes, the device-capture-only rule, failure classification,
/// transcript preview bounds, and publish deduplication (design §19 Phase 3).
/// </summary>
public class CaptureStatusPublisherTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly TestDbContextFactory _contextFactory;
    private readonly SyncOutboxRepository _outboxRepository;
    private readonly Mock<IPendingEventRepository> _pendingEventRepository = new();
    private readonly CaptureStatusPublisher _publisher;

    public CaptureStatusPublisherTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _outboxRepository = new SyncOutboxRepository(_contextFactory);
        _pendingEventRepository
            .Setup(r => r.GetByQueueIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingEvent>());
        _publisher = new CaptureStatusPublisher(
            _outboxRepository,
            _pendingEventRepository.Object,
            NullLogger<CaptureStatusPublisher>.Instance);
    }

    [Fact]
    public async Task PublishAsync_ReceivedCapture_WritesCaptureStatusRowWithCamelCasePayload()
    {
        // Arrange
        var item = DeviceCapture(QueueStatus.Pending, QueueProcessingStage.ReadyForTranscription);

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        var row = await SingleRowAsync();
        row.EntityType.Should().Be(SyncEntityType.CaptureStatus);
        row.EntityType.Should().Be(SyncChangeEntityType.CaptureStatus);
        row.EntityId.Should().Be(item.SourceCaptureId);
        row.Operation.Should().Be(SyncOutboxOperation.Upsert);
        row.DeliveredAt.Should().BeNull();

        // The wire payload must be camelCase, like every other sync body.
        using var document = JsonDocument.Parse(row.PayloadJson!);
        document.RootElement.TryGetProperty("captureId", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("processingStage", out _).Should().BeTrue();

        var payload = Deserialize(row);
        payload.CaptureId.Should().Be(item.SourceCaptureId);
        payload.Status.Should().Be(SyncCaptureStatus.Received);
        payload.ProcessingStage.Should().Be(QueueProcessingStage.ReadyForTranscription);
        payload.UpdatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        payload.TranscriptAvailable.Should().BeFalse();
        payload.TranscriptPreview.Should().BeNull();
        payload.TranscriptCharCount.Should().BeNull();
        // Nothing has been extracted yet: counts stay null rather than a made-up 0.
        payload.PendingEventCount.Should().BeNull();
        payload.ApprovedEventCount.Should().BeNull();
        payload.FailureReason.Should().BeNull();
        payload.FailureRetryable.Should().BeNull();
    }

    [Theory]
    [InlineData(QueueProcessingStage.PendingDownload, SyncCaptureStatus.Received)]
    [InlineData(QueueProcessingStage.Verifying, SyncCaptureStatus.Received)]
    [InlineData(QueueProcessingStage.ReadyForTranscription, SyncCaptureStatus.Received)]
    [InlineData(QueueProcessingStage.Transcribing, SyncCaptureStatus.Processing)]
    [InlineData(QueueProcessingStage.Extracting, SyncCaptureStatus.Processing)]
    [InlineData(QueueProcessingStage.ReviewReady, SyncCaptureStatus.ReviewReady)]
    [InlineData(QueueProcessingStage.Completed, SyncCaptureStatus.Completed)]
    public async Task PublishAsync_EachStage_MapsToTheStatusThePhoneShows(string stage, string expectedStatus)
    {
        // Arrange
        var item = DeviceCapture(QueueStatus.Processing, stage);

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        var payload = Deserialize(await SingleRowAsync());
        payload.Status.Should().Be(expectedStatus);
        payload.ProcessingStage.Should().Be(stage);
    }

    [Fact]
    public async Task PublishAsync_FullLifecycle_WritesOneRowPerTransitionInOrder()
    {
        // Arrange - one capture walking the pipeline
        var item = DeviceCapture(QueueStatus.Pending, QueueProcessingStage.ReadyForTranscription);

        // Act
        await _publisher.PublishAsync(item);

        item.Status = QueueStatus.Processing;
        item.ProcessingStage = QueueProcessingStage.Transcribing;
        await _publisher.PublishAsync(item);

        item.Transcript = "we stopped at the overlook past mile marker 40";
        item.Status = QueueStatus.Completed;
        item.ProcessingStage = QueueProcessingStage.ReviewReady;
        _pendingEventRepository
            .Setup(r => r.GetByQueueIdAsync(item.QueueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                PendingEventWith(PendingStatus.PendingReview),
                PendingEventWith(PendingStatus.PendingReview),
                PendingEventWith(PendingStatus.Approved),
                PendingEventWith(PendingStatus.Rejected),
            });
        await _publisher.PublishAsync(item);

        item.ProcessingStage = QueueProcessingStage.Completed;
        await _publisher.PublishAsync(item);

        // Assert
        var payloads = await PayloadsAsync();
        payloads.Select(p => p.Status).Should().Equal(
            SyncCaptureStatus.Received,
            SyncCaptureStatus.Processing,
            SyncCaptureStatus.ReviewReady,
            SyncCaptureStatus.Completed);

        payloads[1].TranscriptAvailable.Should().BeFalse();

        var reviewReady = payloads[2];
        reviewReady.TranscriptAvailable.Should().BeTrue();
        reviewReady.TranscriptPreview.Should().Be(item.Transcript);
        reviewReady.TranscriptCharCount.Should().Be(item.Transcript!.Length);
        // Rejected events belong to neither count.
        reviewReady.PendingEventCount.Should().Be(2);
        reviewReady.ApprovedEventCount.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_WindowsLocalRecording_PublishesNothing()
    {
        // Arrange - ingestion stamps local recordings with a capture ID but no
        // device ID and the windows platform; no phone is waiting on them.
        var item = DeviceCapture(QueueStatus.Completed, QueueProcessingStage.ReviewReady);
        item.SourceDeviceId = null;
        item.SourcePlatform = CapturePlatform.Windows;

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_QueueItemWithoutCaptureId_PublishesNothing()
    {
        // Arrange - a pasted text source has no capture identity at all
        var item = DeviceCapture(QueueStatus.Completed, QueueProcessingStage.Completed);
        item.SourceCaptureId = null;
        item.SourceDeviceId = null;

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        (await RowsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(QueueProcessingStage.FailedRetryable, true)]
    [InlineData(QueueProcessingStage.FailedConfiguration, false)]
    [InlineData(QueueProcessingStage.FailedPermanent, false)]
    public async Task PublishAsync_FailureStages_MapToFailedWithRetryableFlag(string stage, bool expectedRetryable)
    {
        // Arrange - the queue's own error text is raw exception detail
        var item = DeviceCapture(QueueStatus.Failed, stage);
        item.ErrorMessage = @"Could not read C:\Users\owner\AppData\cache\capture.wav (key sk-secret)";

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        var payload = Deserialize(await SingleRowAsync());
        payload.Status.Should().Be(SyncCaptureStatus.Failed);
        payload.ProcessingStage.Should().Be(stage);
        payload.FailureRetryable.Should().Be(expectedRetryable);
        payload.FailureReason.Should().NotBeNullOrWhiteSpace();

        // §14.5: never a file path, credential, or raw exception text.
        payload.FailureReason.Should().NotContain(@"C:\");
        payload.FailureReason.Should().NotContain("sk-secret");
    }

    [Fact]
    public async Task PublishAsync_FailedWithoutStage_LeavesRetryabilityUnstated()
    {
        // Arrange - a row written before processing stages existed
        var item = DeviceCapture(QueueStatus.Failed, processingStage: null);

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        var payload = Deserialize(await SingleRowAsync());
        payload.Status.Should().Be(SyncCaptureStatus.Failed);
        payload.ProcessingStage.Should().BeNull();
        payload.FailureRetryable.Should().BeNull();
        payload.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(599)]
    [InlineData(600)]
    [InlineData(601)]
    [InlineData(5000)]
    public async Task PublishAsync_LongTranscript_PreviewIsBoundedButCountIsTheFullLength(int transcriptLength)
    {
        // Arrange
        var item = DeviceCapture(QueueStatus.Completed, QueueProcessingStage.ReviewReady);
        item.Transcript = new string('t', transcriptLength);

        // Act
        await _publisher.PublishAsync(item);

        // Assert
        var payload = Deserialize(await SingleRowAsync());
        payload.TranscriptAvailable.Should().BeTrue();
        payload.TranscriptCharCount.Should().Be(transcriptLength);

        var expectedPreviewLength = Math.Min(transcriptLength, CaptureStatusChangePayload.TranscriptPreviewMaxChars);
        payload.TranscriptPreview.Should().NotBeNull();
        payload.TranscriptPreview!.Length.Should().Be(expectedPreviewLength);
        payload.TranscriptPreview.Should().Be(item.Transcript![..expectedPreviewLength]);
    }

    [Fact]
    public async Task PublishAsync_TranscriptCutAtASurrogatePair_TruncatesOnACharacterBoundary()
    {
        // Arrange - filler puts a two-char emoji exactly across the 600 limit
        var item = DeviceCapture(QueueStatus.Completed, QueueProcessingStage.ReviewReady);
        item.Transcript = new string('t', CaptureStatusChangePayload.TranscriptPreviewMaxChars - 1) + "🚗 rest";

        // Act
        await _publisher.PublishAsync(item);

        // Assert - one char short rather than a lone surrogate
        var payload = Deserialize(await SingleRowAsync());
        payload.TranscriptCharCount.Should().Be(item.Transcript!.Length);
        payload.TranscriptPreview!.Length
            .Should().Be(CaptureStatusChangePayload.TranscriptPreviewMaxChars - 1);
        char.IsSurrogate(payload.TranscriptPreview[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_UnchangedStatusAndStage_DoesNotWriteASecondRow()
    {
        // Arrange
        var item = DeviceCapture(QueueStatus.Processing, QueueProcessingStage.Transcribing);
        await _publisher.PublishAsync(item);

        // Act - the same projection again (e.g. a re-entered stage)
        await _publisher.PublishAsync(item);
        await _publisher.PublishAsync(item);

        // Assert
        (await RowsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_UnchangedStatusOnAnotherCapture_IsNotConfusedWithThisOne()
    {
        // Arrange
        var first = DeviceCapture(QueueStatus.Processing, QueueProcessingStage.Transcribing);
        var second = DeviceCapture(QueueStatus.Processing, QueueProcessingStage.Transcribing);

        // Act
        await _publisher.PublishAsync(first);
        await _publisher.PublishAsync(second);
        await _publisher.PublishAsync(first);

        // Assert - dedupe is per capture, not global
        var rows = await RowsAsync();
        rows.Select(r => r.EntityId).Should().Equal(first.SourceCaptureId, second.SourceCaptureId);
    }

    [Fact]
    public async Task PublishAsync_ApprovalAtAnUnchangedStage_StillReachesThePhone()
    {
        // Arrange - review_ready published with everything still pending
        var item = DeviceCapture(QueueStatus.Completed, QueueProcessingStage.ReviewReady);
        _pendingEventRepository
            .Setup(r => r.GetByQueueIdAsync(item.QueueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { PendingEventWith(PendingStatus.PendingReview) });
        await _publisher.PublishAsync(item);

        // Act - the user approves it on Windows; stage does not move
        _pendingEventRepository
            .Setup(r => r.GetByQueueIdAsync(item.QueueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { PendingEventWith(PendingStatus.Approved) });
        await _publisher.PublishAsync(item);

        // Assert
        var payloads = await PayloadsAsync();
        payloads.Should().HaveCount(2);
        payloads[0].PendingEventCount.Should().Be(1);
        payloads[0].ApprovedEventCount.Should().Be(0);
        payloads[1].PendingEventCount.Should().Be(0);
        payloads[1].ApprovedEventCount.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_AfterAFailure_RetryRepublishesTheEarlierStatus()
    {
        // Arrange
        var item = DeviceCapture(QueueStatus.Failed, QueueProcessingStage.FailedRetryable);
        await _publisher.PublishAsync(item);

        // Act - RetryFailedItemAsync puts the item back to ready_for_transcription
        item.Status = QueueStatus.Pending;
        item.ProcessingStage = QueueProcessingStage.ReadyForTranscription;
        item.ErrorMessage = null;
        await _publisher.PublishAsync(item);

        // Assert - the phone must stop showing the failure it was told about
        var payloads = await PayloadsAsync();
        payloads.Select(p => p.Status).Should().Equal(SyncCaptureStatus.Failed, SyncCaptureStatus.Received);
        payloads[1].FailureReason.Should().BeNull();
        payloads[1].FailureRetryable.Should().BeNull();
    }

    [Fact]
    public async Task PublishAsync_ReviewCountsUnavailable_StillPublishesTheStatus()
    {
        // Arrange
        var item = DeviceCapture(QueueStatus.Completed, QueueProcessingStage.ReviewReady);
        _pendingEventRepository
            .Setup(r => r.GetByQueueIdAsync(item.QueueId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        await _publisher.PublishAsync(item);

        // Assert - counts are decoration on the status, never the reason to lose it
        var payload = Deserialize(await SingleRowAsync());
        payload.Status.Should().Be(SyncCaptureStatus.ReviewReady);
    }

    #region Helpers

    private static RecordingQueue DeviceCapture(string status, string? processingStage) => new()
    {
        QueueId = Guid.NewGuid().ToString(),
        AudioFilePath = @"C:\cache\capture.wav",
        Status = status,
        ProcessingStage = processingStage,
        SourceCaptureId = Guid.NewGuid().ToString(),
        SourceDeviceId = "device-1",
        SourcePlatform = CapturePlatform.Ios,
        SyncState = QueueSyncState.Received,
    };

    private static PendingEvent PendingEventWith(PendingStatus status) => new()
    {
        Status = status.ToStringValue(),
        IsApproved = status == PendingStatus.Approved,
    };

    private static CaptureStatusChangePayload Deserialize(SyncOutbox row) =>
        JsonSerializer.Deserialize<CaptureStatusChangePayload>(row.PayloadJson!, WireJson)!;

    private async Task<List<SyncOutbox>> RowsAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.SyncOutboxEntries.AsNoTracking()
            .Where(o => o.EntityType == SyncEntityType.CaptureStatus)
            .OrderBy(o => o.OutboxId)
            .ToListAsync();
    }

    private async Task<SyncOutbox> SingleRowAsync()
    {
        var rows = await RowsAsync();
        rows.Should().HaveCount(1);
        return rows[0];
    }

    private async Task<List<CaptureStatusChangePayload>> PayloadsAsync()
    {
        var rows = await RowsAsync();
        return rows.Select(Deserialize).ToList();
    }

    #endregion
}
