using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for timeline analytics and visualizations.
/// </summary>
public interface IAnalyticsService
{
    /// <summary>
    /// Get comprehensive timeline analytics.
    /// </summary>
    Task<TimelineAnalytics> GetTimelineAnalyticsAsync();

    /// <summary>
    /// Get category distribution data.
    /// </summary>
    Task<List<CategoryDistribution>> GetCategoryDistributionAsync();

    /// <summary>
    /// Get timeline density data.
    /// </summary>
    Task<List<TimelineDensityPoint>> GetTimelineDensityAsync(DensityGranularity granularity);

    /// <summary>
    /// Get tag cloud data.
    /// </summary>
    Task<List<TagCloudItem>> GetTagCloudAsync(int maxTags = 50);

    /// <summary>
    /// Get people network graph data.
    /// </summary>
    Task<PeopleNetwork> GetPeopleNetworkAsync();

    /// <summary>
    /// Get activity heatmap data.
    /// </summary>
    Task<List<ActivityHeatmapCell>> GetActivityHeatmapAsync(HeatmapType type);
}

/// <summary>
/// Density granularity options.
/// </summary>
public enum DensityGranularity
{
    Daily,
    Weekly,
    Monthly,
    Yearly
}

/// <summary>
/// Heatmap type options.
/// </summary>
public enum HeatmapType
{
    DayOfWeek,      // X: day of week (0-6), Y: events
    MonthOfYear,    // X: month (0-11), Y: events
    YearOverTime    // X: month, Y: year
}

/// <summary>
/// Analytics service implementation.
/// </summary>
public class AnalyticsService : IAnalyticsService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<AnalyticsService> _logger;

    public AnalyticsService(IDbContextFactory<AppDbContext> contextFactory, ILogger<AnalyticsService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<TimelineAnalytics> GetTimelineAnalyticsAsync()
    {
        _logger.LogInformation("Generating timeline analytics");

        var analytics = new TimelineAnalytics();

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            // Overview statistics
            analytics.TotalEvents = await dbContext.Events.CountAsync();
            analytics.TotalEras = await dbContext.Eras.CountAsync();
            analytics.TotalTags = await dbContext.Tags.CountAsync();
            // Living people only: merged-away tombstones keep their rows but
            // are not distinct people.
            analytics.TotalPeople = await dbContext.People.CountAsync(p => p.MergedIntoId == null);
            analytics.TotalLocations = await dbContext.Locations.CountAsync();

            analytics.EventsWithAudio = await dbContext.Events
                .CountAsync(e => e.AudioFilePath != null && e.AudioFilePath != "");
            analytics.EventsWithTranscript = await dbContext.Events
                .CountAsync(e => e.RawTranscript != null && e.RawTranscript != "");

            // Date range
            var dates = await dbContext.Events
                .Select(e => e.StartDate)
                .ToListAsync();

            if (dates.Any())
            {
                analytics.EarliestEventDate = dates.Min();
                analytics.LatestEventDate = dates.Max();
                analytics.TimelineSpan = analytics.LatestEventDate - analytics.EarliestEventDate;
            }

            // Get all component analytics. The density and heatmap series are
            // pure functions of the start dates read once above; the public
            // per-series methods re-read for callers that want one series.
            analytics.CategoryDistribution = await GetCategoryDistributionAsync();
            analytics.MonthlyDensity = BuildDensityPoints(dates, DensityGranularity.Monthly);
            analytics.YearlyDensity = BuildDensityPoints(dates, DensityGranularity.Yearly);
            analytics.TagCloud = await GetTagCloudAsync();
            analytics.PeopleNetwork = await GetPeopleNetworkAsync();
            analytics.ActivityByDayOfWeek = BuildHeatmapCells(dates, HeatmapType.DayOfWeek);
            analytics.ActivityByMonth = BuildHeatmapCells(dates, HeatmapType.MonthOfYear);
            analytics.ActivityByYear = BuildHeatmapCells(dates, HeatmapType.YearOverTime);

            _logger.LogInformation("Analytics generated: {EventCount} events analyzed", analytics.TotalEvents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating timeline analytics");
            throw;
        }

        return analytics;
    }

    public async Task<List<CategoryDistribution>> GetCategoryDistributionAsync()
    {
        var distribution = new List<CategoryDistribution>();

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var categoryCounts = await dbContext.Events
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category!)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToListAsync();

            var totalCount = categoryCounts.Sum(c => c.Count);

            foreach (var item in categoryCounts.OrderByDescending(c => c.Count))
            {
                distribution.Add(new CategoryDistribution
                {
                    Category = item.Category,
                    DisplayName = GetCategoryDisplayName(item.Category),
                    Count = item.Count,
                    Percentage = totalCount > 0 ? (double)item.Count / totalCount * 100 : 0,
                    Color = AnalyticsColors.GetCategoryColor(item.Category)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting category distribution");
        }

        return distribution;
    }

    public async Task<List<TimelineDensityPoint>> GetTimelineDensityAsync(DensityGranularity granularity)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var events = await dbContext.Events
                .Select(e => e.StartDate)
                .ToListAsync();

            return BuildDensityPoints(events, granularity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting timeline density");
            return new List<TimelineDensityPoint>();
        }
    }

    /// <summary>Density series from a list of start dates; pure, so the full load can reuse one read.</summary>
    internal static List<TimelineDensityPoint> BuildDensityPoints(List<DateTime> events, DensityGranularity granularity)
    {
        var points = new List<TimelineDensityPoint>();

        if (!events.Any())
            return points;

        {
            var grouped = granularity switch
            {
                DensityGranularity.Daily => events.GroupBy(d => d.Date),
                DensityGranularity.Weekly => events.GroupBy(d => StartOfWeek(d)),
                DensityGranularity.Monthly => events.GroupBy(d => new DateTime(d.Year, d.Month, 1)),
                DensityGranularity.Yearly => events.GroupBy(d => new DateTime(d.Year, 1, 1)),
                _ => events.GroupBy(d => new DateTime(d.Year, d.Month, 1))
            };

            var counts = grouped
                .Select(g => new { Date = g.Key, Count = g.Count() })
                .OrderBy(x => x.Date)
                .ToList();

            var maxCount = counts.Max(c => c.Count);

            foreach (var item in counts)
            {
                points.Add(new TimelineDensityPoint
                {
                    Date = item.Date,
                    Label = granularity switch
                    {
                        DensityGranularity.Daily => item.Date.ToString("MMM d, yyyy"),
                        DensityGranularity.Weekly => $"Week of {item.Date:MMM d, yyyy}",
                        DensityGranularity.Monthly => item.Date.ToString("MMM yyyy"),
                        DensityGranularity.Yearly => item.Date.Year.ToString(),
                        _ => item.Date.ToString("MMM yyyy")
                    },
                    EventCount = item.Count,
                    NormalizedValue = maxCount > 0 ? (double)item.Count / maxCount : 0
                });
            }
        }

        return points;
    }

    public async Task<List<TagCloudItem>> GetTagCloudAsync(int maxTags = 50)
    {
        var tagCloud = new List<TagCloudItem>();

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            // Group by the mapped TagName column, not the [NotMapped] Name alias
            var tagCounts = await dbContext.EventTags
                .GroupBy(et => new { et.TagId, et.Tag.TagName, et.Tag.Color })
                .Select(g => new { g.Key.TagId, Name = g.Key.TagName, g.Key.Color, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(maxTags)
                .ToListAsync();

            if (!tagCounts.Any())
                return tagCloud;

            var maxCount = tagCounts.Max(t => t.Count);
            var minCount = tagCounts.Min(t => t.Count);
            var range = maxCount - minCount;

            foreach (var tag in tagCounts)
            {
                // Calculate weight (1-5 scale)
                double weight = range > 0
                    ? 1 + ((double)(tag.Count - minCount) / range * 4)
                    : 3;

                // Calculate font size (12-36px)
                int fontSize = (int)(12 + (weight - 1) * 6);

                tagCloud.Add(new TagCloudItem
                {
                    TagId = tag.TagId,
                    Name = tag.Name,
                    Color = tag.Color ?? "#808080",
                    Count = tag.Count,
                    Weight = weight,
                    FontSize = fontSize
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tag cloud");
        }

        return tagCloud;
    }

    public async Task<PeopleNetwork> GetPeopleNetworkAsync()
    {
        var network = new PeopleNetwork();

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            // Get all people with their event counts
            var people = await dbContext.People
                .Include(p => p.EventPeople)
                .ToListAsync();

            if (!people.Any())
                return network;

            var maxEventCount = people.Max(p => p.EventPeople.Count);

            // Create nodes
            foreach (var person in people.Where(p => p.EventPeople.Any()))
            {
                var eventCount = person.EventPeople.Count;
                network.Nodes.Add(new PersonNode
                {
                    PersonId = person.PersonId,
                    Name = person.Name,
                    EventCount = eventCount,
                    Size = maxEventCount > 0 ? 10 + ((double)eventCount / maxEventCount * 40) : 20,
                    Color = GetDefaultPersonColor(eventCount)
                });
            }

            // Create edges (people who share events)
            var personEventMap = people
                .Where(p => p.EventPeople.Any())
                .ToDictionary(
                    p => p.PersonId,
                    p => p.EventPeople.Select(ep => ep.EventId).ToHashSet());

            var personIds = personEventMap.Keys.ToList();
            var maxSharedCount = 1;

            for (int i = 0; i < personIds.Count; i++)
            {
                for (int j = i + 1; j < personIds.Count; j++)
                {
                    var person1 = personIds[i];
                    var person2 = personIds[j];
                    var sharedEvents = personEventMap[person1]
                        .Intersect(personEventMap[person2])
                        .ToList();

                    if (sharedEvents.Any())
                    {
                        if (sharedEvents.Count > maxSharedCount)
                            maxSharedCount = sharedEvents.Count;

                        network.Edges.Add(new PersonEdge
                        {
                            SourceId = person1,
                            TargetId = person2,
                            SharedEventCount = sharedEvents.Count,
                            SharedEventIds = sharedEvents
                        });
                    }
                }
            }

            // Calculate edge weights
            foreach (var edge in network.Edges)
            {
                edge.Weight = 1 + ((double)edge.SharedEventCount / maxSharedCount * 4);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting people network");
        }

        return network;
    }

    public async Task<List<ActivityHeatmapCell>> GetActivityHeatmapAsync(HeatmapType type)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var events = await dbContext.Events
                .Select(e => e.StartDate)
                .ToListAsync();

            return BuildHeatmapCells(events, type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity heatmap");
            return new List<ActivityHeatmapCell>();
        }
    }

    /// <summary>Heatmap cells from a list of start dates; pure, so the full load can reuse one read.</summary>
    internal static List<ActivityHeatmapCell> BuildHeatmapCells(List<DateTime> events, HeatmapType type)
    {
        if (!events.Any())
            return new List<ActivityHeatmapCell>();

        return type switch
        {
            HeatmapType.DayOfWeek => GenerateDayOfWeekHeatmap(events),
            HeatmapType.MonthOfYear => GenerateMonthOfYearHeatmap(events),
            HeatmapType.YearOverTime => GenerateYearOverTimeHeatmap(events),
            _ => new List<ActivityHeatmapCell>(),
        };
    }

    private static List<ActivityHeatmapCell> GenerateDayOfWeekHeatmap(List<DateTime> events)
    {
        var cells = new List<ActivityHeatmapCell>();
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        var grouped = events
            .GroupBy(d => (int)d.DayOfWeek)
            .ToDictionary(g => g.Key, g => g.Count());

        var maxCount = grouped.Values.DefaultIfEmpty(0).Max();

        for (int day = 0; day < 7; day++)
        {
            var count = grouped.GetValueOrDefault(day, 0);
            var intensity = maxCount > 0 ? (double)count / maxCount : 0;

            cells.Add(new ActivityHeatmapCell
            {
                X = day,
                Y = 0,
                XLabel = dayNames[day],
                YLabel = "",
                Value = count,
                Intensity = intensity,
                Color = AnalyticsColors.GetHeatmapColor(intensity)
            });
        }

        return cells;
    }

    private static List<ActivityHeatmapCell> GenerateMonthOfYearHeatmap(List<DateTime> events)
    {
        var cells = new List<ActivityHeatmapCell>();
        var monthNames = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        var grouped = events
            .GroupBy(d => d.Month - 1)
            .ToDictionary(g => g.Key, g => g.Count());

        var maxCount = grouped.Values.DefaultIfEmpty(0).Max();

        for (int month = 0; month < 12; month++)
        {
            var count = grouped.GetValueOrDefault(month, 0);
            var intensity = maxCount > 0 ? (double)count / maxCount : 0;

            cells.Add(new ActivityHeatmapCell
            {
                X = month,
                Y = 0,
                XLabel = monthNames[month],
                YLabel = "",
                Value = count,
                Intensity = intensity,
                Color = AnalyticsColors.GetHeatmapColor(intensity)
            });
        }

        return cells;
    }

    private static List<ActivityHeatmapCell> GenerateYearOverTimeHeatmap(List<DateTime> events)
    {
        var cells = new List<ActivityHeatmapCell>();
        var monthNames = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        if (!events.Any())
            return cells;

        var minYear = events.Min(e => e.Year);
        var maxYear = events.Max(e => e.Year);

        var grouped = events
            .GroupBy(d => new { d.Year, d.Month })
            .ToDictionary(g => (g.Key.Year, g.Key.Month), g => g.Count());

        var maxCount = grouped.Values.DefaultIfEmpty(0).Max();

        for (int year = minYear; year <= maxYear; year++)
        {
            for (int month = 1; month <= 12; month++)
            {
                var count = grouped.GetValueOrDefault((year, month), 0);
                var intensity = maxCount > 0 ? (double)count / maxCount : 0;

                cells.Add(new ActivityHeatmapCell
                {
                    X = month - 1,
                    Y = year - minYear,
                    XLabel = monthNames[month - 1],
                    YLabel = year.ToString(),
                    Value = count,
                    Intensity = intensity,
                    Color = AnalyticsColors.GetHeatmapColor(intensity)
                });
            }
        }

        return cells;
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Sunday)) % 7;
        return date.AddDays(-diff).Date;
    }

    private static string GetCategoryDisplayName(string category) => category switch
    {
        "milestone" => "Milestone",
        "work" => "Work",
        "education" => "Education",
        "relationship" => "Relationship",
        "travel" => "Travel",
        "achievement" => "Achievement",
        "challenge" => "Challenge",
        "era" => "Era",
        "other" => "Other",
        _ => category
    };

    private static string GetDefaultPersonColor(int eventCount)
    {
        // Color based on activity level
        if (eventCount >= 10) return "#FF6B6B"; // High activity - red
        if (eventCount >= 5) return "#4ECDC4";  // Medium activity - teal
        if (eventCount >= 3) return "#45B7D1";  // Low-medium activity - blue
        return "#4A90D9";                        // Low activity - default blue
    }
}
