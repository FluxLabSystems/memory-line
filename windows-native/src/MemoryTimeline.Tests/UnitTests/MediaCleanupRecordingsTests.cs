using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Settings "Clear Cache" calls <see cref="MediaService.CleanupOrphansAsync"/>
/// and used to scan only the media tree. With an <see cref="IRecordingStorage"/>
/// registered it also sweeps the recording trees and reports those files in the
/// same result — even when no media tree exists yet.
/// </summary>
public class MediaCleanupRecordingsTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly string _root;
    private readonly RecordingStoragePaths _paths;
    private readonly MediaService _mediaService;

    public MediaCleanupRecordingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"MediaCleanupRecordings_{Guid.NewGuid():N}");
        _paths = new RecordingStoragePaths(Path.Combine(_root, "app"));
        _mediaService = new MediaService(
            new EventMediaRepository(_contextFactory),
            _contextFactory,
            new Mock<IThumbnailGenerator>().Object,
            NullLogger<MediaService>.Instance,
            mediaRootOverride: Path.Combine(_root, "media"), // never created
            projectionPublisher: null,
            recordingStorage: new RecordingStorage(_contextFactory, NullLogger<RecordingStorage>.Instance, _paths));
    }

    [Fact]
    public async Task CleanupOrphansAsync_NoMediaTree_StillSweepsOrphanedRecordings()
    {
        // Arrange - two aged orphans under the recording trees, no media root
        var orphan1 = WriteAgedFile(_paths.RecordingsRoot, "old-take.wav", 120);
        var orphan2 = WriteAgedFile(_paths.SyncedAudioRoot, "dedupe-leftover.m4a", 80);

        // Act
        var result = await _mediaService.CleanupOrphansAsync();

        // Assert
        result.RecordingsDeleted.Should().Be(2);
        result.FilesDeleted.Should().Be(2);
        result.BytesFreed.Should().Be(200);
        result.MediaFilesDeleted.Should().Be(0);
        File.Exists(orphan1).Should().BeFalse();
        File.Exists(orphan2).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupOrphansAsync_WithoutRecordingStorage_ReportsNoRecordings()
    {
        var mediaOnly = new MediaService(
            new EventMediaRepository(_contextFactory),
            _contextFactory,
            new Mock<IThumbnailGenerator>().Object,
            NullLogger<MediaService>.Instance,
            mediaRootOverride: Path.Combine(_root, "media"));
        WriteAgedFile(_paths.RecordingsRoot, "untouched.wav", 50);

        var result = await mediaOnly.CleanupOrphansAsync();

        result.RecordingsDeleted.Should().Be(0);
        result.FilesDeleted.Should().Be(0);
        File.Exists(Path.Combine(_paths.RecordingsRoot, "untouched.wav")).Should().BeTrue();
    }

    private static string WriteAgedFile(string directory, string name, int bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow - RecordingStorage.CleanupGraceWindow - TimeSpan.FromMinutes(5));
        return path;
    }

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
}
