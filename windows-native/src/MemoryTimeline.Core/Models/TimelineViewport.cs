namespace MemoryTimeline.Core.Models;

/// <summary>
/// Timeline viewport representing the visible area and rendering context.
/// </summary>
public class TimelineViewport
{
    /// <summary>
    /// Default soft scroll boundary for panning into the past.
    /// This is NOT a limit on event visibility or CRUD operations - it only affects
    /// the scrolling behavior (soft resistance when panning past this date).
    /// Direct navigation via GoToDate or CenterOn can go to any date.
    /// </summary>
    public static readonly DateTime DefaultMinDate = new DateTime(1990, 1, 1);

    /// <summary>
    /// Default soft scroll boundary for panning into the future.
    /// This is NOT a limit on event visibility or CRUD operations.
    /// </summary>
    public static DateTime DefaultMaxDate => DateTime.Now.Date.AddDays(1);

    /// <summary>
    /// Gets or sets the minimum date soft scroll boundary for panning.
    /// Does NOT limit event visibility or direct navigation.
    /// </summary>
    public DateTime MinDate { get; set; } = DefaultMinDate;

    /// <summary>
    /// Gets or sets the maximum date soft scroll boundary for panning.
    /// Does NOT limit event visibility or direct navigation.
    /// </summary>
    public DateTime MaxDate { get; set; } = DefaultMaxDate;

    /// <summary>
    /// Gets or sets the start date of the visible viewport.
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// Gets or sets the end date of the visible viewport.
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Gets or sets the center date of the viewport (for zooming).
    /// </summary>
    public DateTime CenterDate { get; set; }

    /// <summary>
    /// Gets or sets the current zoom level.
    /// </summary>
    public ZoomLevel ZoomLevel { get; set; }

    /// <summary>
    /// Gets or sets the pixels per day at the current zoom level.
    /// </summary>
    public double PixelsPerDay { get; set; }

    /// <summary>
    /// Gets or sets the viewport width in pixels.
    /// </summary>
    public double ViewportWidth { get; set; }

    /// <summary>
    /// Gets or sets the viewport height in pixels.
    /// </summary>
    public double ViewportHeight { get; set; }

    /// <summary>
    /// Gets or sets the horizontal scroll position in pixels.
    /// </summary>
    public double ScrollPosition { get; set; }

    /// <summary>
    /// Gets the visible time span.
    /// </summary>
    public TimeSpan VisibleTimeSpan => EndDate - StartDate;

    /// <summary>
    /// Gets the total number of days visible.
    /// </summary>
    public double VisibleDays => VisibleTimeSpan.TotalDays;

    /// <summary>
    /// Converts a date to a pixel position in the viewport.
    /// </summary>
    public double DateToPixel(DateTime date)
    {
        if (date < StartDate) return 0;
        if (date > EndDate) return ViewportWidth;

        var days = (date - StartDate).TotalDays;
        return days * PixelsPerDay;
    }

    /// <summary>
    /// Converts a pixel position to a date.
    /// </summary>
    public DateTime PixelToDate(double pixel)
    {
        // Guard against divide-by-zero / non-finite scale which would otherwise
        // produce Infinity/NaN and overflow DateTime.AddDays.
        if (PixelsPerDay <= 0 || double.IsNaN(PixelsPerDay))
            return StartDate;

        var days = pixel / PixelsPerDay;
        return AddDaysClamped(StartDate, days);
    }

    /// <summary>
    /// Adds days to a date, clamping to the valid DateTime range and ignoring
    /// non-finite values so callers never throw on overflow.
    /// </summary>
    private static DateTime AddDaysClamped(DateTime origin, double days)
    {
        if (double.IsNaN(days))
            return origin;

        var maxDays = (DateTime.MaxValue - origin).TotalDays;
        var minDays = (DateTime.MinValue - origin).TotalDays;

        if (days >= maxDays) return DateTime.MaxValue;
        if (days <= minDays) return DateTime.MinValue;

        return origin.AddDays(days);
    }

    /// <summary>
    /// Checks if a date is visible in the viewport.
    /// </summary>
    public bool IsDateVisible(DateTime date)
    {
        return date >= StartDate && date <= EndDate;
    }

    /// <summary>
    /// Checks if an event is visible in the viewport. Inclusive on both ends
    /// by design, matching <see cref="TimelineCoordinateConverter.IsRangeVisible"/>
    /// (the predicate the timeline renders with): an edge-touching event is
    /// visible. No production path calls this today — it is kept as the
    /// viewport-level form of the same rule for tests and future callers.
    /// </summary>
    public bool IsEventVisible(DateTime startDate, DateTime? endDate)
    {
        var eventEnd = endDate ?? startDate;
        return (eventEnd >= StartDate) && (startDate <= EndDate);
    }

    /// <summary>
    /// Clamps a date to the viewport boundaries with soft resistance at edges.
    /// </summary>
    private DateTime ClampDate(DateTime date)
    {
        if (date < MinDate) return MinDate;
        if (date > MaxDate) return MaxDate;
        return date;
    }

    /// <summary>
    /// Creates a viewport centered on the given date.
    /// Note: This method does NOT clamp to MinDate/MaxDate boundaries.
    /// MinDate/MaxDate only affect panning behavior, not direct navigation.
    /// </summary>
    public static TimelineViewport CreateCentered(DateTime centerDate, ZoomLevel zoom, double viewportWidth)
    {
        var viewport = new TimelineViewport();
        var pixelsPerDay = TimelineScale.GetPixelsPerDay(zoom);
        var visibleDays = TimelineScale.GetVisibleDays(zoom, viewportWidth);
        var halfDays = visibleDays / 2.0;

        // Calculate viewport dates centered on the requested date
        // No clamping to MinDate/MaxDate - those are only for scroll limits
        var startDate = centerDate.AddDays(-halfDays);
        var endDate = centerDate.AddDays(halfDays);

        viewport.CenterDate = centerDate;
        viewport.StartDate = startDate;
        viewport.EndDate = endDate;
        viewport.ZoomLevel = zoom;
        viewport.PixelsPerDay = pixelsPerDay;
        viewport.ViewportWidth = viewportWidth;
        viewport.ViewportHeight = 600;
        viewport.ScrollPosition = 0;

        return viewport;
    }

    /// <summary>
    /// Updates the viewport after a zoom change, maintaining the center date.
    /// Note: Does NOT clamp to MinDate/MaxDate - those only affect panning.
    /// </summary>
    public void UpdateForZoom(ZoomLevel newZoom)
    {
        ZoomLevel = newZoom;
        PixelsPerDay = TimelineScale.GetPixelsPerDay(newZoom);

        var visibleDays = TimelineScale.GetVisibleDays(newZoom, ViewportWidth);
        var halfDays = visibleDays / 2.0;

        StartDate = CenterDate.AddDays(-halfDays);
        EndDate = CenterDate.AddDays(halfDays);
    }

    /// <summary>
    /// Pans the viewport by the given number of pixels with soft boundary resistance.
    /// </summary>
    public void Pan(double deltaPixels)
    {
        var deltaDays = deltaPixels / PixelsPerDay;
        var newStartDate = StartDate.AddDays(-deltaDays);
        var newEndDate = EndDate.AddDays(-deltaDays);
        var newCenterDate = CenterDate.AddDays(-deltaDays);

        // Apply soft boundary resistance
        const double resistanceFactor = 0.15; // How much to dampen movement at boundaries

        if (newStartDate < MinDate)
        {
            var overshoot = (MinDate - newStartDate).TotalDays;
            var dampedOvershoot = overshoot * resistanceFactor;
            newStartDate = MinDate.AddDays(-dampedOvershoot);
            newEndDate = newStartDate.AddDays(VisibleDays);
            newCenterDate = newStartDate.AddDays(VisibleDays / 2.0);
        }

        if (newEndDate > MaxDate)
        {
            var overshoot = (newEndDate - MaxDate).TotalDays;
            var dampedOvershoot = overshoot * resistanceFactor;
            newEndDate = MaxDate.AddDays(dampedOvershoot);
            newStartDate = newEndDate.AddDays(-VisibleDays);
            newCenterDate = newStartDate.AddDays(VisibleDays / 2.0);
        }

        StartDate = newStartDate;
        EndDate = newEndDate;
        CenterDate = newCenterDate;
        ScrollPosition += deltaPixels;
    }

    /// <summary>
    /// Snaps the viewport back to boundaries if it's been dragged past them.
    /// Call this after pan gestures complete to provide "snap back" effect.
    /// </summary>
    public void SnapToBoundaries()
    {
        var needsSnap = false;
        var visibleDays = VisibleDays;

        if (StartDate < MinDate)
        {
            StartDate = MinDate;
            EndDate = StartDate.AddDays(visibleDays);
            needsSnap = true;
        }

        if (EndDate > MaxDate)
        {
            EndDate = MaxDate;
            StartDate = EndDate.AddDays(-visibleDays);
            if (StartDate < MinDate) StartDate = MinDate;
            needsSnap = true;
        }

        if (needsSnap)
        {
            CenterDate = StartDate.AddDays((EndDate - StartDate).TotalDays / 2.0);
        }
    }

    /// <summary>
    /// Clamps viewport to boundaries without soft resistance.
    /// </summary>
    private void ClampToBoundaries()
    {
        var visibleDays = VisibleDays;

        if (StartDate < MinDate)
        {
            StartDate = MinDate;
            EndDate = StartDate.AddDays(visibleDays);
        }

        if (EndDate > MaxDate)
        {
            EndDate = MaxDate;
            StartDate = EndDate.AddDays(-visibleDays);
            if (StartDate < MinDate) StartDate = MinDate;
        }

        CenterDate = StartDate.AddDays((EndDate - StartDate).TotalDays / 2.0);
    }

    /// <summary>
    /// Sets the viewport to center on a specific date.
    /// Note: This method does NOT clamp to MinDate/MaxDate boundaries.
    /// MinDate/MaxDate only affect panning behavior, not direct navigation.
    /// </summary>
    public void CenterOn(DateTime date)
    {
        var visibleDays = TimelineScale.GetVisibleDays(ZoomLevel, ViewportWidth);
        var halfDays = visibleDays / 2.0;

        CenterDate = date;
        StartDate = date.AddDays(-halfDays);
        EndDate = date.AddDays(halfDays);
    }
}
