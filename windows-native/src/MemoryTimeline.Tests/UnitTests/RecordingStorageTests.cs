using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// <see cref="RecordingStorage"/>: removing a queue row releases the recording
/// files it owned unless another row still references them, drops the
/// capture's artifact rows and keeps the capture tombstone; the Clear Cache
/// sweep collects files under the recording trees that no row references,
/// outside the grace window. EF InMemory plus a temp directory standing in for
/// the three recording trees.
/// </summary>
public class RecordingStorageTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly string _root;
    private readonly RecordingStoragePaths _paths;
    private readonly RecordingStorage _storage;

    public RecordingStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RecordingStorage_{Guid.NewGuid():N}");
        _paths = new RecordingStoragePaths(Path.Combine(_root, "app"));
        _storage = new RecordingStorage(_contextFactory, NullLogger<RecordingStorage>.Instance, _paths);
    }

    #region ReleaseAsync

    [Fact]
    public async Task ReleaseAsync_UnreferencedFiles_DeletesFilesAndArtifactRowsKeepsCaptureRow()
    {
        // Arrange - a synced capture: the downloaded .m4a plus the normalized WAV
        var original = WriteFile(_paths.SyncedAudioRoot, "cap-1_art-1.m4a", 300);
        var normalized = WriteFile(_paths.NormalizedAudioRoot, "cap-1_art-1.wav", 500);
        var item = await SeedIngestedItemAsync("queue-1", "cap-1", original, normalized);
        await RemoveQueueRowAsync(item.QueueId);

        // Act
        var result = await _storage.ReleaseAsync(item);

        // Assert
        result.FilesDeleted.Should().Be(2);
        result.BytesFreed.Should().Be(800);
        result.FilesKept.Should().Be(0);
        result.ArtifactRowsDeleted.Should().Be(2);
        File.Exists(original).Should().BeFalse();
        File.Exists(normalized).Should().BeFalse();

        await using var context = _contextFactory.CreateDbContext();
        (await context.CaptureArtifacts.CountAsync()).Should().Be(0);
        (await context.Captures.CountAsync(c => c.CaptureId == "cap-1")).Should().Be(1,
            "the capture row is the idempotency tombstone that stops a re-upload from resurrecting the recording");
    }

    [Fact]
    public async Task ReleaseAsync_FileStillPlayedByAnApprovedEvent_IsKept()
    {
        // Arrange - the user approved an event from this recording; the event
        // keeps the playback path
        var original = WriteFile(_paths.SyncedAudioRoot, "cap-2_art-2.m4a", 300);
        var normalized = WriteFile(_paths.NormalizedAudioRoot, "cap-2_art-2.wav", 500);
        var item = await SeedIngestedItemAsync("queue-2", "cap-2", original, normalized);
        await using (var context = _contextFactory.CreateDbContext())
        {
            context.Events.Add(new Event
            {
                EventId = "evt-2",
                Title = "Approved memory",
                StartDate = new DateTime(2024, 1, 1),
                Category = EventCategory.Other,
                AudioFilePath = normalized,
            });
            await context.SaveChangesAsync();
        }

        await RemoveQueueRowAsync(item.QueueId);

        // Act
        var result = await _storage.ReleaseAsync(item);

        // Assert - the WAV the event plays survives; the original does not
        result.FilesDeleted.Should().Be(1);
        result.FilesKept.Should().Be(1);
        File.Exists(normalized).Should().BeTrue();
        File.Exists(original).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_FileSharedWithAnotherQueueItem_IsKept()
    {
        // Arrange - two queue rows point at the same WAV (an import de-duplicated
        // by content, say)
        var wav = WriteFile(_paths.RecordingsRoot, "shared.wav", 400);
        var item = await SeedIngestedItemAsync("queue-3", "cap-3", wav, normalizedPath: null);
        await using (var context = _contextFactory.CreateDbContext())
        {
            context.RecordingQueues.Add(new RecordingQueue { QueueId = "queue-other", AudioFilePath = wav, Status = QueueStatus.Pending });
            await context.SaveChangesAsync();
        }

        await RemoveQueueRowAsync(item.QueueId);

        // Act
        var result = await _storage.ReleaseAsync(item);

        // Assert
        result.FilesDeleted.Should().Be(0);
        result.FilesKept.Should().Be(1);
        File.Exists(wav).Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseAsync_FileReferencedByAPendingEvent_IsKept()
    {
        var wav = WriteFile(_paths.RecordingsRoot, "pending.wav", 400);
        var item = await SeedIngestedItemAsync("queue-4", "cap-4", wav, normalizedPath: null);
        await using (var context = _contextFactory.CreateDbContext())
        {
            context.PendingEvents.Add(new PendingEvent
            {
                PendingId = "pend-4",
                Title = "Still to review",
                StartDate = new DateTime(2024, 1, 1),
                AudioFilePath = wav,
            });
            await context.SaveChangesAsync();
        }

        await RemoveQueueRowAsync(item.QueueId);

        var result = await _storage.ReleaseAsync(item);

        result.FilesKept.Should().Be(1);
        File.Exists(wav).Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseAsync_FileOutsideTheManagedTrees_IsNeverDeleted()
    {
        // Arrange - a path a caller handed to ingestion from its own folder
        var elsewhere = WriteFile(Path.Combine(_root, "users-own-folder"), "mine.wav", 100);
        var item = await SeedIngestedItemAsync("queue-5", "cap-5", elsewhere, normalizedPath: null);
        await RemoveQueueRowAsync(item.QueueId);

        // Act
        var result = await _storage.ReleaseAsync(item);

        // Assert
        result.FilesDeleted.Should().Be(0);
        result.FilesKept.Should().Be(1);
        File.Exists(elsewhere).Should().BeTrue();
        result.ArtifactRowsDeleted.Should().Be(1, "the rows go even when the file is the caller's");
    }

    [Fact]
    public async Task ReleaseAsync_TextItem_DoesNothing()
    {
        var item = new RecordingQueue { QueueId = "text-1", AudioFilePath = string.Empty, SourceType = QueueSourceType.Text };

        var result = await _storage.ReleaseAsync(item);

        result.Should().Be(new RecordingReleaseResult(0, 0, 0, 0));
    }

    #endregion

    #region CleanupOrphansAsync

    [Fact]
    public async Task CleanupOrphansAsync_DeletesUnreferencedFilesInEveryTree_AndKeepsReferencedOnes()
    {
        // Arrange - one referenced file per reference kind, one orphan per tree
        var keptByQueue = WriteFile(_paths.RecordingsRoot, "kept-queue.wav", 10);
        var keptByEvent = WriteFile(_paths.NormalizedAudioRoot, "kept-event.wav", 10);
        var keptByPending = WriteFile(_paths.RecordingsRoot, "kept-pending.wav", 10);
        var keptByArtifact = WriteFile(_paths.SyncedAudioRoot, "kept-artifact.m4a", 10);
        var orphanRecording = WriteFile(_paths.RecordingsRoot, "orphan.wav", 100);
        var orphanSynced = WriteFile(_paths.SyncedAudioRoot, "orphan.m4a", 200);
        var orphanNormalized = WriteFile(_paths.NormalizedAudioRoot, "orphan.wav", 300);
        foreach (var file in new[] { keptByQueue, keptByEvent, keptByPending, keptByArtifact, orphanRecording, orphanSynced, orphanNormalized })
        {
            Age(file);
        }

        await using (var context = _contextFactory.CreateDbContext())
        {
            context.RecordingQueues.Add(new RecordingQueue { QueueId = "q", AudioFilePath = keptByQueue, Status = QueueStatus.Pending });
            context.Events.Add(new Event { EventId = "e", Title = "E", StartDate = new DateTime(2024, 1, 1), Category = EventCategory.Other, AudioFilePath = keptByEvent });
            context.PendingEvents.Add(new PendingEvent { PendingId = "p", Title = "P", StartDate = new DateTime(2024, 1, 1), AudioFilePath = keptByPending });
            context.Captures.Add(new Capture { CaptureId = "c", SourcePlatform = CapturePlatform.Ios, CaptureType = CaptureTypes.Memory, Status = CaptureStatus.Received });
            context.CaptureArtifacts.Add(new CaptureArtifact { ArtifactId = "a", CaptureId = "c", ArtifactType = CaptureArtifactType.AudioOriginal, StorageLocator = keptByArtifact });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _storage.CleanupOrphansAsync();

        // Assert
        result.FilesDeleted.Should().Be(3);
        result.BytesFreed.Should().Be(600);
        File.Exists(orphanRecording).Should().BeFalse();
        File.Exists(orphanSynced).Should().BeFalse();
        File.Exists(orphanNormalized).Should().BeFalse();
        foreach (var kept in new[] { keptByQueue, keptByEvent, keptByPending, keptByArtifact })
        {
            File.Exists(kept).Should().BeTrue(kept);
        }
    }

    [Fact]
    public async Task CleanupOrphansAsync_FreshFile_IsLeftForALaterRun()
    {
        // Arrange - an orphan written moments ago: its queue row may still be
        // on its way (the recorder writes before ingestion commits)
        var fresh = WriteFile(_paths.RecordingsRoot, "just-recorded.wav", 100);

        // Act
        var result = await _storage.CleanupOrphansAsync();

        // Assert
        result.FilesDeleted.Should().Be(0);
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupOrphansAsync_NoTrees_IsANoOp()
    {
        var result = await _storage.CleanupOrphansAsync();

        result.Should().Be(new RecordingCleanupResult(0, 0));
    }

    #endregion

    #region Helpers

    private async Task<RecordingQueue> SeedIngestedItemAsync(string queueId, string captureId, string originalPath, string? normalizedPath)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Captures.Add(new Capture
        {
            CaptureId = captureId,
            SourcePlatform = CapturePlatform.Ios,
            CaptureType = CaptureTypes.Memory,
            Status = CaptureStatus.Received,
        });
        context.CaptureArtifacts.Add(new CaptureArtifact
        {
            ArtifactId = $"{captureId}-orig",
            CaptureId = captureId,
            ArtifactType = CaptureArtifactType.AudioOriginal,
            StorageLocator = originalPath,
            MediaType = "audio/mp4",
        });
        if (normalizedPath != null)
        {
            context.CaptureArtifacts.Add(new CaptureArtifact
            {
                ArtifactId = $"{captureId}-norm",
                CaptureId = captureId,
                ArtifactType = CaptureArtifactType.AudioNormalized,
                StorageLocator = normalizedPath,
                MediaType = "audio/wav",
            });
        }

        var item = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = normalizedPath ?? originalPath,
            Status = QueueStatus.Completed,
            SourceCaptureId = captureId,
            SourcePlatform = CapturePlatform.Ios,
            AudioArtifactId = $"{captureId}-orig",
        };
        context.RecordingQueues.Add(item);
        await context.SaveChangesAsync();
        return item;
    }

    private async Task RemoveQueueRowAsync(string queueId)
    {
        await using var context = _contextFactory.CreateDbContext();
        var row = await context.RecordingQueues.SingleAsync(q => q.QueueId == queueId);
        context.RecordingQueues.Remove(row);
        await context.SaveChangesAsync();
    }

    private static string WriteFile(string directory, string name, int bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void Age(string path) =>
        File.SetCreationTimeUtc(path, DateTime.UtcNow - RecordingStorage.CleanupGraceWindow - TimeSpan.FromMinutes(5));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    #endregion
}
