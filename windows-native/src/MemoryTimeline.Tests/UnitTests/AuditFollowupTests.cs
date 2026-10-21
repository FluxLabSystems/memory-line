using FluentAssertions;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Three findings from docs/reviews/exhaustive-audit-2026-07.md that were
/// still present: the settings cache was read outside its lock, the search
/// facets filtered on a narrower search-term predicate than the result list,
/// and an ongoing era (no end date) was marked invisible as soon as the view
/// scrolled past its start.
/// </summary>
public class AuditFollowupTests
{
    #region Settings cache

    [Fact]
    public async Task SettingsService_ConcurrentReadsAndWrites_NeverCorruptTheCache()
    {
        // Arrange - a hot cache, then hammer it from many threads at once.
        // Before the fix, Dictionary reads raced its writes and this threw
        // (InvalidOperationException from a resize, or a KeyNotFound) or hung.
        var service = new SettingsService(TestDbContextFactory.CreateInMemory(), NullLogger<SettingsService>.Instance);
        await service.SetSettingAsync("warm", "up");

        // Act
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                await service.SetSettingAsync($"key-{w}-{i}", i);
            }
        }));
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 300; i++)
            {
                await service.GetSettingAsync<string>("warm", "cold");
                await service.SettingExistsAsync($"key-0-{i % 60}");
                await service.GetAllSettingsAsync();
            }
        }));

        var act = () => Task.WhenAll(writers.Concat(readers));

        // Assert
        await act.Should().NotThrowAsync();
        (await service.GetAllSettingsAsync()).Count.Should().Be(1 + 8 * 60);
        (await service.GetSettingAsync<string>("warm", "cold")).Should().Be("up");
    }

    #endregion

    #region Facet counts

    [Fact]
    public async Task Search_FacetsCountTheSameEventsTheResultListShows()
    {
        // Arrange - three "work" events; the term "kayak" matches one by
        // title, one only by its transcript, and the third not at all
        var factory = TestDbContextFactory.CreateInMemory();
        await using (var context = factory.CreateDbContext())
        {
            context.Events.AddRange(
                new Event { EventId = "by-title", Title = "Kayak trip", StartDate = new DateTime(2024, 5, 1), Category = EventCategory.Work },
                new Event { EventId = "by-transcript", Title = "Long weekend", RawTranscript = "we rented a kayak", StartDate = new DateTime(2024, 6, 1), Category = EventCategory.Work },
                new Event { EventId = "no-match", Title = "Budget review", StartDate = new DateTime(2024, 7, 1), Category = EventCategory.Work });
            await context.SaveChangesAsync();
        }

        var service = new AdvancedSearchService(factory, NullLogger<AdvancedSearchService>.Instance);

        // Act
        var results = await service.SearchAsync(new SearchFilter { SearchTerm = "kayak", PageNumber = 1, PageSize = 20 });

        // Assert - the list and the category facet agree: 2, not 1
        results.TotalCount.Should().Be(2);
        results.Events.Select(e => e.EventId).Should().BeEquivalentTo(new[] { "by-title", "by-transcript" });
        results.Facets.Categories.Should().ContainKey(EventCategory.Work).WhoseValue.Should().Be(2,
            "a facet must count exactly the events the result list shows");
        results.Facets.WithTranscriptCount.Should().Be(1);
        results.Facets.EarliestDate.Should().Be(new DateTime(2024, 5, 1));
        results.Facets.LatestDate.Should().Be(new DateTime(2024, 6, 1));
    }

    #endregion

    #region Ongoing eras

    [Fact]
    public void CalculateEraPositions_OngoingEra_StaysVisibleAfterTheViewPassesItsStart()
    {
        // Arrange - an era that began in 2020 and has no end, viewed in 2024
        var service = new TimelineService(
            new EventRepository(TestDbContextFactory.CreateInMemory()),
            new EraRepository(TestDbContextFactory.CreateInMemory()),
            NullLogger<TimelineService>.Instance);
        var viewport = new TimelineViewport
        {
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 12, 31),
            PixelsPerDay = 2,
            ViewportWidth = 730,
        };
        var ongoing = new TimelineEraDto { EraId = "ongoing", Name = "Current job", StartDate = new DateTime(2020, 3, 1), EndDate = null };
        var ended = new TimelineEraDto { EraId = "ended", Name = "University", StartDate = new DateTime(2015, 9, 1), EndDate = new DateTime(2019, 6, 30) };
        var future = new TimelineEraDto { EraId = "future", Name = "Sabbatical", StartDate = new DateTime(2026, 1, 1), EndDate = null };

        // Act
        service.CalculateEraPositions(new[] { ongoing, ended, future }, viewport);

        // Assert
        ongoing.IsVisible.Should().BeTrue("an era with no end date is still running and spans the whole view");
        ongoing.Width.Should().BeGreaterThan(0);
        ended.IsVisible.Should().BeFalse("it ended years before the view starts");
        future.IsVisible.Should().BeFalse("it has not started yet");
    }

    [Fact]
    public void CalculateEraPositions_EndedEraInsideTheView_IsStillVisible()
    {
        var service = new TimelineService(
            new EventRepository(TestDbContextFactory.CreateInMemory()),
            new EraRepository(TestDbContextFactory.CreateInMemory()),
            NullLogger<TimelineService>.Instance);
        var viewport = new TimelineViewport
        {
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 12, 31),
            PixelsPerDay = 2,
            ViewportWidth = 730,
        };
        var era = new TimelineEraDto { EraId = "spring", Name = "Spring project", StartDate = new DateTime(2024, 3, 1), EndDate = new DateTime(2024, 5, 31) };

        service.CalculateEraPositions(new[] { era }, viewport);

        era.IsVisible.Should().BeTrue();
    }

    #endregion
}
