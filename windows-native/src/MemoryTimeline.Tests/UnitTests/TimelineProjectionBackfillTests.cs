using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// <see cref="TimelineProjectionPublisher.PublishAllAsync"/>: the full-archive
/// backfill a companion needs after pairing, after a restore, or for an
/// archive that predates projection publishing. Over the REAL repositories and
/// EF InMemory (unique database per test instance), like
/// <see cref="TimelineProjectionPublisherTests"/>. Covers the entity order a
/// truncated delivery depends on, which pending events are included, and the
/// two modes' treatment of rows the outbox already delivered.
/// </summary>
public class TimelineProjectionBackfillTests
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly TimelineProjectionPublisher _publisher;

    public TimelineProjectionBackfillTests()
    {
        _publisher = new TimelineProjectionPublisher(
            new SyncOutboxRepository(_contextFactory),
            new EventRepository(_contextFactory),
            new EraRepository(_contextFactory),
            new PersonRepository(_contextFactory),
            new PendingEventRepository(_contextFactory),
            new EventMediaRepository(_contextFactory),
            NullLogger<TimelineProjectionPublisher>.Instance);
    }

    [Fact]
    public async Task PublishAllAsync_UntouchedArchive_ProjectsEverythingInPrefixSafeOrder()
    {
        // Arrange - an archive that never went through the publisher: two eras,
        // two events (one in an era), two people, a pending event, and decided
        // pending events that must NOT come back from the dead
        await SeedArchiveAsync();

        // Act
        var result = await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.MissingOrChanged);

        // Assert - counts
        result.Eras.Should().Be(2);
        result.Events.Should().Be(2);
        result.People.Should().Be(2);
        result.PendingEvents.Should().Be(1);
        result.Unchanged.Should().Be(0);
        result.Published.Should().Be(7);

        // Order: eras, then events, then people, then the review queue, in
        // outbox_id (delivery) order, so any prefix is consistent on a companion.
        var rows = await RowsAsync();
        rows.Select(r => r.EntityType).Should().Equal(
            SyncEntityType.Era, SyncEntityType.Era,
            SyncEntityType.Event, SyncEntityType.Event,
            SyncEntityType.Person, SyncEntityType.Person,
            SyncEntityType.PendingEvent);
        rows.Should().OnlyContain(r => r.Operation == SyncOutboxOperation.Upsert && r.DeliveredAt == null);
        rows.Single(r => r.EntityType == SyncEntityType.PendingEvent).EntityId.Should().Be("pending-open");
    }

    [Fact]
    public async Task PublishAllAsync_RunTwiceBeforeADrain_QueuesNothingTwice()
    {
        // Arrange
        await SeedArchiveAsync();
        await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);

        // Act - the same request again, identical content still pending
        var second = await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);

        // Assert
        second.Published.Should().Be(0);
        second.Unchanged.Should().Be(7);
        (await RowsAsync()).Should().HaveCount(7);
    }

    [Fact]
    public async Task PublishAllAsync_MissingOrChanged_LeavesDeliveredIdenticalRowsAlone()
    {
        // Arrange - everything was published and pushed to the server already
        await SeedArchiveAsync();
        await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);
        await MarkAllDeliveredAsync();

        // Act - the cheap catch-up finds nothing new
        var result = await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.MissingOrChanged);

        // Assert
        result.Published.Should().Be(0);
        result.Unchanged.Should().Be(7);
        (await RowsAsync()).Should().HaveCount(7);
    }

    [Fact]
    public async Task PublishAllAsync_Everything_RepublishesDeliveredRows()
    {
        // Arrange - delivered to a server this installation no longer talks to
        // (new registration / restored archive): "already sent" is not true any more
        await SeedArchiveAsync();
        await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);
        await MarkAllDeliveredAsync();

        // Act
        var result = await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);

        // Assert - seven new, undelivered rows behind the seven delivered ones
        result.Published.Should().Be(7);
        result.Unchanged.Should().Be(0);
        var rows = await RowsAsync();
        rows.Should().HaveCount(14);
        rows.Count(r => r.DeliveredAt == null).Should().Be(7);
    }

    [Fact]
    public async Task PublishAllAsync_MissingOrChanged_PicksUpWhatChangedSinceDelivery()
    {
        // Arrange - one event edited after its projection was delivered
        await SeedArchiveAsync();
        await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);
        await MarkAllDeliveredAsync();

        await using (var context = _contextFactory.CreateDbContext())
        {
            var evt = await context.Events.SingleAsync(e => e.EventId == "event-in-era");
            evt.Title = "Ferry to the island (renamed)";
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.MissingOrChanged);

        // Assert - exactly the renamed event
        result.Published.Should().Be(1);
        result.Events.Should().Be(1);
        var pending = (await RowsAsync()).Where(r => r.DeliveredAt == null).ToList();
        pending.Should().ContainSingle().Which.EntityId.Should().Be("event-in-era");
        pending[0].PayloadJson.Should().Contain("renamed");
    }

    [Fact]
    public async Task PublishAllAsync_EmptyArchive_PublishesNothing()
    {
        var result = await _publisher.PublishAllAsync(TimelineProjectionBackfillMode.Everything);

        result.Published.Should().Be(0);
        result.Unchanged.Should().Be(0);
        (await RowsAsync()).Should().BeEmpty();
    }

    #region Helpers

    private async Task SeedArchiveAsync()
    {
        await using var context = _contextFactory.CreateDbContext();

        context.Eras.AddRange(
            new Era { EraId = "era-1", Name = "University", StartDate = new DateTime(2001, 9, 1), EndDate = new DateTime(2004, 6, 30) },
            new Era { EraId = "era-2", Name = "First job", StartDate = new DateTime(2004, 9, 1) });

        context.Events.AddRange(
            new Event { EventId = "event-in-era", Title = "Ferry to the island", StartDate = new DateTime(2003, 7, 14), EraId = "era-1", Category = EventCategory.Travel },
            new Event { EventId = "event-loose", Title = "Moved flat", StartDate = new DateTime(2005, 2, 1), Category = EventCategory.Other });

        context.People.AddRange(
            new Person { PersonId = "person-1", Name = "Dana" },
            new Person { PersonId = "person-2", Name = "Ruth" });
        context.EventPeople.Add(new EventPerson { EventId = "event-in-era", PersonId = "person-1" });

        context.RecordingQueues.Add(new RecordingQueue
        {
            QueueId = "queue-1",
            AudioFilePath = string.Empty,
            Status = QueueStatus.Completed,
            SourceCaptureId = "capture-1",
        });

        context.PendingEvents.AddRange(
            new PendingEvent { PendingId = "pending-open", QueueId = "queue-1", Title = "Dinner with Ruth", StartDate = new DateTime(2005, 3, 3), Status = PendingStatus.PendingReview.ToStringValue() },
            new PendingEvent { PendingId = "pending-approved", QueueId = "queue-1", Title = "Approved already", StartDate = new DateTime(2005, 3, 4), Status = PendingStatus.Approved.ToStringValue(), IsApproved = true },
            new PendingEvent { PendingId = "pending-rejected", QueueId = "queue-1", Title = "Rejected already", StartDate = new DateTime(2005, 3, 5), Status = PendingStatus.Rejected.ToStringValue() });

        await context.SaveChangesAsync();
    }

    private async Task<List<SyncOutbox>> RowsAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.SyncOutboxEntries.AsNoTracking().OrderBy(o => o.OutboxId).ToListAsync();
    }

    private async Task MarkAllDeliveredAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        foreach (var row in await context.SyncOutboxEntries.Where(o => o.DeliveredAt == null).ToListAsync())
        {
            row.DeliveredAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    #endregion
}
