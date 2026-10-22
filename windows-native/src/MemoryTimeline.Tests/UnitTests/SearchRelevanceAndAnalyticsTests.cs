using FluentAssertions;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Two optional audit follow-ups: the Search page's "Relevance" sort was a
/// date sort in disguise, and the analytics load re-read every event's start
/// date six times. Relevance now ranks by where the term matches before
/// recency; the density and heatmap series are pure functions of one read.
/// </summary>
public class SearchRelevanceAndAnalyticsTests
{
    [Fact]
    public async Task Search_RelevanceSort_RanksTitleMatchesAboveDescriptionAboveTranscript_ThenNewestFirst()
    {
        // Arrange - the same term in different places; dates deliberately
        // favour the weakest matches so a date sort would get this wrong
        var factory = TestDbContextFactory.CreateInMemory();
        await using (var context = factory.CreateDbContext())
        {
            context.Events.AddRange(
                new Event { EventId = "transcript-only", Title = "Long weekend", RawTranscript = "we rented a kayak", StartDate = new DateTime(2024, 9, 1), Category = EventCategory.Travel },
                new Event { EventId = "description", Title = "Lake trip", Description = "Kayak hire at dawn", StartDate = new DateTime(2024, 8, 1), Category = EventCategory.Travel },
                new Event { EventId = "title-older", Title = "Kayak lesson", StartDate = new DateTime(2022, 5, 1), Category = EventCategory.Travel },
                new Event { EventId = "title-newer", Title = "Kayak race", StartDate = new DateTime(2023, 5, 1), Category = EventCategory.Travel },
                new Event { EventId = "exact-title", Title = "Kayak", StartDate = new DateTime(2020, 1, 1), Category = EventCategory.Travel },
                new Event { EventId = "no-match", Title = "Budget review", StartDate = new DateTime(2024, 10, 1), Category = EventCategory.Work });
            await context.SaveChangesAsync();
        }

        var service = new AdvancedSearchService(factory, NullLogger<AdvancedSearchService>.Instance);

        // Act
        var results = await service.SearchAsync(new SearchFilter
        {
            SearchTerm = "kayak",
            SortBy = SearchSortOptions.Relevance,
            PageNumber = 1,
            PageSize = 20,
        });

        // Assert
        results.Events.Select(e => e.EventId).Should().Equal(
            "exact-title", "title-newer", "title-older", "description", "transcript-only");
    }

    [Fact]
    public async Task Search_RelevanceSort_TranslatesToSqlOnSqlite()
    {
        // The score is a sum of conditionals; EF must translate it to CASE
        // expressions rather than failing at runtime, which only a real
        // relational provider can prove (InMemory evaluates LINQ in memory).
        var databasePath = Path.Combine(Path.GetTempPath(), $"RelevanceSort_{Guid.NewGuid():N}.db");
        var factory = TestDbContextFactory.CreateSqliteFile(databasePath);
        try
        {
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
                context.Events.AddRange(
                    new Event { EventId = "transcript-only", Title = "Long weekend", RawTranscript = "we rented a kayak", StartDate = new DateTime(2024, 9, 1), Category = EventCategory.Travel },
                    new Event { EventId = "title", Title = "Kayak race", StartDate = new DateTime(2023, 5, 1), Category = EventCategory.Travel },
                    new Event { EventId = "exact-title", Title = "Kayak", StartDate = new DateTime(2020, 1, 1), Category = EventCategory.Travel });
                await context.SaveChangesAsync();
            }

            var service = new AdvancedSearchService(factory, NullLogger<AdvancedSearchService>.Instance);

            var results = await service.SearchAsync(new SearchFilter
            {
                SearchTerm = "kayak",
                SortBy = SearchSortOptions.Relevance,
                PageNumber = 1,
                PageSize = 20,
            });

            results.Events.Select(e => e.EventId).Should().Equal("exact-title", "title", "transcript-only");
        }
        finally
        {
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureDeletedAsync();
            }

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Delete(databasePath + suffix);
                }
            }
        }
    }

    [Fact]
    public async Task Search_RelevanceSortWithoutATerm_FallsBackToNewestFirst()
    {
        var factory = TestDbContextFactory.CreateInMemory();
        await using (var context = factory.CreateDbContext())
        {
            context.Events.AddRange(
                new Event { EventId = "older", Title = "A", StartDate = new DateTime(2020, 1, 1), Category = EventCategory.Work },
                new Event { EventId = "newer", Title = "B", StartDate = new DateTime(2024, 1, 1), Category = EventCategory.Work });
            await context.SaveChangesAsync();
        }

        var service = new AdvancedSearchService(factory, NullLogger<AdvancedSearchService>.Instance);

        var results = await service.SearchAsync(new SearchFilter { SortBy = SearchSortOptions.Relevance, PageNumber = 1, PageSize = 20 });

        results.Events.Select(e => e.EventId).Should().Equal("newer", "older");
    }

    [Fact]
    public async Task Analytics_FullLoad_MatchesThePerSeriesMethods()
    {
        // Arrange
        var factory = TestDbContextFactory.CreateInMemory();
        await using (var context = factory.CreateDbContext())
        {
            context.Events.AddRange(
                new Event { EventId = "1", Title = "A", StartDate = new DateTime(2023, 3, 6), Category = EventCategory.Work },
                new Event { EventId = "2", Title = "B", StartDate = new DateTime(2023, 3, 20), Category = EventCategory.Work },
                new Event { EventId = "3", Title = "C", StartDate = new DateTime(2024, 7, 14), Category = EventCategory.Travel });
            await context.SaveChangesAsync();
        }

        var service = new AnalyticsService(factory, NullLogger<AnalyticsService>.Instance);

        // Act
        var analytics = await service.GetTimelineAnalyticsAsync();

        // Assert - the one-read series equal the per-series re-reads
        analytics.MonthlyDensity.Should().BeEquivalentTo(await service.GetTimelineDensityAsync(DensityGranularity.Monthly));
        analytics.YearlyDensity.Should().BeEquivalentTo(await service.GetTimelineDensityAsync(DensityGranularity.Yearly));
        analytics.ActivityByDayOfWeek.Should().BeEquivalentTo(await service.GetActivityHeatmapAsync(HeatmapType.DayOfWeek));
        analytics.ActivityByMonth.Should().BeEquivalentTo(await service.GetActivityHeatmapAsync(HeatmapType.MonthOfYear));
        analytics.ActivityByYear.Should().BeEquivalentTo(await service.GetActivityHeatmapAsync(HeatmapType.YearOverTime));

        analytics.MonthlyDensity.Should().HaveCount(2);
        analytics.MonthlyDensity[0].EventCount.Should().Be(2);
        analytics.YearlyDensity.Select(p => p.Date.Year).Should().Equal(2023, 2024);
    }

    [Fact]
    public void Analytics_Builders_AreDeterministicOverTheSameDates()
    {
        var dates = new List<DateTime> { new(2023, 3, 6), new(2023, 3, 20), new(2024, 7, 14) };

        AnalyticsService.BuildDensityPoints(dates, DensityGranularity.Yearly)
            .Should().BeEquivalentTo(AnalyticsService.BuildDensityPoints(dates, DensityGranularity.Yearly));
        AnalyticsService.BuildHeatmapCells(dates, HeatmapType.DayOfWeek).Should().HaveCount(7);
        AnalyticsService.BuildHeatmapCells(new List<DateTime>(), HeatmapType.MonthOfYear).Should().BeEmpty();
        AnalyticsService.BuildDensityPoints(new List<DateTime>(), DensityGranularity.Monthly).Should().BeEmpty();
    }
}
