using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using System.Security.Cryptography;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="CaptureIngestionService"/> over REAL repositories and the
/// EF InMemory provider (unique per test instance). The service's idempotency is
/// an explicit pre-check by source capture / queue ID — not a reliance on the
/// relational unique index — so it is fully testable on InMemory; the unique
/// index itself is covered by the SQLite-backed SchemaUpgrader tests.
/// </summary>
public class CaptureIngestionServiceTests : IDisposable
{
    private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TestDbContextFactory _contextFactory;
    private readonly CaptureIngestionService _service;
    private readonly List<string> _tempFiles = new();

    public CaptureIngestionServiceTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _service = new CaptureIngestionService(
            new CaptureRepository(_contextFactory),
            new RecordingQueueRepository(_contextFactory),
            new LocalFileArtifactResolver(),
            NullLogger<CaptureIngestionService>.Instance);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_ValidRecording_WritesQueueCaptureArtifactAndOutboxRows()
    {
        // Arrange
        var audioPath = CreateTempAudioFile(out var content);
        var expectedSha256 = ComputeSha256LowercaseHex(content);
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = audioPath,
            DurationSeconds = 12.5,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.IngestLocalRecordingAsync(recording);

        // Assert
        result.Success.Should().BeTrue();
        result.AlreadyIngested.Should().BeFalse();

        await using var context = _contextFactory.CreateDbContext();

        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.QueueId.Should().Be(recording.QueueId);
        queueItem.Status.Should().Be(QueueStatus.Pending);
        queueItem.ProcessingStage.Should().Be(QueueProcessingStage.ReadyForTranscription);
        queueItem.SyncState.Should().Be(QueueSyncState.LocalOnly);
        queueItem.SourcePlatform.Should().Be(CapturePlatform.Windows);
        queueItem.SourceCaptureId.Should().NotBeNullOrEmpty().And.Be(result.CaptureId);
        queueItem.AudioArtifactId.Should().NotBeNullOrEmpty().And.Be(result.ArtifactId);
        queueItem.OriginalFileName.Should().Be(Path.GetFileName(audioPath));
        queueItem.ContentSha256.Should().Be(expectedSha256);
        queueItem.FileSizeBytes.Should().Be(content.Length);

        var capture = await context.Captures.SingleAsync();
        capture.CaptureId.Should().Be(result.CaptureId);
        capture.Status.Should().Be(CaptureStatus.Received);
        capture.SourcePlatform.Should().Be(CapturePlatform.Windows);

        var artifact = await context.CaptureArtifacts.SingleAsync();
        artifact.ArtifactId.Should().Be(result.ArtifactId);
        artifact.CaptureId.Should().Be(result.CaptureId);
        artifact.ArtifactType.Should().Be(CaptureArtifactType.AudioOriginal);
        artifact.ByteLength.Should().Be(content.Length);
        artifact.Sha256.Should().Be(expectedSha256);
        artifact.StorageLocator.Should().Be(audioPath);
        artifact.UploadState.Should().Be(CaptureArtifactUploadState.Local);

        var outboxEntry = await context.SyncOutboxEntries.SingleAsync();
        outboxEntry.EntityType.Should().Be(SyncEntityType.Capture);
        outboxEntry.EntityId.Should().Be(result.CaptureId);
        outboxEntry.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_SameRecordingTwice_IsIdempotent()
    {
        // Arrange
        var audioPath = CreateTempAudioFile(out _);
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = audioPath,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var first = await _service.IngestLocalRecordingAsync(recording);
        var second = await _service.IngestLocalRecordingAsync(recording);

        // Assert - replay is acknowledged, no second set of rows is written
        first.Success.Should().BeTrue();
        first.AlreadyIngested.Should().BeFalse();
        second.Success.Should().BeTrue();
        second.AlreadyIngested.Should().BeTrue();
        second.QueueId.Should().Be(first.QueueId);

        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.CountAsync()).Should().Be(1);
        (await context.SyncOutboxEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_MissingAudioFile_FailsRetryableWithoutWritingRows()
    {
        // Arrange
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.wav"),
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.IngestLocalRecordingAsync(recording);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(IngestionFailureKind.ArtifactUnavailable);
        await AssertNoRowsWrittenAsync();
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_ValidEnvelope_MapsProvenanceAndMetadata()
    {
        // Arrange
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.SourceDeviceId = "iphone-15-pro";
        envelope.TripId = "trip-route-66";
        envelope.TitleHint = "Grand Canyon overlook";
        envelope.TimezoneId = "America/Phoenix";
        envelope.LocalOffsetMinutes = -420;

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeTrue();
        result.AlreadyIngested.Should().BeFalse();
        result.CaptureId.Should().Be(envelope.CaptureId);

        await using var context = _contextFactory.CreateDbContext();

        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.SourcePlatform.Should().Be(CapturePlatform.Ios);
        queueItem.SourceCaptureId.Should().Be(envelope.CaptureId);
        queueItem.SourceDeviceId.Should().Be("iphone-15-pro");
        queueItem.SyncState.Should().Be(QueueSyncState.Received);
        queueItem.AudioFilePath.Should().Be(audioPath);
        queueItem.AudioArtifactId.Should().Be(result.ArtifactId);
        queueItem.OriginalFileName.Should().Be(envelope.OriginalFileName);
        queueItem.ContentSha256.Should().Be(envelope.ExpectedSha256);

        var capture = await context.Captures.SingleAsync();
        capture.CaptureId.Should().Be(envelope.CaptureId);
        capture.SourceDeviceId.Should().Be("iphone-15-pro");
        capture.SourcePlatform.Should().Be(CapturePlatform.Ios);
        capture.CaptureType.Should().Be(CaptureTypes.TripLog);
        capture.TripId.Should().Be("trip-route-66");
        capture.TitleHint.Should().Be("Grand Canyon overlook");
        capture.TimezoneId.Should().Be("America/Phoenix");
        capture.LocalOffsetMinutes.Should().Be(-420);
        capture.Status.Should().Be(CaptureStatus.Received);

        var artifact = await context.CaptureArtifacts.SingleAsync();
        artifact.UploadState.Should().Be(CaptureArtifactUploadState.Downloaded);
        artifact.ByteLength.Should().Be(content.Length);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_WithLocation_StoresTheSnapshotOnTheCaptureAndNowhereElse()
    {
        // Arrange
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.Location = new RemoteCaptureLocation
        {
            Latitude = 36.0544,
            Longitude = -112.1401,
            HorizontalAccuracyM = 12.5,
        };

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeTrue();

        await using var context = _contextFactory.CreateDbContext();
        var capture = await context.Captures.SingleAsync();
        capture.LocationLatitude.Should().Be(36.0544);
        capture.LocationLongitude.Should().Be(-112.1401);
        capture.LocationAccuracyM.Should().Be(12.5);

        (await context.Locations.CountAsync()).Should().Be(0,
            "a coordinate is not a named place; a locations row is the user's or the opt-in geocoder's to make");
        (await context.SyncOutboxEntries.SingleAsync()).PayloadJson
            .Should().NotContain("36.0544", "precise location never enters the outbox (privacy rule §14.5)");
    }

    [Theory]
    [InlineData(91.0, 0.0)]
    [InlineData(0.0, -181.0)]
    [InlineData(double.NaN, 0.0)]
    public async Task IngestRemoteCaptureAsync_FixOffTheGlobe_IsDroppedWithoutFailingTheCapture(
        double latitude, double longitude)
    {
        // Arrange
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.Location = new RemoteCaptureLocation { Latitude = latitude, Longitude = longitude, HorizontalAccuracyM = 5 };

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert - the capture is ingested; the impossible fix is not kept
        result.Success.Should().BeTrue("a bad fix never costs the recording");

        await using var context = _contextFactory.CreateDbContext();
        var capture = await context.Captures.SingleAsync();
        capture.LocationLatitude.Should().BeNull();
        capture.LocationLongitude.Should().BeNull();
        capture.LocationAccuracyM.Should().BeNull();
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_WithStatusPublisher_AlsoPublishesReceivedStatus()
    {
        // Arrange - the capturing phone is waiting to hear Windows has it
        var service = CreateServiceWithStatusPublisher();
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.SourceDeviceId = "iphone-15-pro";

        // Act
        var result = await service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeTrue();

        await using var context = _contextFactory.CreateDbContext();
        var statusEntry = await context.SyncOutboxEntries
            .SingleAsync(o => o.EntityType == SyncEntityType.CaptureStatus);
        statusEntry.EntityId.Should().Be(envelope.CaptureId);
        statusEntry.PayloadJson.Should().Contain(SyncCaptureStatus.Received);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_WithStatusPublisher_PublishesNoCaptureStatus()
    {
        // Arrange - a recording made on Windows has no phone waiting on it
        var service = CreateServiceWithStatusPublisher();
        var audioPath = CreateTempAudioFile(out _);
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = audioPath,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var result = await service.IngestLocalRecordingAsync(recording);

        // Assert - only the capture row, no status noise
        result.Success.Should().BeTrue();

        await using var context = _contextFactory.CreateDbContext();
        (await context.SyncOutboxEntries.CountAsync(o => o.EntityType == SyncEntityType.CaptureStatus))
            .Should().Be(0);
        (await context.SyncOutboxEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_UppercaseExpectedSha256_IsAcceptedCaseInsensitively()
    {
        // Arrange - devices may declare the hash in uppercase hex
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.ExpectedSha256 = envelope.ExpectedSha256.ToUpperInvariant();

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert - accepted, and the stored hash is the computed lowercase form
        result.Success.Should().BeTrue();
        result.AlreadyIngested.Should().BeFalse();

        await using var context = _contextFactory.CreateDbContext();
        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.ContentSha256.Should().Be(envelope.ExpectedSha256.ToLowerInvariant());
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_SameCaptureIdTwice_ReturnsExistingQueueItem()
    {
        // Arrange
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);

        // Act
        var first = await _service.IngestRemoteCaptureAsync(envelope);
        var second = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.AlreadyIngested.Should().BeTrue();
        second.QueueId.Should().Be(first.QueueId);

        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_Sha256Mismatch_FailsPermanentWithoutWritingRows()
    {
        // Arrange - correct byte length, wrong (but well-formed) hash
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.ExpectedSha256 = new string('a', 64);

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(IngestionFailureKind.ArtifactInvalid);
        await AssertNoRowsWrittenAsync();
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_ByteLengthMismatch_FailsPermanentWithoutWritingRows()
    {
        // Arrange - correct hash, wrong declared length
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.ExpectedByteLength = content.Length + 1;

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(IngestionFailureKind.ArtifactInvalid);
        await AssertNoRowsWrittenAsync();
    }

    [Theory]
    [InlineData(null, ValidSha256, 100L, "some-locator")] // missing capture ID
    [InlineData("capture-1", null, 100L, "some-locator")] // missing hash
    [InlineData("capture-1", ValidSha256, 0L, "some-locator")] // zero byte length
    [InlineData("capture-1", ValidSha256, -1L, "some-locator")] // negative byte length
    [InlineData("capture-1", ValidSha256, 100L, null)] // missing storage locator
    public async Task IngestRemoteCaptureAsync_MalformedEnvelope_FailsAsInvalidRequest(
        string? captureId, string? expectedSha256, long expectedByteLength, string? storageLocator)
    {
        // Arrange
        var envelope = new RemoteCaptureEnvelope
        {
            CaptureId = captureId ?? string.Empty,
            ExpectedSha256 = expectedSha256 ?? string.Empty,
            ExpectedByteLength = expectedByteLength,
            StorageLocator = storageLocator ?? string.Empty
        };

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(IngestionFailureKind.InvalidRequest);
        await AssertNoRowsWrittenAsync();
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_OutboxPayload_ExcludesUserContent()
    {
        // Arrange - user-authored text must never leave the machine via the
        // metadata-only outbox payload (privacy rule §14.5)
        const string noteSentinel = "SENTINEL-PRIVATE-NOTE-4f2a";
        const string titleSentinel = "SENTINEL-PRIVATE-TITLE-9c1d";

        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        envelope.UserNote = noteSentinel;
        envelope.TitleHint = titleSentinel;

        // Act
        var result = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeTrue();

        await using var context = _contextFactory.CreateDbContext();
        var outboxEntry = await context.SyncOutboxEntries.SingleAsync();
        outboxEntry.PayloadJson.Should().NotBeNullOrEmpty();
        outboxEntry.PayloadJson.Should().NotContain(noteSentinel);
        outboxEntry.PayloadJson.Should().NotContain(titleSentinel);
        outboxEntry.PayloadJson.Should().Contain(envelope.CaptureId);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_SameContentDifferentFile_ReturnsExistingItem()
    {
        // Arrange - identical bytes under two different paths and queue IDs
        // (e.g. the same file imported twice under timestamped copy names)
        var content = new byte[512];
        Random.Shared.NextBytes(content);
        var firstPath = CreateTempAudioFileWithContent(content);
        var secondPath = CreateTempAudioFileWithContent(content);

        // Act
        var first = await _service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = firstPath,
            CreatedAt = DateTime.UtcNow
        });
        var second = await _service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = secondPath,
            CreatedAt = DateTime.UtcNow
        });

        // Assert - duplicate hash means reuse (§12.2)
        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.AlreadyIngested.Should().BeTrue();
        second.QueueId.Should().Be(first.QueueId);

        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.CountAsync()).Should().Be(1);
        (await context.Captures.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_SameContentNewCaptureId_ReturnsExistingItem()
    {
        // Arrange - a device re-uploads the same audio under a freshly minted
        // capture ID (e.g. reinstall/restore before the receipt was acknowledged)
        var audioPath = CreateTempAudioFile(out var content);
        var first = await _service.IngestRemoteCaptureAsync(CreateValidEnvelope(audioPath, content));

        var replay = CreateValidEnvelope(audioPath, content); // new CaptureId, same bytes

        // Act
        var second = await _service.IngestRemoteCaptureAsync(replay);

        // Assert
        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.AlreadyIngested.Should().BeTrue();
        second.QueueId.Should().Be(first.QueueId);

        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.CountAsync()).Should().Be(1);
        (await context.Captures.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_QueueItemDeleted_AcknowledgesDuplicateWithoutResurrecting()
    {
        // Arrange - ingest, then the user removes the queue item (queue removal
        // does not delete the capture row)
        var audioPath = CreateTempAudioFile(out var content);
        var envelope = CreateValidEnvelope(audioPath, content);
        var first = await _service.IngestRemoteCaptureAsync(envelope);
        first.Success.Should().BeTrue();

        await using (var setup = _contextFactory.CreateDbContext())
        {
            var queueRow = await setup.RecordingQueues.SingleAsync();
            setup.RecordingQueues.Remove(queueRow);
            await setup.SaveChangesAsync();
        }

        // Act - the device re-sends the same capture
        var second = await _service.IngestRemoteCaptureAsync(envelope);

        // Assert - acknowledged as duplicate (NOT a retryable failure), and the
        // deletion is not silently resurrected (§12.2)
        second.Success.Should().BeTrue();
        second.AlreadyIngested.Should().BeTrue();
        second.QueueId.Should().BeNull();

        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.CountAsync()).Should().Be(0);
        (await context.Captures.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_SameCaptureIdDifferentContent_FailsAsConflict()
    {
        // Arrange - same capture ID declared with different bytes is a conflict,
        // not an idempotent replay
        var firstPath = CreateTempAudioFile(out var firstContent);
        var envelope = CreateValidEnvelope(firstPath, firstContent);
        (await _service.IngestRemoteCaptureAsync(envelope)).Success.Should().BeTrue();

        var conflictingPath = CreateTempAudioFile(out var conflictingContent);
        var conflicting = CreateValidEnvelope(conflictingPath, conflictingContent);
        conflicting.CaptureId = envelope.CaptureId;

        // Act
        var result = await _service.IngestRemoteCaptureAsync(conflicting);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(IngestionFailureKind.ArtifactInvalid);

        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.CountAsync()).Should().Be(1);
        (await context.Captures.CountAsync()).Should().Be(1);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    #region Helpers

    /// <summary>Writes a small random "audio" file into the temp directory and tracks it for cleanup.</summary>
    private string CreateTempAudioFile(out byte[] content)
    {
        content = new byte[512];
        Random.Shared.NextBytes(content);
        return CreateTempAudioFileWithContent(content);
    }

    /// <summary>
    /// Same service over the same database, plus the real capture status
    /// publisher (an optional dependency: absent in the shared instance above).
    /// </summary>
    private CaptureIngestionService CreateServiceWithStatusPublisher() => new(
        new CaptureRepository(_contextFactory),
        new RecordingQueueRepository(_contextFactory),
        new LocalFileArtifactResolver(),
        NullLogger<CaptureIngestionService>.Instance,
        new CaptureStatusPublisher(
            new SyncOutboxRepository(_contextFactory),
            new PendingEventRepository(_contextFactory),
            NullLogger<CaptureStatusPublisher>.Instance));

    /// <summary>Writes the given bytes to a fresh temp "audio" file and tracks it for cleanup.</summary>
    private string CreateTempAudioFileWithContent(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_ingest_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Independent hash computation the service's result is checked against.</summary>
    private static string ComputeSha256LowercaseHex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static RemoteCaptureEnvelope CreateValidEnvelope(string audioPath, byte[] content) => new()
    {
        CaptureId = Guid.NewGuid().ToString(),
        SourcePlatform = CapturePlatform.Ios,
        CaptureType = CaptureTypes.TripLog,
        CapturedAtUtc = DateTime.UtcNow,
        OriginalFileName = "roadtrip-clip.m4a",
        MediaType = "audio/mp4",
        ExpectedByteLength = content.Length,
        ExpectedSha256 = ComputeSha256LowercaseHex(content),
        StorageLocator = audioPath
    };

    /// <summary>Asserts a failed ingestion left all four tables untouched.</summary>
    private async Task AssertNoRowsWrittenAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        (await context.Captures.CountAsync()).Should().Be(0);
        (await context.CaptureArtifacts.CountAsync()).Should().Be(0);
        (await context.RecordingQueues.CountAsync()).Should().Be(0);
        (await context.SyncOutboxEntries.CountAsync()).Should().Be(0);
    }

    #endregion
}
