using FluentAssertions;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

public class QueueServiceTests
{
    private readonly Mock<IRecordingQueueRepository> _repositoryMock;
    private readonly Mock<IEventExtractionService> _extractionServiceMock;
    private readonly Mock<INotificationService> _notificationServiceMock;
    private readonly Mock<ILogger<QueueService>> _loggerMock;
    private readonly QueueService _queueService;

    public QueueServiceTests()
    {
        _repositoryMock = new Mock<IRecordingQueueRepository>();
        _extractionServiceMock = new Mock<IEventExtractionService>();
        _notificationServiceMock = new Mock<INotificationService>();
        _loggerMock = new Mock<ILogger<QueueService>>();

        // QueueService now REQUIRES an INotificationService (throws ArgumentNullException otherwise).
        _queueService = new QueueService(
            _repositoryMock.Object,
            _extractionServiceMock.Object,
            _loggerMock.Object,
            _notificationServiceMock.Object);
    }

    /// <summary>
    /// Builds a service over the shared mocks plus an explicit capture status
    /// publisher (an optional dependency: absent in the shared instance above).
    /// </summary>
    private QueueService CreateQueueServiceWith(ICaptureStatusPublisher statusPublisher) => new(
        _repositoryMock.Object,
        _extractionServiceMock.Object,
        _loggerMock.Object,
        _notificationServiceMock.Object,
        statusPublisher);

    [Fact]
    public void Constructor_NullNotificationService_ThrowsArgumentNullException()
    {
        // INotificationService is a required dependency (no null default).
        Assert.Throws<ArgumentNullException>(() => new QueueService(
            _repositoryMock.Object,
            _extractionServiceMock.Object,
            _loggerMock.Object,
            null!));
    }

    [Fact]
    public async Task AddToQueueAsync_ValidRecording_AddsToQueue()
    {
        // Arrange
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = "/path/to/audio.wav",
            DurationSeconds = 120.5,
            FileSizeBytes = 1024000,
            CreatedAt = DateTime.UtcNow
        };

        var expectedQueueItem = new RecordingQueue
        {
            QueueId = recording.QueueId,
            AudioFilePath = recording.AudioFilePath,
            Status = QueueStatus.Pending,
            DurationSeconds = recording.DurationSeconds,
            FileSizeBytes = recording.FileSizeBytes,
            CreatedAt = recording.CreatedAt
        };

        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedQueueItem);

        // Act
        var result = await _queueService.AddToQueueAsync(recording);

        // Assert
        result.Should().NotBeNull();
        result.QueueId.Should().Be(recording.QueueId);
        result.Status.Should().Be(QueueStatus.Pending);
        _repositoryMock.Verify(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAllQueueItemsAsync_WithItems_ReturnsAllItems()
    {
        // Arrange
        var queueItems = new List<RecordingQueue>
        {
            new() { QueueId = "1", AudioFilePath = "/path1.wav", Status = QueueStatus.Pending },
            new() { QueueId = "2", AudioFilePath = "/path2.wav", Status = QueueStatus.Processing },
            new() { QueueId = "3", AudioFilePath = "/path3.wav", Status = QueueStatus.Completed }
        };

        _repositoryMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItems);

        // Act
        var results = await _queueService.GetAllQueueItemsAsync();

        // Assert
        results.Should().HaveCount(3);
        results.Select(r => r.QueueId).Should().Contain(new[] { "1", "2", "3" });
    }

    [Fact]
    public async Task UpdateQueueItemStatusAsync_ExistingItem_UpdatesStatus()
    {
        // Arrange
        var queueId = "test-queue-id";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _queueService.UpdateQueueItemStatusAsync(queueId, QueueStatus.Completed);

        // Assert
        _repositoryMock.Verify(r => r.UpdateAsync(It.Is<RecordingQueue>(q =>
            q.QueueId == queueId && q.Status == QueueStatus.Completed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetQueueCountByStatusAsync_WithStatusFilter_ReturnsCorrectCount()
    {
        // Arrange
        var status = QueueStatus.Pending;
        var expectedCount = 5;

        _repositoryMock.Setup(r => r.GetCountByStatusAsync(status, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCount);

        // Act
        var count = await _queueService.GetQueueCountByStatusAsync(status);

        // Assert
        count.Should().Be(expectedCount);
    }

    [Fact]
    public async Task RemoveQueueItemAsync_ExistingItem_RemovesItem()
    {
        // Arrange
        var queueId = "test-queue-id";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.DeleteAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _queueService.RemoveQueueItemAsync(queueId);

        // Assert
        _repositoryMock.Verify(r => r.DeleteAsync(It.Is<RecordingQueue>(q => q.QueueId == queueId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryFailedItemAsync_FailedItem_ResetsStatusToPending()
    {
        // Arrange
        var queueId = "failed-item";
        var failedItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Failed,
            ErrorMessage = "Processing error",
            ProcessedAt = DateTime.UtcNow
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _queueService.RetryFailedItemAsync(queueId);

        // Assert
        _repositoryMock.Verify(r => r.UpdateAsync(It.Is<RecordingQueue>(q =>
            q.QueueId == queueId &&
            q.Status == QueueStatus.Pending &&
            q.ErrorMessage == null &&
            q.ProcessedAt == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearCompletedItemsAsync_WithCompletedItems_RemovesAllCompleted()
    {
        // Arrange
        var completedItems = new List<RecordingQueue>
        {
            new() { QueueId = "1", Status = QueueStatus.Completed },
            new() { QueueId = "2", Status = QueueStatus.Completed },
            new() { QueueId = "3", Status = QueueStatus.Completed }
        };

        _repositoryMock.Setup(r => r.GetByStatusAsync(QueueStatus.Completed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedItems);

        _repositoryMock.Setup(r => r.DeleteAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _queueService.ClearCompletedItemsAsync();

        // Assert
        _repositoryMock.Verify(r => r.DeleteAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task AddToQueueAsync_NewRecording_StampsProcessingStageAndSyncState()
    {
        // Arrange
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = "/path/to/audio.wav",
            CreatedAt = DateTime.UtcNow
        };

        RecordingQueue? added = null;
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => added = q)
            .ReturnsAsync((RecordingQueue q, CancellationToken _) => q);

        // Act
        await _queueService.AddToQueueAsync(recording);

        // Assert - new local items start ready for transcription and unsynced
        added.Should().NotBeNull();
        added!.ProcessingStage.Should().Be(QueueProcessingStage.ReadyForTranscription);
        added.SyncState.Should().Be(QueueSyncState.LocalOnly);
    }

    [Fact]
    public async Task GetAllQueueItemsAsync_CarriesTheSyncStateOntoTheDto()
    {
        // Arrange - one row per sync state, as ingestion and the outbox
        // publisher leave them
        var rows = new List<RecordingQueue>
        {
            new() { QueueId = "local", AudioFilePath = "/a.wav", Status = QueueStatus.Pending, SyncState = QueueSyncState.LocalOnly },
            new() { QueueId = "remote", AudioFilePath = "/b.wav", Status = QueueStatus.Pending, SyncState = QueueSyncState.Received },
            new() { QueueId = "acked", AudioFilePath = "/c.wav", Status = QueueStatus.Completed, SyncState = QueueSyncState.Acknowledged },
            new() { QueueId = "legacy", AudioFilePath = "/d.wav", Status = QueueStatus.Completed, SyncState = null },
        };
        _repositoryMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

        // Act
        var dtos = (await _queueService.GetAllQueueItemsAsync()).ToDictionary(d => d.QueueId, d => d.SyncState);

        // Assert - the DTO reports what the row says, including "unknown" for
        // rows that pre-date the sync columns
        dtos["local"].Should().Be(QueueSyncState.LocalOnly);
        dtos["remote"].Should().Be(QueueSyncState.Received);
        dtos["acked"].Should().Be(QueueSyncState.Acknowledged);
        dtos["legacy"].Should().BeNull();
    }

    [Fact]
    public async Task UpdateQueueItemStatusAsync_WithProcessingStage_PersistsStage()
    {
        // Arrange
        var queueId = "stage-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _queueService.UpdateQueueItemStatusAsync(queueId, QueueStatus.Processing,
            processingStage: QueueProcessingStage.Transcribing);

        // Assert
        _repositoryMock.Verify(r => r.UpdateAsync(It.Is<RecordingQueue>(q =>
            q.QueueId == queueId &&
            q.Status == QueueStatus.Processing &&
            q.ProcessingStage == QueueProcessingStage.Transcribing), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateQueueItemStatusAsync_WithoutProcessingStage_LeavesStageUntouched()
    {
        // Arrange
        var queueId = "stage-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Processing,
            ProcessingStage = QueueProcessingStage.Transcribing
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act - no processingStage argument
        await _queueService.UpdateQueueItemStatusAsync(queueId, QueueStatus.Completed);

        // Assert - status changed, stage preserved
        queueItem.Status.Should().Be(QueueStatus.Completed);
        queueItem.ProcessingStage.Should().Be(QueueProcessingStage.Transcribing);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessCaptureAsync_UnknownQueueId_DoesNotInvokeExtraction()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync("missing-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecordingQueue?)null);

        // Act
        await _queueService.ProcessCaptureAsync("missing-id");

        // Assert
        _extractionServiceMock.Verify(e => e.ProcessRecordingAsync(
            It.IsAny<string>(), It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCaptureAsync_CompletedItem_IsSkipped()
    {
        // Arrange
        var queueItem = new RecordingQueue
        {
            QueueId = "done-item",
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Completed
        };

        _repositoryMock.Setup(r => r.GetByIdAsync("done-item", It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        // Act
        await _queueService.ProcessCaptureAsync("done-item");

        // Assert - neither extraction nor any status write happened
        _extractionServiceMock.Verify(e => e.ProcessRecordingAsync(
            It.IsAny<string>(), It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCaptureAsync_PendingItemWithEvents_CompletesAsReviewReady()
    {
        // Arrange
        var queueId = "pending-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription
        };

        // The same instance is mutated across updates, so snapshot each
        // UpdateAsync call instead of verifying with It.Is predicates.
        var updates = new List<(string Status, string? Stage)>();

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => updates.Add((q.Status, q.ProcessingStage)))
            .Returns(Task.CompletedTask);

        _extractionServiceMock.Setup(e => e.ProcessRecordingAsync(queueId, It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        // Act
        await _queueService.ProcessCaptureAsync(queueId);

        // Assert - transcribing while processing, review_ready once events exist
        updates.Should().NotBeEmpty();
        updates[0].Status.Should().Be(QueueStatus.Processing);
        updates[0].Stage.Should().Be(QueueProcessingStage.Transcribing);
        updates[^1].Status.Should().Be(QueueStatus.Completed);
        updates[^1].Stage.Should().Be(QueueProcessingStage.ReviewReady);
        queueItem.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessCaptureAsync_PendingItemWithNoEvents_CompletesWithCompletedStage()
    {
        // Arrange
        var queueId = "empty-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription
        };

        var updates = new List<(string Status, string? Stage)>();

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => updates.Add((q.Status, q.ProcessingStage)))
            .Returns(Task.CompletedTask);

        _extractionServiceMock.Setup(e => e.ProcessRecordingAsync(queueId, It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        await _queueService.ProcessCaptureAsync(queueId);

        // Assert - nothing to review, so the item is simply done
        updates.Should().NotBeEmpty();
        updates[^1].Status.Should().Be(QueueStatus.Completed);
        updates[^1].Stage.Should().Be(QueueProcessingStage.Completed);
    }

    [Fact]
    public async Task ProcessCaptureAsync_ConfigurationException_FailsAsConfigurationWithoutRetrying()
    {
        // Arrange
        var queueId = "config-error-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription
        };

        var updates = new List<(string Status, string? Stage)>();

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => updates.Add((q.Status, q.ProcessingStage)))
            .Returns(Task.CompletedTask);

        _extractionServiceMock.Setup(e => e.ProcessRecordingAsync(queueId, It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConfigurationException("LLM API key is not configured"));

        // Act
        await _queueService.ProcessCaptureAsync(queueId);

        // Assert - non-retryable: extraction attempted exactly once, item failed
        // with the configuration stage
        _extractionServiceMock.Verify(e => e.ProcessRecordingAsync(
            queueId, It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()), Times.Once);
        updates.Should().NotBeEmpty();
        updates[^1].Status.Should().Be(QueueStatus.Failed);
        updates[^1].Stage.Should().Be(QueueProcessingStage.FailedConfiguration);
        queueItem.ErrorMessage.Should().Be("LLM API key is not configured");
    }

    [Fact]
    public async Task ProcessNextItemAsync_ForwardsTheTokenAndPutsACancelledItemBackToPending()
    {
        // Arrange: the user cancels while the item is being transcribed.
        var queueItem = new RecordingQueue
        {
            QueueId = "cancel-me",
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription
        };
        var updates = new List<(string Status, string? Stage)>();
        _repositoryMock.Setup(r => r.GetByStatusAsync(QueueStatus.Pending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RecordingQueue> { queueItem });
        _repositoryMock.Setup(r => r.GetByIdAsync("cancel-me", It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => updates.Add((q.Status, q.ProcessingStage)))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var forwarded = CancellationToken.None;
        _extractionServiceMock
            .Setup(e => e.ProcessRecordingAsync("cancel-me", It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()))
            .Returns<string, IProgress<(int, string)>?, CancellationToken>(async (_, _, token) =>
            {
                forwarded = token;
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                await Task.Yield();
                return 0;
            });

        // Act
        var act = () => _queueService.ProcessNextItemAsync(cts.Token);

        // Assert: the caller's token reached the extraction call, the
        // cancellation propagated, and the item went back to Pending with no
        // error and no failure toast - cancellation is not a failure.
        await act.Should().ThrowAsync<OperationCanceledException>();
        forwarded.Should().Be(cts.Token);
        updates[0].Status.Should().Be(QueueStatus.Processing);
        updates[^1].Status.Should().Be(QueueStatus.Pending);
        updates[^1].Stage.Should().Be(QueueProcessingStage.ReadyForTranscription);
        queueItem.ErrorMessage.Should().BeNull();
        _notificationServiceMock.Verify(n => n.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAllPendingAsync_CancelledBetweenItems_LeavesTheRemainingItemsPending()
    {
        // Arrange: two pending items; the batch is cancelled right after the
        // first completes.
        var first = new RecordingQueue { QueueId = "first", AudioFilePath = "/a.wav", Status = QueueStatus.Pending };
        var second = new RecordingQueue { QueueId = "second", AudioFilePath = "/b.wav", Status = QueueStatus.Pending };
        _repositoryMock.Setup(r => r.GetByStatusAsync(QueueStatus.Pending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RecordingQueue> { first, second });
        _repositoryMock.Setup(r => r.GetByIdAsync("first", It.IsAny<CancellationToken>())).ReturnsAsync(first);
        _repositoryMock.Setup(r => r.GetByIdAsync("second", It.IsAny<CancellationToken>())).ReturnsAsync(second);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        _extractionServiceMock
            .Setup(e => e.ProcessRecordingAsync("first", It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return 1;
            });

        // Act
        var act = () => _queueService.ProcessAllPendingAsync(cts.Token);

        // Assert: the first item finished normally, the second was never started.
        await act.Should().ThrowAsync<OperationCanceledException>();
        first.Status.Should().Be(QueueStatus.Completed);
        second.Status.Should().Be(QueueStatus.Pending);
        _extractionServiceMock.Verify(
            e => e.ProcessRecordingAsync("second", It.IsAny<IProgress<(int, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessNextItemAsync_PreCancelledToken_ThrowsBeforeTouchingTheQueue()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = () => _queueService.ProcessNextItemAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _repositoryMock.Verify(r => r.GetByStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateQueueItemStatusAsync_WithStatusPublisher_PublishesTheUpdatedItem()
    {
        // Arrange
        var publisherMock = new Mock<ICaptureStatusPublisher>();
        var published = new List<(string Status, string? Stage)>();
        publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            // The same instance is mutated across updates, so snapshot it here.
            .Callback<RecordingQueue, CancellationToken>((q, _) => published.Add((q.Status, q.ProcessingStage)))
            .Returns(Task.CompletedTask);

        var queueService = CreateQueueServiceWith(publisherMock.Object);

        var queueId = "device-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription,
            SourceCaptureId = "capture-1",
            SourceDeviceId = "device-1",
            SourcePlatform = CapturePlatform.Ios
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await queueService.UpdateQueueItemStatusAsync(queueId, QueueStatus.Processing,
            processingStage: QueueProcessingStage.Transcribing);

        // Assert - the phone hears about the transition that was just persisted
        published.Should().ContainSingle();
        published[0].Status.Should().Be(QueueStatus.Processing);
        published[0].Stage.Should().Be(QueueProcessingStage.Transcribing);
    }

    [Fact]
    public async Task RetryFailedItemAsync_WithStatusPublisher_PublishesTheReactivatedItem()
    {
        // Arrange
        var publisherMock = new Mock<ICaptureStatusPublisher>();
        var published = new List<(string Status, string? Stage)>();
        publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => published.Add((q.Status, q.ProcessingStage)))
            .Returns(Task.CompletedTask);

        var queueService = CreateQueueServiceWith(publisherMock.Object);

        var queueId = "failed-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Failed,
            ProcessingStage = QueueProcessingStage.FailedRetryable,
            ErrorMessage = "transcription failed",
            SourceCaptureId = "capture-1",
            SourceDeviceId = "device-1",
            SourcePlatform = CapturePlatform.Ios
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await queueService.RetryFailedItemAsync(queueId);

        // Assert - the failure the phone was told about is withdrawn
        published.Should().ContainSingle();
        published[0].Status.Should().Be(QueueStatus.Pending);
        published[0].Stage.Should().Be(QueueProcessingStage.ReadyForTranscription);
    }

    [Fact]
    public async Task UpdateQueueItemStatusAsync_StatusPublisherThrows_StillUpdatesTheItem()
    {
        // Arrange
        var publisherMock = new Mock<ICaptureStatusPublisher>();
        publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("outbox unavailable"));

        var queueService = CreateQueueServiceWith(publisherMock.Object);

        var queueId = "device-item";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = "/path.wav",
            Status = QueueStatus.Pending,
            SourceCaptureId = "capture-1",
            SourceDeviceId = "device-1",
            SourcePlatform = CapturePlatform.Ios
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act - status publishing must never break processing
        await queueService.UpdateQueueItemStatusAsync(queueId, QueueStatus.Completed,
            processingStage: QueueProcessingStage.Completed);

        // Assert
        queueItem.Status.Should().Be(QueueStatus.Completed);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueItemStatusChanged_WhenStatusUpdated_RaisesEvent()
    {
        // Arrange
        var eventRaised = false;
        string? capturedQueueId = null;
        string? capturedNewStatus = null;

        _queueService.QueueItemStatusChanged += (sender, args) =>
        {
            eventRaised = true;
            capturedQueueId = args.QueueId;
            capturedNewStatus = args.NewStatus;
        };

        var queueId = "test-id";
        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            Status = QueueStatus.Pending
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(queueId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueItem);

        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _queueService.UpdateQueueItemStatusAsync(queueId, QueueStatus.Processing);

        // Assert
        eventRaised.Should().BeTrue();
        capturedQueueId.Should().Be(queueId);
        capturedNewStatus.Should().Be(QueueStatus.Processing);
    }
}
