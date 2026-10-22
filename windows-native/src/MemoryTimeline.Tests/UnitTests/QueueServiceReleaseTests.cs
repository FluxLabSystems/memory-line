using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Removing or clearing queue rows used to leave their recording files on
/// disk. <see cref="QueueService"/> now hands each removed row to
/// <see cref="IRecordingStorage"/>, after the row delete and without letting a
/// release failure fail the removal.
/// </summary>
public class QueueServiceReleaseTests
{
    private readonly Mock<IRecordingQueueRepository> _repository = new();
    private readonly Mock<IRecordingStorage> _storage = new();
    private readonly List<string> _sequence = new();

    private QueueService CreateService() => new(
        _repository.Object,
        new Mock<IEventExtractionService>().Object,
        NullLogger<QueueService>.Instance,
        new Mock<INotificationService>().Object,
        statusPublisher: null,
        recordingStorage: _storage.Object);

    [Fact]
    public async Task RemoveQueueItemAsync_ReleasesTheRowsFilesAfterTheDelete()
    {
        // Arrange
        var item = new RecordingQueue { QueueId = "q-1", AudioFilePath = "/tmp/q-1.wav", Status = QueueStatus.Completed };
        _repository.Setup(r => r.GetByIdAsync("q-1", It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _repository.Setup(r => r.DeleteAsync(item, It.IsAny<CancellationToken>())).Callback(() => _sequence.Add("delete")).Returns(Task.CompletedTask);
        _storage.Setup(s => s.ReleaseAsync(item, It.IsAny<CancellationToken>()))
            .Callback(() => _sequence.Add("release"))
            .ReturnsAsync(new RecordingReleaseResult(1, 100, 0, 1));

        // Act
        await CreateService().RemoveQueueItemAsync("q-1");

        // Assert
        _sequence.Should().Equal("delete", "release");
        _storage.Verify(s => s.ReleaseAsync(item, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveQueueItemAsync_UnknownItem_ReleasesNothing()
    {
        _repository.Setup(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((RecordingQueue?)null);

        await CreateService().RemoveQueueItemAsync("missing");

        _storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveQueueItemAsync_ReleaseThrows_RemovalStillSucceeds()
    {
        // Arrange
        var item = new RecordingQueue { QueueId = "q-2", AudioFilePath = "/tmp/q-2.wav" };
        _repository.Setup(r => r.GetByIdAsync("q-2", It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _repository.Setup(r => r.DeleteAsync(item, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _storage.Setup(s => s.ReleaseAsync(item, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("file locked"));

        // Act
        var act = () => CreateService().RemoveQueueItemAsync("q-2");

        // Assert - the row is gone; the file is the cache sweep's problem
        await act.Should().NotThrowAsync();
        _repository.Verify(r => r.DeleteAsync(item, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearCompletedItemsAsync_ReleasesEveryClearedRow()
    {
        // Arrange
        var items = new[]
        {
            new RecordingQueue { QueueId = "c-1", AudioFilePath = "/tmp/c-1.wav", Status = QueueStatus.Completed },
            new RecordingQueue { QueueId = "c-2", AudioFilePath = string.Empty, SourceType = QueueSourceType.Text, Status = QueueStatus.Completed },
        };
        _repository.Setup(r => r.GetByStatusAsync(QueueStatus.Completed, It.IsAny<CancellationToken>())).ReturnsAsync(items);
        _repository.Setup(r => r.DeleteAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _storage.Setup(s => s.ReleaseAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordingReleaseResult(0, 0, 0, 0));

        // Act
        await CreateService().ClearCompletedItemsAsync();

        // Assert - the storage decides what a text row owns (nothing); the
        // service hands over every row it removed
        _storage.Verify(s => s.ReleaseAsync(items[0], It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(s => s.ReleaseAsync(items[1], It.IsAny<CancellationToken>()), Times.Once);
    }
}
