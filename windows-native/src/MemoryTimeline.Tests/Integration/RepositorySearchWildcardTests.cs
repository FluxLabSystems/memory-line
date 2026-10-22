using FluentAssertions;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MemoryTimeline.Tests.Integration;

/// <summary>
/// The four LIKE-backed searches read the user's text literally: a term
/// containing <c>%</c>, <c>_</c> or <c>\</c> matches only values that contain
/// those characters, not everything a wildcard would admit. Runs against a
/// real FILE-based SQLite database because the escape clause is what the
/// SQLite provider emits; the InMemory provider would not prove it.
/// </summary>
public class RepositorySearchWildcardTests : IDisposable
{
    private readonly string _databasePath;
    private readonly TestDbContextFactory _factory;

    public RepositorySearchWildcardTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"SearchWildcardTest_{Guid.NewGuid()}.db");
        _factory = TestDbContextFactory.CreateSqliteFile(_databasePath);
        using var context = _factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task TagRepository_SearchByNameAsync_ReadsPercentAndUnderscoreLiterally()
    {
        // Arrange - "100%" would match every tag starting with 100 as a wildcard
        await using (var context = _factory.CreateDbContext())
        {
            context.Tags.AddRange(
                new Tag { TagName = "100%" },
                new Tag { TagName = "100 percent" },
                new Tag { TagName = "100x" },
                new Tag { TagName = "road_trip" },
                new Tag { TagName = "roadxtrip" });
            await context.SaveChangesAsync();
        }

        var repository = new TagRepository(_factory);

        // Act / Assert
        (await repository.SearchByNameAsync("100%")).Select(t => t.TagName).Should().Equal("100%");
        (await repository.SearchByNameAsync("road_trip")).Select(t => t.TagName).Should().Equal("road_trip");
        (await repository.SearchByNameAsync("100")).Select(t => t.TagName)
            .Should().BeEquivalentTo(new[] { "100%", "100 percent", "100x" }, "a plain term still searches for containment");
    }

    [Fact]
    public async Task PersonRepository_SearchByNameAsync_ReadsWildcardsLiterally()
    {
        // Arrange
        await using (var context = _factory.CreateDbContext())
        {
            context.People.AddRange(
                new Person { Name = "Mr_Smith" },
                new Person { Name = "MrsSmith" },
                new Person { Name = "Al 50%" });
            await context.SaveChangesAsync();
        }

        var repository = new PersonRepository(_factory);

        // Act / Assert
        (await repository.SearchByNameAsync("Mr_")).Select(p => p.Name).Should().Equal("Mr_Smith");
        (await repository.SearchByNameAsync("50%")).Select(p => p.Name).Should().Equal("Al 50%");
        (await repository.SearchByNameAsync("%")).Select(p => p.Name).Should().Equal(new[] { "Al 50%" },
            "a lone percent sign is a character to look for, not match-everything");
    }

    [Fact]
    public async Task LocationRepository_SearchByNameAsync_ReadsBackslashLiterally()
    {
        // Arrange - the escape character itself must survive as text
        await using (var context = _factory.CreateDbContext())
        {
            context.Locations.AddRange(
                new Location { Name = @"C:\Photos" },
                new Location { Name = "C:Photos" },
                new Location { Name = "Cafe 100%" });
            await context.SaveChangesAsync();
        }

        var repository = new LocationRepository(_factory);

        // Act / Assert
        (await repository.SearchByNameAsync(@"C:\")).Select(l => l.Name).Should().Equal(@"C:\Photos");
        (await repository.SearchByNameAsync("100%")).Select(l => l.Name).Should().Equal("Cafe 100%");
        (await repository.SearchByNameAsync("Photos")).Select(l => l.Name)
            .Should().BeEquivalentTo(@"C:\Photos", "C:Photos");
    }

    [Fact]
    public async Task EventRepository_SearchAsync_ReadsWildcardsLiterallyAcrossTitleDescriptionAndTranscript()
    {
        // Arrange - the literal lives in a different column on each row
        await using (var context = _factory.CreateDbContext())
        {
            context.Events.AddRange(
                new Event { Title = "Paid 100%", StartDate = new DateTime(2020, 1, 1), Category = EventCategory.Work },
                new Event { Title = "Paid 100 dollars", StartDate = new DateTime(2020, 1, 2), Category = EventCategory.Work },
                new Event { Title = "Deposit", Description = "the 100% down payment", StartDate = new DateTime(2020, 1, 3), Category = EventCategory.Work },
                new Event { Title = "Call", RawTranscript = "we agreed on 100% of it", StartDate = new DateTime(2020, 1, 4), Category = EventCategory.Work },
                new Event { Title = "Call again", RawTranscript = "we agreed on 100 of it", StartDate = new DateTime(2020, 1, 5), Category = EventCategory.Work });
            await context.SaveChangesAsync();
        }

        var repository = new EventRepository(_factory);

        // Act
        var hits = await repository.SearchAsync("100%");

        // Assert - exactly the rows carrying a literal "100%", from any column
        hits.Select(e => e.Title).Should().BeEquivalentTo("Paid 100%", "Deposit", "Call");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
