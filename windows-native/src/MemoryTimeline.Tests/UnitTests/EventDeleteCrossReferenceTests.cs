using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Deleting an event that the Connections page has related to another must
/// succeed. cross_references carries two ON DELETE RESTRICT foreign keys and
/// every connection runs with foreign_keys=ON, so this is a FILE-BACKED
/// SQLite test on purpose: the EF InMemory provider enforces no foreign keys
/// and would pass whether or not the references were removed first.
/// </summary>
public class EventDeleteCrossReferenceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly TestDbContextFactory _factory;
    private readonly EventService _eventService;

    public EventDeleteCrossReferenceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"EventDeleteXref_{Guid.NewGuid():N}.db");
        _factory = TestDbContextFactory.CreateSqliteFile(_databasePath);
        using (var context = _factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _eventService = new EventService(new EventRepository(_factory), _factory, NullLogger<EventService>.Instance);
    }

    [Fact]
    public async Task DeleteEventAsync_EventWithStoredCrossReferences_DeletesEventAndItsReferencesBothWays()
    {
        // Arrange - A relates to B (A first) and C relates to A (A second);
        // B and C also relate to each other and must keep that link
        await SeedEventsAsync("A", "B", "C");
        await SeedReferenceAsync("ref-ab", "A", "B");
        await SeedReferenceAsync("ref-ca", "C", "A");
        await SeedReferenceAsync("ref-bc", "B", "C");
        (await ForeignKeysEnforcedAsync()).Should().BeTrue("the test must run under the production pragma");

        // Act
        await _eventService.DeleteEventAsync("A");

        // Assert
        await using var context = _factory.CreateDbContext();
        (await context.Events.Select(e => e.EventId).ToListAsync()).Should().BeEquivalentTo(new[] { "B", "C" });
        (await context.CrossReferences.Select(cr => cr.ReferenceId).ToListAsync()).Should().Equal("ref-bc");
    }

    [Fact]
    public async Task DeleteRangeAsync_EventsWithCrossReferences_DeletesThemTogether()
    {
        // Arrange
        await SeedEventsAsync("A", "B", "C");
        await SeedReferenceAsync("ref-ab", "A", "B");
        await SeedReferenceAsync("ref-bc", "B", "C");

        // Act
        var repository = new EventRepository(_factory);
        var toDelete = new List<Event>();
        await using (var context = _factory.CreateDbContext())
        {
            toDelete.AddRange(await context.Events.AsNoTracking().Where(e => e.EventId == "A" || e.EventId == "B").ToListAsync());
        }

        await repository.DeleteRangeAsync(toDelete);

        // Assert
        await using var verify = _factory.CreateDbContext();
        (await verify.Events.Select(e => e.EventId).ToListAsync()).Should().Equal("C");
        (await verify.CrossReferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteEventAsync_EventWithoutCrossReferences_StillDeletes()
    {
        await SeedEventsAsync("A", "B");

        await _eventService.DeleteEventAsync("A");

        await using var context = _factory.CreateDbContext();
        (await context.Events.Select(e => e.EventId).ToListAsync()).Should().Equal("B");
    }

    #region Helpers

    private async Task SeedEventsAsync(params string[] ids)
    {
        await using var context = _factory.CreateDbContext();
        foreach (var id in ids)
        {
            context.Events.Add(new Event
            {
                EventId = id,
                Title = $"Event {id}",
                StartDate = new DateTime(2024, 1, 1),
                Category = EventCategory.Other,
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task SeedReferenceAsync(string referenceId, string eventId1, string eventId2)
    {
        await using var context = _factory.CreateDbContext();
        context.CrossReferences.Add(new CrossReference
        {
            ReferenceId = referenceId,
            EventId1 = eventId1,
            EventId2 = eventId2,
            RelationshipType = RelationshipType.Temporal.ToStringValue(),
            ConfidenceScore = 0.8,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<bool> ForeignKeysEnforcedAsync()
    {
        await using var context = _factory.CreateDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    public void Dispose()
    {
        using (var context = _factory.CreateDbContext())
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

    #endregion
}
