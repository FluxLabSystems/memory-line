using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for the text/paste capture path through
/// <see cref="EventExtractionService.ProcessRecordingAsync"/>: Text sources
/// must skip speech-to-text entirely, and Audio sources must persist their
/// Whisper transcript on the recording_queue row so retries never re-run STT.
/// Hybrid style (like RagServiceTests): REAL RecordingQueueRepository over the
/// shared in-memory factory; STT/LLM/settings/event/person services are mocks.
/// </summary>
public class TextExtractionTests : IDisposable
{
    private const string PastedText = "In March 2014 I started my first job at Contoso in Seattle.";
    private const string WhisperText = "Yesterday I finished the marathon in Boston.";

    private readonly TestDbContextFactory _contextFactory;
    private readonly RecordingQueueRepository _queueRepository;
    private readonly Mock<ILlmService> _llmMock;
    private readonly Mock<ISpeechToTextService> _sttMock;
    private readonly EventExtractionService _extractionService;

    public TextExtractionTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _queueRepository = new RecordingQueueRepository(_contextFactory);

        _llmMock = new Mock<ILlmService>();
        _llmMock
            .Setup(l => l.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventExtractionResult
            {
                Success = true,
                OverallConfidence = 0.9,
                Events = new List<ExtractedEvent>
                {
                    new()
                    {
                        Title = "Extracted event",
                        StartDate = new DateTime(2014, 3, 10),
                        Category = "Work",
                        Confidence = 0.85
                    }
                }
            });

        _sttMock = new Mock<ISpeechToTextService>();

        // API key present so the pre-flight check passes.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock
            .Setup(s => s.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, It.IsAny<string>()))
            .ReturnsAsync("test-api-key");

        var eventServiceMock = new Mock<IEventService>();
        eventServiceMock
            .Setup(s => s.GetRecentEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(Enumerable.Empty<Event>());

        var personServiceMock = new Mock<IPersonService>();
        personServiceMock
            .Setup(p => p.GetAllPersonsAsync(It.IsAny<PersonSortOption>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<PersonDto>());

        _extractionService = new EventExtractionService(
            _llmMock.Object,
            _sttMock.Object,
            eventServiceMock.Object,
            settingsMock.Object,
            _queueRepository,
            personServiceMock.Object,
            _contextFactory,
            new Mock<ILogger<EventExtractionService>>().Object);
    }

    [Fact]
    public async Task ProcessRecordingAsync_TextSource_SkipsSpeechToTextAndCreatesPendingEvents()
    {
        // Arrange — a pasted-text queue row (no audio file at all)
        await _queueRepository.AddAsync(new RecordingQueue
        {
            QueueId = "text-queue-1",
            SourceType = QueueSourceType.Text,
            SourceLabel = "Journal 2014-03",
            Transcript = PastedText,
            AudioFilePath = string.Empty,
            Status = QueueStatus.Pending
        });
        var progress = new ImmediateProgress<(int, string)>();

        // Act
        var count = await _extractionService.ProcessRecordingAsync("text-queue-1", progress);

        // Assert — STT never ran, the stored text went straight to the LLM
        count.Should().Be(1);
        _sttMock.Verify(s => s.TranscribeAsync(It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _sttMock.Verify(s => s.TranscribeAsync(It.IsAny<string>(), It.IsAny<IProgress<double>?>()), Times.Never);
        _llmMock.Verify(l => l.ExtractEventsAsync(PastedText, It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        progress.Reports.Should().Contain(r => r.Item2 == "Using saved text...");

        // Pending event persisted with the text and NO phantom audio path
        await using var verifyContext = _contextFactory.CreateDbContext();
        var pending = await verifyContext.PendingEvents.AsNoTracking().SingleAsync();
        pending.QueueId.Should().Be("text-queue-1");
        pending.Title.Should().Be("Extracted event");
        pending.Transcript.Should().Be(PastedText);
        pending.AudioFilePath.Should().BeNull("the empty audio path convention must not leak onto events");
        pending.Status.Should().Be(PendingStatus.PendingReview.ToStringValue());
        pending.IsApproved.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessRecordingAsync_TextSourceWithoutStoredText_ThrowsWithoutCallingStt()
    {
        // Arrange — a corrupt text row with no stored content
        await _queueRepository.AddAsync(new RecordingQueue
        {
            QueueId = "text-queue-empty",
            SourceType = QueueSourceType.Text,
            Transcript = null,
            AudioFilePath = string.Empty,
            Status = QueueStatus.Pending
        });

        // Act
        var act = () => _extractionService.ProcessRecordingAsync("text-queue-empty");

        // Assert
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*has no stored text*");
        _sttMock.Verify(s => s.TranscribeAsync(It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _llmMock.Verify(l => l.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRecordingAsync_AudioSource_PersistsTranscriptAndSkipsSttOnSecondRun()
    {
        // Arrange — a classic audio recording
        const string audioPath = @"C:\audio\recording.wav";
        await _queueRepository.AddAsync(new RecordingQueue
        {
            QueueId = "audio-queue-1",
            SourceType = QueueSourceType.Audio,
            AudioFilePath = audioPath,
            Status = QueueStatus.Pending
        });
        _sttMock
            .Setup(s => s.TranscribeAsync(audioPath, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Success = true, Text = WhisperText });

        // Act — first attempt transcribes and must persist the transcript
        var firstCount = await _extractionService.ProcessRecordingAsync("audio-queue-1");

        // Assert — transcript cached on the recording_queue row
        firstCount.Should().Be(1);
        await using (var verifyContext = _contextFactory.CreateDbContext())
        {
            var row = await verifyContext.RecordingQueues.AsNoTracking()
                .SingleAsync(q => q.QueueId == "audio-queue-1");
            row.Transcript.Should().Be(WhisperText);
            row.SourceType.Should().Be(QueueSourceType.Audio);
        }

        // Act — second attempt (the retry path re-enters ProcessRecordingAsync)
        var progress = new ImmediateProgress<(int, string)>();
        var secondCount = await _extractionService.ProcessRecordingAsync("audio-queue-1", progress);

        // Assert — Whisper ran exactly ONCE across both runs; the retry reused
        // the persisted transcript and still reached extraction
        secondCount.Should().Be(1);
        _sttMock.Verify(s => s.TranscribeAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _llmMock.Verify(l => l.ExtractEventsAsync(WhisperText, It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        progress.Reports.Should().Contain(r => r.Item2 == "Using saved transcript...");
    }

    [Fact]
    public async Task ProcessRecordingAsync_ForwardsTheCancellationTokenToSpeechToTextAndExtraction()
    {
        // Arrange — an audio source; the caller's token must reach both calls
        const string audioPath = @"C:\audio\cancel.wav";
        await _queueRepository.AddAsync(new RecordingQueue
        {
            QueueId = "audio-cancel",
            SourceType = QueueSourceType.Audio,
            AudioFilePath = audioPath,
            Status = QueueStatus.Pending
        });
        using var cts = new CancellationTokenSource();
        _sttMock
            .Setup(s => s.TranscribeAsync(audioPath, null, cts.Token))
            .ReturnsAsync(new TranscriptionResult { Success = true, Text = WhisperText });

        // Act
        var count = await _extractionService.ProcessRecordingAsync("audio-cancel", null, cts.Token);

        // Assert
        count.Should().Be(1);
        _sttMock.Verify(s => s.TranscribeAsync(audioPath, null, cts.Token), Times.Once);
        _llmMock.Verify(l => l.ExtractEventsAsync(WhisperText, It.IsAny<ExtractionContext?>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task ProcessRecordingAsync_CancelledDuringTranscription_PropagatesWithoutExtracting()
    {
        // Arrange — Whisper observes the cancellation mid-decode
        const string audioPath = @"C:\audio\cancel-mid.wav";
        await _queueRepository.AddAsync(new RecordingQueue
        {
            QueueId = "audio-cancel-mid",
            SourceType = QueueSourceType.Audio,
            AudioFilePath = audioPath,
            Status = QueueStatus.Pending
        });
        using var cts = new CancellationTokenSource();
        _sttMock
            .Setup(s => s.TranscribeAsync(audioPath, null, It.IsAny<CancellationToken>()))
            .Returns<string, IProgress<double>?, CancellationToken>((_, _, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new TranscriptionResult());
            });

        // Act
        var act = () => _extractionService.ProcessRecordingAsync("audio-cancel-mid", null, cts.Token);

        // Assert — cancellation propagates as itself and the LLM is never called
        await act.Should().ThrowAsync<OperationCanceledException>();
        _llmMock.Verify(
            l => l.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        using var context = _contextFactory.CreateDbContext();
        context.Database.EnsureDeleted();
    }

    /// <summary>
    /// IProgress implementation that records reports synchronously —
    /// Progress&lt;T&gt; posts through a SynchronizationContext and would race the asserts.
    /// </summary>
    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();

        public void Report(T value) => Reports.Add(value);
    }
}
