using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="QueueService.EnqueueTextAsync"/> — the text/paste
/// capture path. Follows the full-mock style of <see cref="QueueServiceTests"/>:
/// all collaborators are Moq mocks and the created row is inspected via the
/// repository's AddAsync argument.
/// </summary>
public class TextQueueServiceTests
{
    private readonly Mock<IRecordingQueueRepository> _repositoryMock;
    private readonly Mock<IEventExtractionService> _extractionServiceMock;
    private readonly Mock<INotificationService> _notificationServiceMock;
    private readonly Mock<ILogger<QueueService>> _loggerMock;
    private readonly QueueService _queueService;

    public TextQueueServiceTests()
    {
        _repositoryMock = new Mock<IRecordingQueueRepository>();
        _extractionServiceMock = new Mock<IEventExtractionService>();
        _notificationServiceMock = new Mock<INotificationService>();
        _loggerMock = new Mock<ILogger<QueueService>>();

        // Echo the entity back so the service returns the stored row's id.
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecordingQueue q, CancellationToken _) => q);

        _queueService = new QueueService(
            _repositoryMock.Object,
            _extractionServiceMock.Object,
            _loggerMock.Object,
            _notificationServiceMock.Object);
    }

    [Fact]
    public async Task EnqueueTextAsync_ValidText_CreatesPendingTextSourceRow()
    {
        // Arrange — multibyte characters prove the size is a UTF-8 BYTE count
        var text = "Ma journée à Zürich — I met François for coffee.";
        RecordingQueue? captured = null;
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync((RecordingQueue q, CancellationToken _) => q);

        // Act
        var queueId = await _queueService.EnqueueTextAsync(text, "Journal 2014-03");

        // Assert
        queueId.Should().NotBeNullOrWhiteSpace();
        captured.Should().NotBeNull();
        captured!.QueueId.Should().Be(queueId);
        captured.SourceType.Should().Be(QueueSourceType.Text);
        captured.SourceLabel.Should().Be("Journal 2014-03");
        captured.Transcript.Should().Be(text);
        captured.AudioFilePath.Should().Be(string.Empty, "text sources store an empty path in the NOT NULL column");
        captured.Status.Should().Be(QueueStatus.Pending);
        captured.DurationSeconds.Should().BeNull();
        captured.FileSizeBytes.Should().Be(Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public async Task EnqueueTextAsync_ValidText_RaisesPendingStatusChangedEvent()
    {
        // Arrange
        QueueItemStatusChangedEventArgs? raised = null;
        _queueService.QueueItemStatusChanged += (_, args) => raised = args;

        // Act
        var queueId = await _queueService.EnqueueTextAsync("Some captured text");

        // Assert
        raised.Should().NotBeNull();
        raised!.QueueId.Should().Be(queueId);
        raised.NewStatus.Should().Be(QueueStatus.Pending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task EnqueueTextAsync_NullOrWhitespaceText_ThrowsArgumentException(string? text)
    {
        // Act
        var act = () => _queueService.EnqueueTextAsync(text!);

        // Assert — clear rejection, nothing written
        await act.Should().ThrowAsync<ArgumentException>();
        _repositoryMock.Verify(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueTextAsync_LongLabel_TrimsAndClipsTo200Characters()
    {
        // Arrange
        RecordingQueue? captured = null;
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync((RecordingQueue q, CancellationToken _) => q);
        var longLabel = "  " + new string('x', 250) + "  ";

        // Act
        await _queueService.EnqueueTextAsync("Some text", longLabel);

        // Assert — trimmed first, then clipped to the column's 200-char max
        captured.Should().NotBeNull();
        captured!.SourceLabel.Should().HaveLength(200);
        captured.SourceLabel.Should().Be(new string('x', 200));
    }

    [Fact]
    public async Task EnqueueTextAsync_BlankLabel_StoresNullLabel()
    {
        // Arrange
        RecordingQueue? captured = null;
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync((RecordingQueue q, CancellationToken _) => q);

        // Act
        await _queueService.EnqueueTextAsync("Some text", "   ");

        // Assert
        captured.Should().NotBeNull();
        captured!.SourceLabel.Should().BeNull();
    }
}
