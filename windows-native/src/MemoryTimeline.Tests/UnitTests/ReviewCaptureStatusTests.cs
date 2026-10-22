using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// What the review path publishes (design §19 Phase 3). Two projections leave
/// Windows when an extraction is created or decided, and nothing else emits
/// either of them:
///
/// <list type="bullet">
/// <item><b>Capture status.</b> Approving or rejecting must republish the
/// capture's status, because <see cref="QueueService"/> only publishes on a
/// stage transition and review never causes one — so without this the phone's
/// review counts freeze at extraction time and a capture reads "Ready for
/// review" forever, even after the user finished reviewing on the PC.</item>
///
/// <item><b>Timeline projections.</b> The status says how many events are
/// waiting; only the <c>pending_event</c> projection says what they are, and
/// only the <c>event</c> projection puts an approved one on the companion's
/// timeline. An approval publishes both — the new event, and a tombstone that
/// takes the item out of the review queue — because watching a memory leave the
/// queue and never arrive anywhere reads as data loss.</item>
/// </list>
///
/// A file-based SQLite database is used because approval opens a relational
/// transaction. Both publishers are optional dependencies exactly like
/// <see cref="QueueService"/>'s, so the no-publisher paths are covered too.
/// </summary>
public class ReviewCaptureStatusTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly RecordingQueueRepository _queueRepository;
    private readonly Mock<ICaptureStatusPublisher> _statusPublisher = new();
    private readonly Mock<ITimelineProjectionPublisher> _projectionPublisher = new();
    private readonly Mock<ILlmService> _llmService = new();
    private readonly List<RecordingQueue> _published = new();
    private readonly List<string> _projected = new();
    private readonly EventExtractionService _extractionService;
    private readonly string _databasePath;

    public ReviewCaptureStatusTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(), $"ReviewCaptureStatusTests_{Guid.NewGuid()}.db");
        _contextFactory = TestDbContextFactory.CreateSqliteFile(_databasePath);

        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _queueRepository = new RecordingQueueRepository(_contextFactory);

        // Snapshot each publish: the service hands over the live queue row, so
        // the assertions need the values as they were at publish time.
        _statusPublisher
            .Setup(p => p.PublishAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingQueue, CancellationToken>((item, _) => _published.Add(new RecordingQueue
            {
                QueueId = item.QueueId,
                Status = item.Status,
                ProcessingStage = item.ProcessingStage,
                SourceCaptureId = item.SourceCaptureId,
            }))
            .Returns(Task.CompletedTask);

        // Recorded as strings in one list because the ORDER matters as much as
        // the contents: an approval's event upsert has to reach a companion
        // before the people it names, and both before the tombstone that empties
        // the queue entry.
        _projectionPublisher
            .Setup(p => p.PublishPendingEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) => _projected.Add($"pending_event:{id}"))
            .Returns(Task.CompletedTask);
        _projectionPublisher
            .Setup(p => p.PublishEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) => _projected.Add($"event:{id}"))
            .Returns(Task.CompletedTask);
        _projectionPublisher
            .Setup(p => p.PublishPersonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) => _projected.Add($"person:{id}"))
            .Returns(Task.CompletedTask);
        _projectionPublisher
            .Setup(p => p.PublishDeletedAsync(
                It.IsAny<TimelineProjectionEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<TimelineProjectionEntity, string, CancellationToken>(
                (entity, id, _) => _projected.Add($"delete:{entity}:{id}"))
            .Returns(Task.CompletedTask);

        _extractionService = BuildService(_statusPublisher.Object, _projectionPublisher.Object);
    }

    [Fact]
    public async Task ApprovePendingEventAsync_LastPendingEventApproved_CompletesTheQueueItemAndPublishes()
    {
        // Arrange - one capture waiting on its only extracted event
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the island");

        // Act
        await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert - the review is over, so the capture is done
        _published.Should().ContainSingle();
        _published[0].QueueId.Should().Be(queueId);
        _published[0].ProcessingStage.Should().Be(QueueProcessingStage.Completed);
        _published[0].Status.Should().Be(QueueStatus.Completed);

        var stored = await _queueRepository.GetByIdAsync(queueId);
        stored!.ProcessingStage.Should().Be(QueueProcessingStage.Completed);
        stored.Status.Should().Be(QueueStatus.Completed);
        stored.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ApprovePendingEventAsync_PendingEventsRemain_PublishesWithoutCompleting()
    {
        // Arrange - two events, only one approved
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var first = await SeedPendingEventAsync(queueId, "Ferry to the island");
        await SeedPendingEventAsync(queueId, "Dinner at the harbour");

        // Act
        await _extractionService.ApprovePendingEventAsync(first);

        // Assert - the counts moved, but the review is not over
        _published.Should().ContainSingle();
        _published[0].ProcessingStage.Should().Be(QueueProcessingStage.ReviewReady);

        var stored = await _queueRepository.GetByIdAsync(queueId);
        stored!.ProcessingStage.Should().Be(QueueProcessingStage.ReviewReady);
    }

    [Fact]
    public async Task RejectPendingEventAsync_LastPendingEventRejected_CompletesTheQueueItemAndPublishes()
    {
        // Arrange - rejecting the last event finishes the review just as
        // approving it does
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Nothing worth keeping");

        // Act
        await _extractionService.RejectPendingEventAsync(pendingId);

        // Assert
        _published.Should().ContainSingle();
        _published[0].ProcessingStage.Should().Be(QueueProcessingStage.Completed);

        var stored = await _queueRepository.GetByIdAsync(queueId);
        stored!.ProcessingStage.Should().Be(QueueProcessingStage.Completed);
    }

    [Fact]
    public async Task RejectPendingEventAsync_EventsRemain_PublishesWithoutCompleting()
    {
        // Arrange
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var first = await SeedPendingEventAsync(queueId, "Duplicate");
        await SeedPendingEventAsync(queueId, "Ferry to the island");

        // Act
        await _extractionService.RejectPendingEventAsync(first);

        // Assert
        _published.Should().ContainSingle();
        _published[0].ProcessingStage.Should().Be(QueueProcessingStage.ReviewReady);
    }

    [Fact]
    public async Task ApproveBatch_WithPublishSuppressed_PublishesOncePerCaptureNotOncePerEvent()
    {
        // Arrange - what ReviewViewModel.ApproveAllAsync does: suppress the
        // per-event publish, then publish once per affected capture
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingIds = new List<string>
        {
            await SeedPendingEventAsync(queueId, "Ferry to the island"),
            await SeedPendingEventAsync(queueId, "Dinner at the harbour"),
            await SeedPendingEventAsync(queueId, "Late walk on the pier"),
        };

        // Act
        foreach (var pendingId in pendingIds)
        {
            await _extractionService.ApprovePendingEventAsync(pendingId, publishCaptureStatus: false);
        }

        _published.Should().BeEmpty("a batch must not publish once per event");
        await _extractionService.PublishCaptureStatusAsync(queueId);

        // Assert - one publish, carrying the finished state
        _published.Should().ContainSingle();
        _published[0].ProcessingStage.Should().Be(QueueProcessingStage.Completed);
    }

    [Fact]
    public async Task PublishCaptureStatusAsync_QueueItemStillProcessing_IsNotForcedToCompleted()
    {
        // Arrange - a capture that is still transcribing has no review to finish
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.Transcribing, QueueStatus.Processing);

        // Act
        await _extractionService.PublishCaptureStatusAsync(queueId);

        // Assert - the status is still projected, but the stage is untouched
        _published.Should().ContainSingle();
        _published[0].ProcessingStage.Should().Be(QueueProcessingStage.Transcribing);

        var stored = await _queueRepository.GetByIdAsync(queueId);
        stored!.ProcessingStage.Should().Be(QueueProcessingStage.Transcribing);
        stored.Status.Should().Be(QueueStatus.Processing);
    }

    [Fact]
    public async Task PublishCaptureStatusAsync_UnknownQueueId_PublishesNothing()
    {
        // Act
        await _extractionService.PublishCaptureStatusAsync(Guid.NewGuid().ToString());
        await _extractionService.PublishCaptureStatusAsync(string.Empty);

        // Assert
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovePendingEventAsync_PublisherThrows_StillApprovesTheEvent()
    {
        // Arrange - a status the phone misses is republished by the next
        // transition; it must never cost the user their approval
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the island");
        _statusPublisher
            .Setup(p => p.PublishAsync(It.IsAny<RecordingQueue>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        var approved = await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert
        approved.Title.Should().Be("Ferry to the island");
        await using var context = _contextFactory.CreateDbContext();
        (await context.Events.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ApprovePendingEventAsync_WithoutAStatusPublisher_StillApproves()
    {
        // Arrange - the publisher is optional, exactly as in QueueService, so an
        // unregistered path (and every existing test) keeps working
        var service = BuildService(statusPublisher: null);
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the island");

        // Act
        var approved = await service.ApprovePendingEventAsync(pendingId);
        await service.PublishCaptureStatusAsync(queueId);

        // Assert - approved, and the queue row was left exactly as it was
        approved.Title.Should().Be("Ferry to the island");
        var stored = await _queueRepository.GetByIdAsync(queueId);
        stored!.ProcessingStage.Should().Be(QueueProcessingStage.ReviewReady);
    }

    [Fact]
    public async Task ExtractAndCreatePendingEventsAsync_PublishesOneProjectionPerExtractedEvent()
    {
        // Arrange - the capture the extraction hangs off (the FK is enforced on
        // SQLite), and two events out of one transcript
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.Extracting, QueueStatus.Processing);
        GivenExtractionReturns("Ferry to the island", "Dinner at the harbour");

        // Act
        var created = await _extractionService.ExtractAndCreatePendingEventsAsync(
            queueId, "We took the ferry over and ate at the harbour that night.");

        // Assert - a companion can only show the queue it was sent, so every
        // extracted event needs its own projection
        created.Should().HaveCount(2);
        _projected.Should().BeEquivalentTo(
            created.Select(pending => $"pending_event:{pending.PendingId}"));
    }

    [Fact]
    public async Task ApprovePendingEventAsync_PublishesTheNewEventThenTombstonesTheQueueEntry()
    {
        // Arrange
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the island");

        // Act
        var approved = await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert - both, and the event first: an item briefly in the queue AND
        // on the timeline is a duplicate the tombstone clears, while a queue
        // entry that vanishes with no event behind it reads as a lost memory
        _projected.Should().Equal(
            $"event:{approved.EventId}",
            $"delete:{TimelineProjectionEntity.PendingEvent}:{pendingId}");
    }

    [Fact]
    public async Task ApprovePendingEventAsync_PublishesLinkedPeopleBetweenTheEventAndTheTombstone()
    {
        // Arrange - an extraction that named two people. Approval is where they
        // first reach the archive: one is created outright here, and both gain
        // an event_people row the person projection's EventCount is derived from.
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(
            queueId, "Ferry to the island", new[] { "Dana", "Sarah" });

        // Act
        var approved = await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert - the people the approval actually linked
        List<string> personIds;
        await using (var context = _contextFactory.CreateDbContext())
        {
            personIds = await context.EventPeople.AsNoTracking()
                .Where(ep => ep.EventId == approved.EventId)
                .Select(ep => ep.PersonId)
                .ToListAsync();
        }
        personIds.Should().HaveCount(2);

        // ...published between the event and the tombstone. An event payload
        // carries person IDS only — names come from the person projection — so a
        // batch truncated after the tombstone would leave a companion with a
        // settled-looking timeline entry naming contacts it cannot resolve and
        // nothing pending to suggest more is coming. Truncated after the people
        // it shows a duplicate queue item, the cost already accepted above.
        _projected.Should().HaveCount(4);
        _projected[0].Should().Be($"event:{approved.EventId}");
        _projected.Skip(1).Take(2).Should()
            .BeEquivalentTo(personIds.Select(id => $"person:{id}"));
        _projected[3].Should().Be($"delete:{TimelineProjectionEntity.PendingEvent}:{pendingId}");
    }

    [Fact]
    public async Task ApprovePendingEventAsync_WithNoExtractedPeople_PublishesNoPersonProjections()
    {
        // Arrange - the cost bound: people are published only where the junction
        // was actually written
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "A quiet afternoon", Array.Empty<string>());

        // Act
        var approved = await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert
        _projected.Should().Equal(
            $"event:{approved.EventId}",
            $"delete:{TimelineProjectionEntity.PendingEvent}:{pendingId}");
    }

    [Fact]
    public async Task RejectPendingEventAsync_TombstonesTheQueueEntryAndPublishesNoEvent()
    {
        // Arrange
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Nothing worth keeping");

        // Act
        await _extractionService.RejectPendingEventAsync(pendingId);

        // Assert - the extraction was thrown away, so the id is all that is
        // left to publish and no timeline entry follows it
        _projected.Should().Equal($"delete:{TimelineProjectionEntity.PendingEvent}:{pendingId}");

        await using var context = _contextFactory.CreateDbContext();
        (await context.Events.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdatePendingEventAsync_RepublishesTheCorrectedPendingEvent()
    {
        // Arrange - a reviewer fixing the extraction before deciding on it
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the ilsand");

        // Act
        await _extractionService.UpdatePendingEventAsync(new PendingEvent
        {
            PendingId = pendingId,
            Title = "Ferry to the island",
            StartDate = new DateTime(2024, 7, 1),
            Category = "travel",
        });

        // Assert - the companion's queue must show what will actually be
        // approved, not the title the model first guessed at
        _projected.Should().Equal($"pending_event:{pendingId}");
    }

    [Fact]
    public async Task ApprovePendingEventAsync_ProjectionPublisherThrows_StillApprovesTheEvent()
    {
        // Arrange - the approve has already committed by the time anything is
        // projected; a failed publish must not report a failure for it
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the island");
        _projectionPublisher
            .Setup(p => p.PublishEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        var approved = await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert - the event is in the archive
        approved.Title.Should().Be("Ferry to the island");
        await using var context = _contextFactory.CreateDbContext();
        (await context.Events.AsNoTracking().CountAsync()).Should().Be(1);

        // ...and each publish is guarded on its own, so one failing did not
        // take the rest of the approval's projections with it
        _projected.Should().Equal($"delete:{TimelineProjectionEntity.PendingEvent}:{pendingId}");
    }

    [Fact]
    public async Task ApprovePendingEventAsync_WithoutAProjectionPublisher_StillApproves()
    {
        // Arrange - the projection publisher is optional and defaults to none,
        // so every existing construction of the service keeps working
        var service = BuildService(_statusPublisher.Object, projectionPublisher: null);
        var queueId = await SeedQueueItemAsync(QueueProcessingStage.ReviewReady);
        var pendingId = await SeedPendingEventAsync(queueId, "Ferry to the island");

        // Act
        var approved = await service.ApprovePendingEventAsync(pendingId);

        // Assert
        approved.Title.Should().Be("Ferry to the island");
        _projected.Should().BeEmpty();
        _published.Should().ContainSingle("the capture status is a separate publisher and still fires");
    }

    // ---- Helpers ----

    private EventExtractionService BuildService(
        ICaptureStatusPublisher? statusPublisher,
        ITimelineProjectionPublisher? projectionPublisher = null) =>
        new(
            _llmService.Object,
            new Mock<ISpeechToTextService>().Object,
            new Mock<IEventService>().Object,
            new Mock<ISettingsService>().Object,
            _queueRepository,
            new Mock<IPersonService>().Object,
            _contextFactory,
            new Mock<ILogger<EventExtractionService>>().Object,
            revisionWriter: null,
            statusPublisher: statusPublisher,
            projectionPublisher: projectionPublisher);

    /// <summary>
    /// Makes the mocked LLM return one extracted event per title, so extraction
    /// creates exactly that many pending events.
    /// </summary>
    private void GivenExtractionReturns(params string[] titles)
    {
        _llmService
            .Setup(s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventExtractionResult
            {
                Success = true,
                OverallConfidence = 0.8,
                Events = titles.Select(title => new ExtractedEvent
                {
                    Title = title,
                    StartDate = new DateTime(2024, 7, 1),
                    DatePrecision = "day",
                    Category = "other",
                    Confidence = 0.7,
                }).ToList(),
            });
    }

    /// <summary>Seeds a device capture sitting at the given stage.</summary>
    private async Task<string> SeedQueueItemAsync(string stage, string status = QueueStatus.Completed)
    {
        var item = new RecordingQueue
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = @"C:\cache\capture.wav",
            Status = status,
            ProcessingStage = stage,
            SourceCaptureId = Guid.NewGuid().ToString(),
            SourceDeviceId = "device-1",
            SourcePlatform = CapturePlatform.Ios,
            SyncState = QueueSyncState.Received,
            CreatedAt = DateTime.UtcNow,
        };

        await _queueRepository.AddAsync(item);
        return item.QueueId;
    }

    /// <summary>
    /// Seeds an approvable pending event on a queue item. ExtractedData is left
    /// empty so approval skips tag/person/location mapping — the subject here is
    /// the status republish, not metadata.
    /// </summary>
    private async Task<string> SeedPendingEventAsync(
        string queueId, string title, string[]? people = null)
    {
        // An empty ExtractedData is the "no metadata to map" case the ordering
        // tests above want; passing people opts into the junction writes.
        var extractedData = people == null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(new ExtractedEvent
            {
                Title = title,
                StartDate = new DateTime(2024, 7, 1),
                Category = "other",
                People = people.ToList(),
                Confidence = 0.9,
            });

        var pending = new PendingEvent
        {
            PendingId = Guid.NewGuid().ToString(),
            QueueId = queueId,
            Title = title,
            StartDate = new DateTime(2024, 7, 1),
            Category = "other",
            ExtractedData = extractedData,
            Status = PendingStatus.PendingReview.ToStringValue(),
            IsApproved = false,
            CreatedAt = DateTime.UtcNow,
        };

        await using var context = _contextFactory.CreateDbContext();
        context.PendingEvents.Add(pending);
        await context.SaveChangesAsync();
        return pending.PendingId;
    }

    public void Dispose()
    {
        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureDeleted();
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
