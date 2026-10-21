using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for timeline operations.
/// </summary>
public interface ITimelineService
{
    Task<IEnumerable<TimelineEventDto>> GetEventsForViewportAsync(TimelineViewport viewport);
    Task<IEnumerable<TimelineEraDto>> GetErasForViewportAsync(TimelineViewport viewport);
    Task<TimelineViewport> CreateViewportAsync(ZoomLevel zoomLevel, DateTime centerDate, int viewportWidth, int viewportHeight);
    Task<TimelineViewport> ZoomInAsync(TimelineViewport currentViewport, DateTime? centerDate = null);
    Task<TimelineViewport> ZoomOutAsync(TimelineViewport currentViewport, DateTime? centerDate = null);
    Task<TimelineViewport> PanAsync(TimelineViewport currentViewport, double pixelOffset);
    Task<DateTime> GetEarliestEventDateAsync();
    Task<DateTime> GetLatestEventDateAsync();
    void CalculateEventPositions(IEnumerable<TimelineEventDto> events, TimelineViewport viewport);
    void CalculateEraPositions(IEnumerable<TimelineEraDto> eras, TimelineViewport viewport);
}

/// <summary>
/// Timeline service implementation.
/// </summary>
public class TimelineService : ITimelineService
{
    /// <summary>
    /// Pixels of span-bar overdraw kept past each viewport edge. A span's
    /// RENDERED geometry (<see cref="TimelineEventDto.RenderX"/>/<see
    /// cref="TimelineEventDto.RenderWidth"/>) is clamped to the viewport plus
    /// this margin - mirroring the uncertainty band's clamp - so a
    /// decades-long event at Day zoom cannot materialize a multi-million-pixel
    /// XAML element. The margin keeps rounded corners and pointer targets
    /// just off-screen instead of cutting the bar exactly at the edge.
    /// </summary>
    public const double SpanRenderMargin = 200.0;

    private readonly IEventRepository _eventRepository;
    private readonly IEraRepository _eraRepository;
    private readonly IEventMediaRepository? _mediaRepository;
    private readonly ILogger<TimelineService> _logger;

    public TimelineService(
        IEventRepository eventRepository,
        IEraRepository eraRepository,
        ILogger<TimelineService> logger,
        IEventMediaRepository? mediaRepository = null)
    {
        _eventRepository = eventRepository;
        _eraRepository = eraRepository;
        _logger = logger;
        _mediaRepository = mediaRepository;
    }

    /// <summary>
    /// Gets events visible in the current viewport.
    /// </summary>
    public async Task<IEnumerable<TimelineEventDto>> GetEventsForViewportAsync(TimelineViewport viewport)
    {
        try
        {
            // Add buffer to load events slightly outside viewport
            var bufferDays = 30;
            var startDate = viewport.StartDate.AddDays(-bufferDays);
            var endDate = viewport.EndDate.AddDays(bufferDays);

            var events = await _eventRepository.GetByDateRangeAsync(startDate, endDate);
            var dtos = events.Select(TimelineEventDto.FromEvent).ToList();

            CalculateEventPositions(dtos, viewport);

            // Media counts for the pins' photo badges: ONE batched query for
            // the whole viewport, never per-event. Badge data is decorative,
            // so a failure only logs and hides the badges.
            if (_mediaRepository != null && dtos.Count > 0)
            {
                try
                {
                    var counts = await _mediaRepository.GetCountsForEventsAsync(dtos.Select(d => d.EventId));
                    foreach (var dto in dtos)
                    {
                        if (counts.TryGetValue(dto.EventId, out var count))
                        {
                            dto.MediaCount = count;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load media counts for the viewport; photo badges will be hidden.");
                }
            }

            _logger.LogDebug("Loaded {Count} events for viewport", dtos.Count);
            return dtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events for viewport");
            throw;
        }
    }

    /// <summary>
    /// Gets eras visible in the current viewport.
    /// </summary>
    public async Task<IEnumerable<TimelineEraDto>> GetErasForViewportAsync(TimelineViewport viewport)
    {
        try
        {
            var allEras = await _eraRepository.GetAllAsync();
            var dtos = allEras
                .Where(era => era.StartDate <= viewport.EndDate &&
                             (era.EndDate == null || era.EndDate >= viewport.StartDate))
                .Select(TimelineEraDto.FromEra)
                .ToList();

            CalculateEraPositions(dtos, viewport);

            _logger.LogDebug("Loaded {Count} eras for viewport", dtos.Count);
            return dtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting eras for viewport");
            throw;
        }
    }

    /// <summary>
    /// Creates a new viewport with specified parameters.
    /// </summary>
    public async Task<TimelineViewport> CreateViewportAsync(
        ZoomLevel zoomLevel,
        DateTime centerDate,
        int viewportWidth,
        int viewportHeight)
    {
        try
        {
            // If no center date provided, calculate from events
            if (centerDate == DateTime.MinValue)
            {
                var earliest = await GetEarliestEventDateAsync();
                var latest = await GetLatestEventDateAsync();

                if (earliest != DateTime.MinValue && latest != DateTime.MaxValue)
                {
                    centerDate = earliest.AddDays((latest - earliest).TotalDays / 2);
                }
                else
                {
                    centerDate = DateTime.Now;
                }
            }

            // Use the new TimelineViewport.CreateCentered method
            var viewport = TimelineViewport.CreateCentered(centerDate, zoomLevel, viewportWidth);
            viewport.ViewportHeight = viewportHeight;

            return viewport;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating viewport");
            throw;
        }
    }

    /// <summary>
    /// Zooms in the viewport.
    /// </summary>
    public async Task<TimelineViewport> ZoomInAsync(TimelineViewport currentViewport, DateTime? centerDate = null)
    {
        try
        {
            if (!TimelineScale.CanZoomIn(currentViewport.ZoomLevel))
            {
                _logger.LogDebug("Already at maximum zoom level");
                return currentViewport;
            }

            var newZoomLevel = TimelineScale.ZoomIn(currentViewport.ZoomLevel);
            var center = centerDate ?? currentViewport.CenterDate;

            return await CreateViewportAsync(
                newZoomLevel,
                center,
                (int)currentViewport.ViewportWidth,
                (int)currentViewport.ViewportHeight);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error zooming in");
            throw;
        }
    }

    /// <summary>
    /// Zooms out the viewport.
    /// </summary>
    public async Task<TimelineViewport> ZoomOutAsync(TimelineViewport currentViewport, DateTime? centerDate = null)
    {
        try
        {
            if (!TimelineScale.CanZoomOut(currentViewport.ZoomLevel))
            {
                _logger.LogDebug("Already at minimum zoom level");
                return currentViewport;
            }

            var newZoomLevel = TimelineScale.ZoomOut(currentViewport.ZoomLevel);
            var center = centerDate ?? currentViewport.CenterDate;

            return await CreateViewportAsync(
                newZoomLevel,
                center,
                (int)currentViewport.ViewportWidth,
                (int)currentViewport.ViewportHeight);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error zooming out");
            throw;
        }
    }

    /// <summary>
    /// Pans the viewport by pixel offset.
    /// </summary>
    public Task<TimelineViewport> PanAsync(TimelineViewport currentViewport, double pixelOffset)
    {
        try
        {
            // Create a copy and pan it
            var newViewport = new TimelineViewport
            {
                StartDate = currentViewport.StartDate,
                EndDate = currentViewport.EndDate,
                CenterDate = currentViewport.CenterDate,
                ZoomLevel = currentViewport.ZoomLevel,
                PixelsPerDay = currentViewport.PixelsPerDay,
                ViewportWidth = currentViewport.ViewportWidth,
                ViewportHeight = currentViewport.ViewportHeight,
                ScrollPosition = currentViewport.ScrollPosition
            };

            newViewport.Pan(pixelOffset);

            return Task.FromResult(newViewport);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error panning viewport");
            throw;
        }
    }

    /// <summary>
    /// Gets the earliest event date.
    /// </summary>
    public async Task<DateTime> GetEarliestEventDateAsync()
    {
        try
        {
            var events = await _eventRepository.GetAllAsync();
            return events.Any() ? events.Min(e => e.StartDate) : DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting earliest event date");
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Gets the latest event date.
    /// </summary>
    public async Task<DateTime> GetLatestEventDateAsync()
    {
        try
        {
            var events = await _eventRepository.GetAllAsync();
            return events.Any() ? events.Max(e => e.EndDate ?? e.StartDate) : DateTime.MaxValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting latest event date");
            return DateTime.MaxValue;
        }
    }

    /// <summary>
    /// Calculates pixel positions for events based on viewport.
    /// Events are displayed as map pins pointing down toward the era bars.
    /// Uses the TimelineCoordinateConverter for Premiere-style coordinate transformations.
    ///
    /// New layout: Events area at top, era bars in middle, timeline axis at bottom.
    /// Pin tips point downward toward the era bars baseline.
    /// </summary>
    public void CalculateEventPositions(IEnumerable<TimelineEventDto> events, TimelineViewport viewport)
    {
        // Pin dimensions
        const double pinWidth = 30.0;
        const double pinHeight = 40.0;
        const double pinSpacing = 5.0; // Spacing between stacked pins
        const double spanHeight = 24.0; // Rounded span bar height

        // The baseline is near the bottom of the events area (above era bars).
        // Era bars area is ~40px, timeline axis is 80px, so events area is viewport height - 120px.
        // Position pins so their tips point to the baseline.
        var eventsAreaHeight = Math.Max(200, viewport.ViewportHeight - 120);
        var baselineY = eventsAreaHeight - 10; // 10px padding from bottom

        // Use the coordinate converter for date-to-screen transformations
        var converter = TimelineCoordinateConverter.FromViewport(viewport);
        var maxVisibleX = Math.Max(0.0, viewport.ViewportWidth);

        foreach (var evt in events.OrderBy(e => e.StartDate))
        {
            var datePixelX = converter.DateToScreen(evt.StartDate);

            // Duration-proportional width at the current scale. The
            // pixels-per-day overload keeps span edges aligned with pin
            // positions when the viewport sits between discrete zoom levels
            // (continuous cursor zoom); at a discrete level it equals
            // TimelineScale.GetEventWidth(start, end, viewport.ZoomLevel).
            var proportionalWidth = TimelineScale.GetEventWidth(
                evt.StartDate, evt.EndDate, viewport.PixelsPerDay, viewport.ZoomLevel);

            if (evt.EndDate.HasValue && proportionalWidth > pinWidth)
            {
                // Span: left edge anchored on the start date, real width.
                evt.RenderMode = EventRenderMode.Span;
                evt.PixelX = datePixelX;
                evt.Width = proportionalWidth;
                evt.Height = spanHeight;

                // Render geometry: the XAML element is clamped to the
                // viewport plus SpanRenderMargin so long spans at deep zoom
                // never materialize at their true (possibly multi-million
                // pixel) width. PixelX/Width keep the TRUE values for the
                // track-overlap math below.
                var renderLeft = Math.Clamp(evt.PixelX, -SpanRenderMargin, maxVisibleX + SpanRenderMargin);
                var renderRight = Math.Clamp(evt.PixelX + evt.Width, -SpanRenderMargin, maxVisibleX + SpanRenderMargin);
                evt.RenderX = renderLeft;
                evt.RenderWidth = renderRight - renderLeft;

                // Overhang past the viewport edges drives the end-cap
                // chevrons. Measured from the CLAMPED render geometry (so it
                // caps at SpanRenderMargin): EventSpanBar insets its chevrons
                // and title by these amounts relative to the rendered element
                // edge, which places them exactly at the visible edge.
                evt.SpanLeftOverhang = Math.Max(0, -renderLeft);
                evt.SpanRightOverhang = Math.Max(0, renderRight - maxVisibleX);
            }
            else
            {
                // Pin: fixed size, centered on the start date. Duration events
                // too short to exceed the pin width stay pins.
                evt.RenderMode = EventRenderMode.Pin;
                evt.PixelX = datePixelX - (pinWidth / 2);
                evt.Width = pinWidth;
                evt.Height = pinHeight;
                evt.RenderX = evt.PixelX;
                evt.RenderWidth = evt.Width;
                evt.SpanLeftOverhang = 0;
                evt.SpanRightOverhang = 0;
            }

            // Check visibility using coordinate converter
            evt.IsInViewport = converter.IsRangeVisible(evt.StartDate, evt.EndDate);

            // Uncertainty window (F1 integration): pixel bounds of the
            // DatePrecision window for events coarser than Month, clamped to
            // the viewport so a decade window can't produce a megapixel-wide
            // rect. Inside the viewport the bounds equal GetWindow exactly.
            if (evt.IsApproximate)
            {
                var (earliest, latest) = DatePrecisionExtensions.GetWindow(evt.StartDate, evt.DatePrecision);

                // The band is gated on the WINDOW's visibility, not the
                // anchor's: a "Summer 1998" band must not pop off mid-pan
                // while June 1998 is still on screen just because the anchor
                // date crossed the viewport edge.
                evt.IsWindowInViewport = latest >= viewport.StartDate && earliest <= viewport.EndDate;
                evt.WindowStartX = Math.Clamp(converter.DateToScreen(earliest), 0.0, maxVisibleX);
                evt.WindowEndX = Math.Clamp(converter.DateToScreen(latest), 0.0, maxVisibleX);
            }
            else
            {
                evt.IsWindowInViewport = false;
                evt.WindowStartX = 0;
                evt.WindowEndX = 0;
            }
        }

        // Use tracks to stack overlapping items above each other. The overlap
        // math in FindAvailableTrack uses each event's REAL width
        // (PixelX + Width), so wide spans claim their full horizontal extent.
        var tracks = new List<List<TimelineEventDto>>();

        foreach (var evt in events.OrderBy(e => e.StartDate))
        {
            // Find a track for this event
            var trackIndex = FindAvailableTrack(tracks, evt);
            // Position so pin tip touches the baseline, stacking upward for overlapping events
            evt.PixelY = baselineY - pinHeight - (trackIndex * (pinHeight + pinSpacing));
        }
    }

    /// <summary>
    /// Calculates pixel positions for eras based on viewport.
    /// Uses the TimelineCoordinateConverter for Premiere-style coordinate transformations.
    /// </summary>
    public void CalculateEraPositions(IEnumerable<TimelineEraDto> eras, TimelineViewport viewport)
    {
        var converter = TimelineCoordinateConverter.FromViewport(viewport);

        foreach (var era in eras)
        {
            era.PixelX = converter.DateToScreen(era.StartDate);

            // An era with no end date is ONGOING: it runs to the edge of the
            // view (that is also what gives it its width). IsRangeVisible treats
            // a null end as a point event, which is right for events and wrong
            // here - it made every ongoing era disappear the moment the view
            // scrolled past its start.
            var endDate = era.EndDate ?? viewport.EndDate;
            var endPixel = converter.DateToScreen(endDate);

            era.Width = Math.Max(0, endPixel - era.PixelX);
            era.IsVisible = era.EndDate.HasValue
                ? converter.IsRangeVisible(era.StartDate, era.EndDate)
                : era.StartDate <= converter.VisibleEndDate;
        }
    }

    private int FindAvailableTrack(List<List<TimelineEventDto>> tracks, TimelineEventDto newEvent)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var hasOverlap = track.Any(e =>
            {
                var eEnd = e.PixelX + e.Width;
                var newEnd = newEvent.PixelX + newEvent.Width;
                return !(newEvent.PixelX >= eEnd || newEnd <= e.PixelX);
            });

            if (!hasOverlap)
            {
                track.Add(newEvent);
                return i;
            }
        }

        // Create new track
        var newTrack = new List<TimelineEventDto> { newEvent };
        tracks.Add(newTrack);
        return tracks.Count - 1;
    }
}
