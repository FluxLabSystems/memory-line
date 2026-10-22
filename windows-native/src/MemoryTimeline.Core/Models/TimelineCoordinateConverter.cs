namespace MemoryTimeline.Core.Models;

/// <summary>
/// Centralized coordinate transformation service for timeline navigation.
/// Follows Adobe Premiere's dual coordinate system model (date-space vs screen-space).
///
/// The relationship is:
///   screenX = (date - scrollOffset).TotalDays * pixelsPerDay
///   date = scrollOffset.AddDays(screenX / pixelsPerDay)
/// </summary>
public class TimelineCoordinateConverter
{
    /// <summary>
    /// The leftmost visible date (scroll offset in date-space).
    /// </summary>
    public DateTime ScrollOffset { get; set; }

    /// <summary>
    /// Current zoom level expressed as pixels per day.
    /// </summary>
    public double PixelsPerDay { get; set; }

    /// <summary>
    /// Visible width in pixels.
    /// </summary>
    public double ViewportWidth { get; set; }

    /// <summary>
    /// Creates a converter from a TimelineViewport.
    /// </summary>
    public static TimelineCoordinateConverter FromViewport(TimelineViewport viewport)
    {
        return new TimelineCoordinateConverter
        {
            ScrollOffset = viewport.StartDate,
            PixelsPerDay = viewport.PixelsPerDay,
            ViewportWidth = viewport.ViewportWidth
        };
    }

    /// <summary>
    /// Converts a date to screen-space X coordinate.
    /// </summary>
    public double DateToScreen(DateTime date)
    {
        return (date - ScrollOffset).TotalDays * PixelsPerDay;
    }

    /// <summary>
    /// Converts a screen-space X coordinate to date.
    /// </summary>
    public DateTime ScreenToDate(double screenX)
    {
        // Guard against divide-by-zero / non-finite scale which would overflow AddDays.
        if (PixelsPerDay <= 0 || double.IsNaN(PixelsPerDay))
            return ScrollOffset;

        var days = screenX / PixelsPerDay;
        if (double.IsNaN(days))
            return ScrollOffset;

        var maxDays = (DateTime.MaxValue - ScrollOffset).TotalDays;
        var minDays = (DateTime.MinValue - ScrollOffset).TotalDays;
        if (days >= maxDays) return DateTime.MaxValue;
        if (days <= minDays) return DateTime.MinValue;

        return ScrollOffset.AddDays(days);
    }

    /// <summary>
    /// Gets the leftmost visible date.
    /// </summary>
    public DateTime VisibleStartDate => ScrollOffset;

    /// <summary>
    /// Gets the rightmost visible date.
    /// </summary>
    public DateTime VisibleEndDate => ScreenToDate(ViewportWidth);

    /// <summary>
    /// Gets the duration of the visible time span.
    /// </summary>
    public TimeSpan VisibleDuration => VisibleEndDate - VisibleStartDate;

    /// <summary>
    /// Gets the visible duration in days.
    /// </summary>
    public double VisibleDays => (PixelsPerDay <= 0 || double.IsNaN(PixelsPerDay)) ? 0 : ViewportWidth / PixelsPerDay;

    /// <summary>
    /// Checks if a date is within the visible viewport.
    /// </summary>
    public bool IsDateVisible(DateTime date)
    {
        return date >= VisibleStartDate && date <= VisibleEndDate;
    }

    /// <summary>
    /// Checks if a date range overlaps with the visible viewport. This is the
    /// visibility predicate the timeline actually renders with
    /// (TimelineService's event/era positioning).
    /// Boundaries are INCLUSIVE by design: an event whose end is exactly the
    /// visible start, or whose start is exactly the visible end, counts as
    /// visible. A pin sitting on the viewport edge is drawn rather than
    /// popping in a pixel later, and the same closed interval is what the
    /// data layer's overlap query uses, so what is loaded and what is drawn
    /// agree. A point event (null end) is a zero-length range at its start.
    /// </summary>
    public bool IsRangeVisible(DateTime start, DateTime? end)
    {
        var rangeEnd = end ?? start;
        return rangeEnd >= VisibleStartDate && start <= VisibleEndDate;
    }
}

/// <summary>
/// Configuration for time ruler tick marks with adaptive density.
/// Follows Adobe Premiere's "nice numbers" algorithm.
/// </summary>
public class TimeRulerConfig
{
    /// <summary>
    /// Major tick interval in days. Exact for <see cref="TimeRulerIntervalType.Day"/>
    /// and <see cref="TimeRulerIntervalType.Week"/>; for the calendar interval
    /// types (month, quarter, year, five-year) it is the NOMINAL length — the
    /// ruler advances those by calendar units (see <see cref="GenerateTicks"/>),
    /// so a major tick always lands on the 1st of a month / 1 January.
    /// </summary>
    public double MajorTickIntervalDays { get; set; }

    /// <summary>
    /// Number of minor ticks between major ticks.
    /// </summary>
    public int MinorTicksPerMajor { get; set; }

    /// <summary>
    /// Label format for major ticks.
    /// </summary>
    public string LabelFormat { get; set; } = "MMM yyyy";

    /// <summary>
    /// The type of interval (for formatting purposes).
    /// </summary>
    public TimeRulerIntervalType IntervalType { get; set; }

    /// <summary>
    /// Calculates the optimal ruler configuration based on current zoom level.
    /// Uses Premiere-style "nice numbers" algorithm for comfortable label spacing.
    /// </summary>
    /// <param name="pixelsPerDay">Current zoom level</param>
    /// <param name="targetPixelGap">Desired pixels between major ticks (80-150 recommended)</param>
    public static TimeRulerConfig Calculate(double pixelsPerDay, double targetPixelGap = 120)
    {
        // Calculate the time interval that fits the target pixel gap
        var targetIntervalDays = targetPixelGap / pixelsPerDay;

        // Snap to "nice" intervals based on the scale
        return targetIntervalDays switch
        {
            // Very zoomed in: show days/weeks
            < 3 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 1,
                MinorTicksPerMajor = 4, // 6-hour marks
                LabelFormat = "ddd d",
                IntervalType = TimeRulerIntervalType.Day
            },
            < 10 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 7,
                MinorTicksPerMajor = 7, // daily marks
                LabelFormat = "MMM d",
                IntervalType = TimeRulerIntervalType.Week
            },
            // Medium zoom: show weeks/months
            < 35 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 7,
                MinorTicksPerMajor = 7,
                LabelFormat = "MMM d",
                IntervalType = TimeRulerIntervalType.Week
            },
            < 60 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 30,
                MinorTicksPerMajor = 4, // roughly weekly
                LabelFormat = "MMM yyyy",
                IntervalType = TimeRulerIntervalType.Month
            },
            // Zoomed out: show months/years
            < 180 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 30,
                MinorTicksPerMajor = 2, // bi-weekly
                LabelFormat = "MMM yyyy",
                IntervalType = TimeRulerIntervalType.Month
            },
            < 400 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 90, // quarterly
                MinorTicksPerMajor = 3,
                LabelFormat = "MMM yyyy",
                IntervalType = TimeRulerIntervalType.Quarter
            },
            // Very zoomed out: show years
            < 730 => new TimeRulerConfig
            {
                MajorTickIntervalDays = 365,
                MinorTicksPerMajor = 4, // quarterly
                LabelFormat = "yyyy",
                IntervalType = TimeRulerIntervalType.Year
            },
            _ => new TimeRulerConfig
            {
                MajorTickIntervalDays = 365 * 5, // 5-year intervals
                MinorTicksPerMajor = 5, // yearly
                LabelFormat = "yyyy",
                IntervalType = TimeRulerIntervalType.FiveYear
            }
        };
    }

    /// <summary>
    /// Generates tick marks for the visible viewport, from one major interval
    /// before the aligned visible start to one after the visible end.
    /// Day and week rulers advance by fixed day counts; month, quarter, year
    /// and five-year rulers advance by CALENDAR units, so every major tick
    /// lands on a real boundary (the 1st of the month, 1 January) instead of
    /// drifting by the difference between 30 days and a month. Minor ticks
    /// subdivide each major interval: by calendar months/years when the
    /// subdivision is a whole number of them (quarter → months, year →
    /// quarters, five-year → years), otherwise evenly in time (a month's four
    /// "weekly" ticks).
    /// </summary>
    public IEnumerable<TimeRulerTick> GenerateTicks(TimelineCoordinateConverter converter)
    {
        var ticks = new List<TimeRulerTick>();
        var minorPerMajor = Math.Max(1, MinorTicksPerMajor);

        // One major before the aligned visible start, one after the visible end (buffer).
        var currentMajor = StepMajor(GetAlignedDate(converter.VisibleStartDate), -1);
        var endDate = StepMajor(converter.VisibleEndDate, 1);

        while (currentMajor <= endDate)
        {
            var nextMajor = StepMajor(currentMajor, 1);
            if (nextMajor <= currentMajor)
            {
                // Clamped at the end of the DateTime range: nothing further to draw.
                ticks.Add(CreateTick(converter, currentMajor, isMajor: true));
                break;
            }

            ticks.Add(CreateTick(converter, currentMajor, isMajor: true));
            for (var i = 1; i < minorPerMajor; i++)
            {
                ticks.Add(CreateTick(converter, MinorTickDate(currentMajor, nextMajor, i, minorPerMajor), isMajor: false));
            }

            currentMajor = nextMajor;
        }

        return ticks;
    }

    private TimeRulerTick CreateTick(TimelineCoordinateConverter converter, DateTime date, bool isMajor)
    {
        return new TimeRulerTick
        {
            Date = date,
            ScreenX = converter.DateToScreen(date),
            IsMajor = isMajor,
            Label = isMajor ? date.ToString(LabelFormat) : null
        };
    }

    /// <summary>Months in one major interval for the calendar interval types; 0 for day/week.</summary>
    private int MajorIntervalMonths => IntervalType switch
    {
        TimeRulerIntervalType.Month => 1,
        TimeRulerIntervalType.Quarter => 3,
        TimeRulerIntervalType.Year => 12,
        TimeRulerIntervalType.FiveYear => 60,
        _ => 0
    };

    /// <summary>Moves a date by <paramref name="count"/> major intervals (calendar-aware), clamped to the DateTime range.</summary>
    private DateTime StepMajor(DateTime date, int count)
    {
        var months = MajorIntervalMonths;
        return months > 0
            ? AddMonthsClamped(date, months * count)
            : AddDaysClamped(date, MajorTickIntervalDays * count);
    }

    /// <summary>
    /// The <paramref name="index"/>-th of <paramref name="count"/> minor ticks
    /// between two consecutive major ticks: a whole number of calendar months
    /// when the interval divides that way, else an even share of the real span.
    /// </summary>
    private DateTime MinorTickDate(DateTime major, DateTime nextMajor, int index, int count)
    {
        var months = MajorIntervalMonths;
        if (months > 0 && months % count == 0)
        {
            return AddMonthsClamped(major, months / count * index);
        }

        var spanTicks = (nextMajor - major).Ticks;
        return major.AddTicks(spanTicks / count * index);
    }

    private static DateTime AddMonthsClamped(DateTime date, int months)
    {
        try
        {
            return date.AddMonths(months);
        }
        catch (ArgumentOutOfRangeException)
        {
            return months > 0 ? DateTime.MaxValue : DateTime.MinValue;
        }
    }

    private static DateTime AddDaysClamped(DateTime date, double days)
    {
        try
        {
            return date.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return days > 0 ? DateTime.MaxValue : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Aligns a date to the interval boundary at or before it (midnight, the
    /// week's Sunday, the 1st of the month/quarter, 1 January, or 1 January
    /// of the five-year block).
    /// </summary>
    private DateTime GetAlignedDate(DateTime date)
    {
        return IntervalType switch
        {
            TimeRulerIntervalType.Day => date.Date,
            TimeRulerIntervalType.Week => AddDaysClamped(date.Date, -(int)date.DayOfWeek),
            TimeRulerIntervalType.Month => new DateTime(date.Year, date.Month, 1),
            TimeRulerIntervalType.Quarter => new DateTime(date.Year, ((date.Month - 1) / 3) * 3 + 1, 1),
            TimeRulerIntervalType.Year => new DateTime(date.Year, 1, 1),
            TimeRulerIntervalType.FiveYear => new DateTime(Math.Max(1, (date.Year / 5) * 5), 1, 1),
            _ => date.Date
        };
    }
}

/// <summary>
/// Type of interval for time ruler formatting.
/// </summary>
public enum TimeRulerIntervalType
{
    Day,
    Week,
    Month,
    Quarter,
    Year,
    FiveYear
}

/// <summary>
/// Represents a single tick mark on the time ruler.
/// </summary>
public class TimeRulerTick
{
    /// <summary>
    /// The date this tick represents.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Screen X position in pixels.
    /// </summary>
    public double ScreenX { get; set; }

    /// <summary>
    /// Whether this is a major tick (with label) or minor tick.
    /// </summary>
    public bool IsMajor { get; set; }

    /// <summary>
    /// Label text for major ticks, null for minor ticks.
    /// </summary>
    public string? Label { get; set; }
}

/// <summary>
/// Handles zoom operations with anchor point preservation.
/// Like Premiere, zooming can be anchored to cursor position, center, or playhead.
/// </summary>
public static class ZoomHelper
{
    /// <summary>
    /// Zoom base for exponential scaling (per wheel tick).
    /// Lower = slower zoom, Higher = faster zoom. Premiere typically uses 1.1 to 1.3.
    /// </summary>
    public const double ZoomBase = 1.15;

    /// <summary>
    /// Standard Windows wheel delta per tick.
    /// </summary>
    public const double WheelDeltaNormalization = 120.0;

    /// <summary>
    /// Performs cursor-anchored zoom following Adobe Premiere's algorithm.
    /// The timecode under the cursor stays visually fixed while the timeline expands/contracts.
    /// </summary>
    /// <param name="viewport">Current viewport state</param>
    /// <param name="cursorScreenX">Cursor X position in pixels relative to viewport</param>
    /// <param name="wheelDelta">Raw mouse wheel delta (typically ±120 per tick)</param>
    /// <param name="minPixelsPerDay">Minimum zoom level (most zoomed out)</param>
    /// <param name="maxPixelsPerDay">Maximum zoom level (most zoomed in)</param>
    /// <returns>New viewport state after zoom</returns>
    public static (DateTime newStartDate, double newPixelsPerDay) CalculateCursorAnchoredZoom(
        TimelineViewport viewport,
        double cursorScreenX,
        double wheelDelta,
        double minPixelsPerDay = 0.01,
        double maxPixelsPerDay = 100.0)
    {
        // Normalize wheel delta (Windows reports ±120 per tick)
        var normalizedDelta = wheelDelta / WheelDeltaNormalization;

        // Step 1: Convert cursor position to date-space (anchor point)
        var anchorDate = viewport.PixelToDate(cursorScreenX);

        // Step 2: Calculate cursor's fractional position within viewport (0.0 = left, 1.0 = right)
        var cursorFraction = cursorScreenX / viewport.ViewportWidth;
        cursorFraction = Math.Clamp(cursorFraction, 0.0, 1.0);

        // Step 3: Calculate new pixels per day using exponential scaling
        // Positive delta = zoom in (more pixels per day)
        var zoomFactor = Math.Pow(ZoomBase, normalizedDelta);
        var newPixelsPerDay = viewport.PixelsPerDay * zoomFactor;

        // Step 4: Clamp to zoom limits
        newPixelsPerDay = Math.Clamp(newPixelsPerDay, minPixelsPerDay, maxPixelsPerDay);

        // Step 5: Calculate new visible duration
        var newVisibleDays = viewport.ViewportWidth / newPixelsPerDay;

        // Step 6: Calculate new start date to keep anchor at same screen position
        // Key insight: anchorDate must remain at cursorFraction of the new viewport
        var newStartDate = anchorDate.AddDays(-cursorFraction * newVisibleDays);

        return (newStartDate, newPixelsPerDay);
    }

    /// <summary>
    /// Performs center-anchored zoom (traditional zoom behavior).
    /// </summary>
    public static (DateTime newStartDate, double newPixelsPerDay) CalculateCenterAnchoredZoom(
        TimelineViewport viewport,
        double wheelDelta,
        double minPixelsPerDay = 0.01,
        double maxPixelsPerDay = 100.0)
    {
        // Anchor at center of viewport
        var centerScreenX = viewport.ViewportWidth / 2.0;
        return CalculateCursorAnchoredZoom(viewport, centerScreenX, wheelDelta, minPixelsPerDay, maxPixelsPerDay);
    }

    /// <summary>
    /// Zooms the viewport while keeping a specific date at the same screen position.
    /// This is the Premiere-style zoom anchoring behavior for discrete zoom levels.
    /// </summary>
    /// <param name="viewport">Current viewport</param>
    /// <param name="anchorDate">Date to keep at same screen position</param>
    /// <param name="newZoomLevel">New zoom level</param>
    public static void ZoomCenteredOn(TimelineViewport viewport, DateTime anchorDate, ZoomLevel newZoomLevel)
    {
        // Calculate where anchor currently sits as fraction of viewport (0 = left edge, 1 = right edge)
        var visibleDays = viewport.VisibleDays;
        var anchorFraction = visibleDays > 0
            ? (anchorDate - viewport.StartDate).TotalDays / visibleDays
            : 0.5;

        // Clamp to valid range (also normalizes any NaN/Infinity to the center)
        if (double.IsNaN(anchorFraction) || double.IsInfinity(anchorFraction))
            anchorFraction = 0.5;
        anchorFraction = Math.Max(0, Math.Min(1, anchorFraction));

        // Get new pixels per day
        var newPixelsPerDay = TimelineScale.GetPixelsPerDay(newZoomLevel);
        var newVisibleDays = viewport.ViewportWidth / newPixelsPerDay;

        // Calculate new scroll offset to keep anchor at same screen position
        var newStartDate = anchorDate.AddDays(-anchorFraction * newVisibleDays);

        // Update viewport
        viewport.ZoomLevel = newZoomLevel;
        viewport.PixelsPerDay = newPixelsPerDay;
        viewport.StartDate = newStartDate;
        viewport.EndDate = newStartDate.AddDays(newVisibleDays);
        viewport.CenterDate = newStartDate.AddDays(newVisibleDays / 2);
    }

    /// <summary>
    /// Zooms the viewport while keeping the center date fixed.
    /// </summary>
    public static void ZoomCenteredOnCenter(TimelineViewport viewport, ZoomLevel newZoomLevel)
    {
        ZoomCenteredOn(viewport, viewport.CenterDate, newZoomLevel);
    }

    /// <summary>
    /// Zooms the viewport while keeping a screen position fixed.
    /// </summary>
    public static void ZoomCenteredOnScreenPosition(TimelineViewport viewport, double screenX, ZoomLevel newZoomLevel)
    {
        var anchorDate = viewport.PixelToDate(screenX);
        ZoomCenteredOn(viewport, anchorDate, newZoomLevel);
    }

    /// <summary>
    /// Gets the closest discrete zoom level for a given pixels per day value.
    /// </summary>
    public static ZoomLevel GetClosestZoomLevel(double pixelsPerDay)
    {
        var levels = new[] { ZoomLevel.Year, ZoomLevel.Month, ZoomLevel.Week, ZoomLevel.Day };
        ZoomLevel closest = ZoomLevel.Month;
        double closestDiff = double.MaxValue;

        foreach (var level in levels)
        {
            var levelPpd = TimelineScale.GetPixelsPerDay(level);
            var diff = Math.Abs(pixelsPerDay - levelPpd);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                closest = level;
            }
        }

        return closest;
    }
}
