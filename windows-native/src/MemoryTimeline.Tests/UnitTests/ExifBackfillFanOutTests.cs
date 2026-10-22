using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The EXIF coordinate backfill writes onto <c>Location</c> rows, which are
/// shared: every OTHER event linked to a location that just gained
/// coordinates has a map pin a companion has not seen. The backfill must
/// republish those events itself — the attaching event is published by the
/// attach — and must publish nothing when no location changed. Real
/// <see cref="TimelineProjectionPublisher"/> over EF InMemory, like
/// <see cref="ExifBackfillTests"/> with the publisher wired in.
/// </summary>
public class ExifBackfillFanOutTests
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly MediaService _mediaService;

    public ExifBackfillFanOutTests()
    {
        var publisher = new TimelineProjectionPublisher(
            new SyncOutboxRepository(_contextFactory),
            new EventRepository(_contextFactory),
            new EraRepository(_contextFactory),
            new PersonRepository(_contextFactory),
            new PendingEventRepository(_contextFactory),
            new EventMediaRepository(_contextFactory),
            NullLogger<TimelineProjectionPublisher>.Instance);

        _mediaService = new MediaService(
            new EventMediaRepository(_contextFactory),
            _contextFactory,
            new Mock<IThumbnailGenerator>().Object,
            NullLogger<MediaService>.Instance,
            mediaRootOverride: Path.Combine(Path.GetTempPath(), $"ExifFanOut_{Guid.NewGuid():N}"),
            projectionPublisher: publisher);
    }

    [Fact]
    public async Task Backfill_SharedLocation_RepublishesTheOtherLinkedEventsOnly()
    {
        // Arrange - three events share "The beach house" (no coordinates yet);
        // a fourth is linked to an already-located place and must stay quiet
        await SeedEventAsync("event-attaching");
        await SeedEventAsync("event-sibling-1");
        await SeedEventAsync("event-sibling-2");
        await SeedEventAsync("event-elsewhere");
        await SeedLocationAsync("loc-beach", "The beach house");
        await SeedLocationAsync("loc-amsterdam", "Amsterdam", latitude: 52.37, longitude: 4.89);
        await SeedLinkAsync("event-attaching", "loc-beach");
        await SeedLinkAsync("event-sibling-1", "loc-beach");
        await SeedLinkAsync("event-sibling-2", "loc-beach");
        await SeedLinkAsync("event-elsewhere", "loc-amsterdam");

        // Act - the photo on event-attaching says where the beach house is
        var republished = await _mediaService.BackfillEventLocationCoordinatesAsync("event-attaching", 48.8566, 2.3522);

        // Assert - the two siblings, not the attaching event (its attach
        // publishes it, after the media count moved too) and not the unrelated one
        republished.Should().BeEquivalentTo(new[] { "event-sibling-1", "event-sibling-2" });

        var rows = await OutboxRowsAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.EntityId).Should().BeEquivalentTo(new[] { "event-sibling-1", "event-sibling-2" });
        rows.Should().OnlyContain(r => r.EntityType == SyncEntityType.Event && r.Operation == SyncOutboxOperation.Upsert);

        // The projections carry the pin that moved.
        foreach (var row in rows)
        {
            row.PayloadJson.Should().Contain("48.8566");
            row.PayloadJson.Should().Contain("2.3522");
        }
    }

    [Fact]
    public async Task Backfill_NothingToFill_PublishesNothing()
    {
        // Arrange - every linked location already has coordinates
        await SeedEventAsync("event-1");
        await SeedEventAsync("event-2");
        await SeedLocationAsync("loc-amsterdam", "Amsterdam", latitude: 52.37, longitude: 4.89);
        await SeedLinkAsync("event-1", "loc-amsterdam");
        await SeedLinkAsync("event-2", "loc-amsterdam");

        // Act
        var republished = await _mediaService.BackfillEventLocationCoordinatesAsync("event-1", 48.8566, 2.3522);

        // Assert
        republished.Should().BeEmpty();
        (await OutboxRowsAsync()).Should().BeEmpty();
    }

    #region Helpers

    private async Task SeedEventAsync(string eventId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.Add(new Event
        {
            EventId = eventId,
            Title = $"Event {eventId}",
            StartDate = new DateTime(2024, 6, 1),
            Category = EventCategory.Travel,
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedLocationAsync(string locationId, string name, double? latitude = null, double? longitude = null)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Locations.Add(new Location
        {
            LocationId = locationId,
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedLinkAsync(string eventId, string locationId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventLocations.Add(new EventLocation { EventId = eventId, LocationId = locationId });
        await context.SaveChangesAsync();
    }

    private async Task<List<SyncOutbox>> OutboxRowsAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.SyncOutboxEntries.AsNoTracking().OrderBy(o => o.OutboxId).ToListAsync();
    }

    #endregion
}
