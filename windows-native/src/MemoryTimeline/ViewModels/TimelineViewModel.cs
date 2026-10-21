using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the timeline view.
/// </summary>
public partial class TimelineViewModel : ObservableObject
{
    private readonly ITimelineService _timelineService;
    private readonly IEventService _eventService;
    private readonly IPersonService _personService;
    private readonly IDraftService _draftService;
    private readonly IMediaService _mediaService;
    private readonly IEraRepository _eraRepository;
    private readonly ITagRepository _tagRepository;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<TimelineViewModel> _logger;

    // The singleton ViewModel keeps its zoom across navigation; the stored
    // default_zoom_level is applied only to the FIRST initialization so a
    // zoom the user chose in this session is not reset on every page visit.
    private bool _defaultZoomApplied;

    // Captured at construction time (the ViewModel is created on the UI thread) so
    // messenger callbacks arriving from background threads can marshal to the UI thread.
    private readonly DispatcherQueue? _dispatcherQueue;

    [ObservableProperty]
    private TimelineViewport? _viewport;

    [ObservableProperty]
    private ObservableCollection<TimelineEventDto> _events = new();

    [ObservableProperty]
    private ObservableCollection<TimelineEraDto> _eras = new();

    [ObservableProperty]
    private ObservableCollection<TimeRulerTickDto> _timeRulerTicks = new();

    [ObservableProperty]
    private ObservableCollection<EraBarDto> _eraBars = new();

    [ObservableProperty]
    private ObservableCollection<EraFilterDto> _eraFilters = new();

    /// <summary>
    /// Gets the visible era bars (filtered by user selection).
    /// </summary>
    public IEnumerable<EraBarDto> VisibleEraBars =>
        EraBars.Where(eb => EraFilters.FirstOrDefault(f => f.EraId == eb.EraId)?.IsVisible ?? true);

    /// <summary>
    /// Gets the height needed for era bars based on number of tracks.
    /// </summary>
    public double EraBarsHeight => Math.Max(20, _eraBarTrackCount * 8);

    private int _eraBarTrackCount = 1;

    /// <summary>
    /// Duration events rendered as span bars (RenderMode == Span). Pins stay
    /// in <see cref="Events"/>; the control renders the two layers separately.
    /// </summary>
    public ObservableCollection<TimelineEventDto> SpanEvents { get; } = new();

    /// <summary>
    /// Events with approximate dates (precision coarser than Month) that get a
    /// soft gradient uncertainty underlay behind their pin/span.
    /// </summary>
    public ObservableCollection<TimelineEventDto> UncertainEvents { get; } = new();

    /// <summary>Rows of the swimlane gutter panel (empty in Auto mode).</summary>
    public ObservableCollection<LaneGutterRowDto> LaneGutterRows { get; } = new();

    /// <summary>
    /// The active swimlane mode. Auto keeps the classic track stacking; other
    /// modes re-layout the ALREADY-LOADED events into horizontal lanes without
    /// a database reload (no flicker, no scroll-position loss).
    /// </summary>
    [ObservableProperty]
    private LaneMode _laneMode = LaneMode.Auto;

    /// <summary>True when a swimlane mode (not Auto) is active; shows the gutter.</summary>
    public bool IsLaneModeActive => LaneMode != LaneMode.Auto;

    partial void OnLaneModeChanged(LaneMode value)
    {
        OnPropertyChanged(nameof(IsLaneModeActive));
        _collapsedLaneKeys.Clear();

        // Lane switches re-layout from the cached DTOs - no DB work here.
        // Positions update in place through the DTOs' observable properties,
        // so nothing is torn down and the scroll position is untouched.
        if (Viewport != null && _loadedEventDtos.Count > 0)
        {
            // Restore the Auto stacking geometry first; lane geometry (if any)
            // then overrides PixelY per lane.
            _timelineService.CalculateEventPositions(_loadedEventDtos, Viewport);
        }

        RecomputeLanes();
        ApplyLaneGeometry();
        RebuildLaneGutter();

        // Notify other views; a subscriber failure must not break the switch.
        try
        {
            WeakReferenceMessenger.Default.Send(new LaneModeChangedMessage(value.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error publishing LaneModeChangedMessage");
        }
    }

    // All DTOs fetched by the last settled reload (including the ±30-day
    // buffer); the pin/span/uncertainty collections are projections of this.
    private List<TimelineEventDto> _loadedEventDtos = new();

    // All era DTOs fetched by the last settled reload.
    private List<TimelineEraDto> _loadedEras = new();

    // Lane membership computed on the reload path (never per scroll tick);
    // pan/zoom ticks only re-apply the cached assignment's geometry.
    private List<TimelineLane> _lanes = new();

    // Collapsed lane keys for the CURRENT mode; cleared on mode change,
    // deliberately not persisted anywhere.
    private readonly HashSet<string> _collapsedLaneKeys = new(StringComparer.OrdinalIgnoreCase);

    // Last-request-wins debounce for viewport reloads: pan/zoom ticks shift
    // the already-loaded DTOs synchronously and the DB reload fires once,
    // ~150ms after the LAST tick (O(gestures) reloads instead of O(ticks)).
    private readonly ReloadCoalescer _reloadCoalescer = new();

    // Monotonic sequences: a load that completes after a newer load started
    // drops its stale results instead of clobbering them.
    private long _eventLoadSequence;
    private long _eraLoadSequence;

    /// <summary>Number of loaded events currently visible in the viewport.</summary>
    private int VisibleEventCount => _loadedEventDtos.Count(e => e.IsVisible);

    [ObservableProperty]
    private ZoomLevel _currentZoomLevel = ZoomLevel.Month;

    partial void OnCurrentZoomLevelChanged(ZoomLevel value)
    {
        // Notify zoom level boolean properties when zoom level changes
        OnPropertyChanged(nameof(IsYearZoom));
        OnPropertyChanged(nameof(IsMonthZoom));
        OnPropertyChanged(nameof(IsWeekZoom));
        OnPropertyChanged(nameof(IsDayZoom));
        OnPropertyChanged(nameof(CurrentZoomLevelDisplay));
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Ready";

    /// <summary>
    /// User-visible error message. Non-null when the last operation failed;
    /// surfaced in the UI as an error InfoBar. Cleared (set to null) when a
    /// new operation starts or the user dismisses the InfoBar.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _totalEventCount;

    [ObservableProperty]
    private TimelineEventDto? _selectedEvent;

    /// <summary>
    /// Media attachments of the selected event, shown as the details-panel
    /// photo strip (ordered by sort order). Loaded lazily on selection.
    /// </summary>
    public ObservableCollection<EventMediaThumbDto> SelectedEventMedia { get; } = new();

    /// <summary>Header text for the details-panel photo strip ("Photos (3)").</summary>
    [ObservableProperty]
    private string _selectedEventMediaHeader = "Photos";

    partial void OnSelectedEventChanged(TimelineEventDto? value)
    {
        if (value == null)
        {
            SelectedEventMedia.Clear();
            SelectedEventMediaHeader = "Photos";
        }
    }

    // Viewport dimensions
    [ObservableProperty]
    private int _viewportWidth = 1000;

    [ObservableProperty]
    private int _viewportHeight = 600;

    // Zoom level display properties
    public bool IsYearZoom => CurrentZoomLevel == ZoomLevel.Year;
    public bool IsMonthZoom => CurrentZoomLevel == ZoomLevel.Month;
    public bool IsWeekZoom => CurrentZoomLevel == ZoomLevel.Week;
    public bool IsDayZoom => CurrentZoomLevel == ZoomLevel.Day;
    public string CurrentZoomLevelDisplay => TimelineScale.GetZoomLevelName(CurrentZoomLevel);
    public bool CanZoomIn => TimelineScale.CanZoomIn(CurrentZoomLevel);
    public bool CanZoomOut => TimelineScale.CanZoomOut(CurrentZoomLevel);

    public TimelineViewModel(
        ITimelineService timelineService,
        IEventService eventService,
        IPersonService personService,
        IDraftService draftService,
        IMediaService mediaService,
        IEraRepository eraRepository,
        ITagRepository tagRepository,
        ISettingsService settingsService,
        ILogger<TimelineViewModel> logger)
    {
        _timelineService = timelineService;
        _eventService = eventService;
        _personService = personService;
        _draftService = draftService;
        _mediaService = mediaService;
        _eraRepository = eraRepository;
        _tagRepository = tagRepository;
        _settingsService = settingsService;
        _logger = logger;

        // The ViewModel is constructed on the UI thread; capture its dispatcher so
        // messages arriving from background threads can be marshalled back.
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // React to events created anywhere in the app (Add Event dialog, review
        // approval, imports) so the timeline updates without renavigation.
        WeakReferenceMessenger.Default.Register<EventCreatedMessage>(this, static (recipient, message) =>
        {
            ((TimelineViewModel)recipient).OnEventCreated(message);
        });

        // React to edits/deletes made elsewhere (e.g. the Search page) so the
        // timeline never shows stale or ghost events. Our own CRUD paths run
        // with IsLoading set and reload themselves, so the handlers skip them.
        WeakReferenceMessenger.Default.Register<EventUpdatedMessage>(this, static (recipient, message) =>
        {
            ((TimelineViewModel)recipient).OnEventUpdated(message);
        });

        WeakReferenceMessenger.Default.Register<EventDeletedMessage>(this, static (recipient, message) =>
        {
            ((TimelineViewModel)recipient).OnEventDeleted(message);
        });

        // React to media attachments made anywhere in the app so the pin's
        // photo-count badge and the details-panel strip stay current.
        WeakReferenceMessenger.Default.Register<MediaAttachedMessage>(this, static (recipient, message) =>
        {
            ((TimelineViewModel)recipient).OnMediaAttached(message);
        });
    }

    /// <summary>
    /// Handles a <see cref="MediaAttachedMessage"/> from any thread by
    /// marshalling the refresh onto the UI thread.
    /// </summary>
    private void OnMediaAttached(MediaAttachedMessage message)
    {
        void Handle() => _ = HandleMediaAttachedAsync(message);

        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            Handle();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Handle);
        }
    }

    /// <summary>
    /// Refreshes the media count badge for the event a file was just attached
    /// to and, when that event is selected, rebuilds the details-panel strip.
    /// Re-queries the exact count instead of incrementing so overlapping
    /// notifications converge on the true value.
    /// </summary>
    private async Task HandleMediaAttachedAsync(MediaAttachedMessage message)
    {
        try
        {
            var media = await _mediaService.GetForEventAsync(message.EventId);

            var dto = FindLoadedEvent(message.EventId);
            if (dto != null)
            {
                dto.MediaCount = media.Count;
            }

            if (SelectedEvent?.EventId == message.EventId)
            {
                ApplySelectedEventMedia(media);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing media for event {EventId} after attach", message.EventId);
        }
    }

    /// <summary>
    /// Handles an <see cref="EventCreatedMessage"/> from any thread by marshalling
    /// the viewport reload onto the UI thread.
    /// </summary>
    private void OnEventCreated(EventCreatedMessage message)
    {
        void Handle() => _ = HandleEventCreatedAsync(message);

        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            Handle();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Handle);
        }
    }

    /// <summary>
    /// Reloads the viewport after an event was created elsewhere in the app,
    /// navigating to the event's date when it is outside the current viewport.
    /// </summary>
    private async Task HandleEventCreatedAsync(EventCreatedMessage message)
    {
        // Our own CRUD paths (CreateEventAsync etc.) set IsLoading and already
        // reload/navigate themselves; skip to avoid overlapping service calls.
        if (IsLoading)
            return;

        try
        {
            TotalEventCount = await _eventService.GetTotalEventCountAsync();

            if (Viewport == null)
                return;

            if (!Viewport.IsDateVisible(message.StartDate))
            {
                // The new event is outside the current viewport - navigate to it.
                await CreateViewportAsync(CurrentZoomLevel, message.StartDate);
            }
            else
            {
                await LoadEventsForViewportAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing timeline after event created: {EventId}", message.EventId);
        }
    }

    /// <summary>
    /// Handles an <see cref="EventUpdatedMessage"/> from any thread by marshalling
    /// the viewport reload onto the UI thread.
    /// </summary>
    private void OnEventUpdated(EventUpdatedMessage message)
    {
        void Handle() => _ = HandleEventUpdatedAsync(message);

        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            Handle();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Handle);
        }
    }

    /// <summary>
    /// Reloads the viewport after an event was edited elsewhere in the app
    /// (e.g. the Search page) so the timeline reflects the change without
    /// renavigation. Our own UpdateEventAsync publishes while IsLoading is set
    /// and reloads itself, so it is skipped here.
    /// </summary>
    private async Task HandleEventUpdatedAsync(EventUpdatedMessage message)
    {
        if (IsLoading || Viewport == null)
            return;

        try
        {
            await LoadEventsForViewportAsync();

            if (SelectedEvent?.EventId == message.EventId)
            {
                // Re-point the details panel at the reloaded DTO (null when the
                // edit moved the event outside the current viewport).
                SelectedEvent = FindLoadedEvent(message.EventId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing timeline after event updated: {EventId}", message.EventId);
        }
    }

    /// <summary>
    /// Handles an <see cref="EventDeletedMessage"/> from any thread by marshalling
    /// the removal onto the UI thread.
    /// </summary>
    private void OnEventDeleted(EventDeletedMessage message)
    {
        void Handle() => _ = HandleEventDeletedAsync(message);

        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            Handle();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Handle);
        }
    }

    /// <summary>
    /// Removes an event deleted elsewhere in the app (e.g. the Search page)
    /// from the timeline. Our own DeleteEventAsync runs with IsLoading set and
    /// reloads itself, so it is skipped here.
    /// </summary>
    private async Task HandleEventDeletedAsync(EventDeletedMessage message)
    {
        if (IsLoading)
            return;

        try
        {
            var dto = FindLoadedEvent(message.EventId);
            if (dto != null)
            {
                _loadedEventDtos.Remove(dto);
                Events.Remove(dto);
                SpanEvents.Remove(dto);
                UncertainEvents.Remove(dto);

                // In a lane mode the removal changes lane membership:
                // recompute lanes, geometry, and the gutter (mirroring the
                // settled-reload path) so row counts stay accurate, a
                // now-empty lane row disappears, and _lanes stops holding
                // the removed DTO.
                if (LaneMode != LaneMode.Auto)
                {
                    RecomputeLanes();
                    ApplyLaneGeometry();
                    RebuildLaneGutter();
                }
            }

            if (SelectedEvent?.EventId == message.EventId)
            {
                SelectedEvent = null;
            }

            TotalEventCount = await _eventService.GetTotalEventCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating timeline after event deleted: {EventId}", message.EventId);
        }
    }

    /// <summary>
    /// Initializes the timeline with default viewport.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Loading timeline...";

            await ApplyDefaultZoomLevelAsync();

            TotalEventCount = await _eventService.GetTotalEventCountAsync();

            // Create initial viewport
            await CreateViewportAsync(CurrentZoomLevel, DateTime.Now);

            StatusText = $"Loaded {VisibleEventCount} events";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing timeline");
            StatusText = "Error loading timeline";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Applies the Settings page's "Default Zoom Level" (default_zoom_level,
    /// stored lowercase: year/month/week/day) the first time the timeline
    /// initializes. An unknown or unreadable value keeps the built-in Month.
    /// </summary>
    private async Task ApplyDefaultZoomLevelAsync()
    {
        if (_defaultZoomApplied)
        {
            return;
        }

        _defaultZoomApplied = true;

        try
        {
            var stored = await _settingsService.GetDefaultZoomLevelAsync();
            if (Enum.TryParse<ZoomLevel>(stored?.Trim(), ignoreCase: true, out var zoomLevel) &&
                Enum.IsDefined(typeof(ZoomLevel), zoomLevel))
            {
                CurrentZoomLevel = zoomLevel;
            }
            else
            {
                _logger.LogWarning("Unrecognized default zoom level '{Value}'; using {Fallback}", stored, CurrentZoomLevel);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the default zoom level; using {Fallback}", CurrentZoomLevel);
        }
    }

    /// <summary>
    /// Initializes the timeline with specified viewport dimensions.
    /// </summary>
    public async Task InitializeAsync(int width, int height)
    {
        ViewportWidth = width;
        ViewportHeight = height;
        await InitializeAsync();
    }

    /// <summary>
    /// Creates a new viewport and loads events.
    /// </summary>
    private async Task CreateViewportAsync(ZoomLevel zoomLevel, DateTime centerDate)
    {
        if (ViewportWidth <= 0 || ViewportHeight <= 0)
            return;

        Viewport = await _timelineService.CreateViewportAsync(
            zoomLevel,
            centerDate,
            ViewportWidth,
            ViewportHeight);

        await LoadEventsForViewportAsync();
        await LoadErasForViewportAsync();
        GenerateTimeRulerTicks();
    }

    /// <summary>
    /// Loads events for the current viewport (the settled-reload path). All
    /// fetched DTOs - including the ±30-day buffer - are kept and rendered
    /// with a Visibility binding, so intra-buffer pans reveal them instantly
    /// without waiting for the next reload.
    /// </summary>
    private async Task LoadEventsForViewportAsync()
    {
        if (Viewport == null) return;

        // Last-request-wins: if another load starts while this query is in
        // flight, this one's stale results are dropped on completion.
        var sequence = ++_eventLoadSequence;

        try
        {
            var events = (await _timelineService.GetEventsForViewportAsync(Viewport)).ToList();
            if (sequence != _eventLoadSequence)
                return;

            _loadedEventDtos = events;

            var viewport = Viewport;
            if (viewport != null)
            {
                // The viewport may have moved while the query ran (the user
                // kept panning); re-anchor the DTOs to the live viewport.
                _timelineService.CalculateEventPositions(_loadedEventDtos, viewport);
            }

            // Lane membership recomputes on this settled path only - never per
            // scroll tick (ticks just re-apply the cached assignment).
            RecomputeLanes();
            ApplyLaneGeometry();
            RebuildEventCollections();
            RebuildLaneGutter();

            _logger.LogDebug(
                "Loaded {Count} events for the viewport ({Visible} visible)",
                _loadedEventDtos.Count,
                VisibleEventCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading events for viewport");
            ErrorMessage = $"Could not load events: {ex.Message}";
        }
    }

    /// <summary>Finds a loaded event DTO (pin or span) by id.</summary>
    private TimelineEventDto? FindLoadedEvent(string eventId)
        => _loadedEventDtos.FirstOrDefault(e => e.EventId == eventId);

    /// <summary>
    /// Rebuilds the pin/span/uncertainty layer collections from the loaded
    /// DTOs. Only called on the settled reload path (or when a zoom shift
    /// actually flipped an event between pin and span) - never on plain pans.
    /// </summary>
    private void RebuildEventCollections()
    {
        Events.Clear();
        SpanEvents.Clear();
        UncertainEvents.Clear();

        foreach (var evt in _loadedEventDtos)
        {
            if (evt.RenderMode == EventRenderMode.Span)
            {
                SpanEvents.Add(evt);
            }
            else
            {
                Events.Add(evt);
            }

            if (evt.IsApproximate)
            {
                UncertainEvents.Add(evt);
            }
        }
    }

    /// <summary>
    /// Recomputes lane membership for the current mode from the loaded DTOs.
    /// Runs on the settled reload path and on mode changes only.
    /// </summary>
    private void RecomputeLanes()
    {
        _lanes = LaneAssignment.ComputeLanes(_loadedEventDtos, LaneMode).ToList();
    }

    /// <summary>
    /// Applies lane geometry (rows from the top of the events area) over the
    /// stacking positions the pure calc produced. In Auto mode this is a no-op
    /// apart from clearing any leftover collapse flags, so Auto renders
    /// exactly as the classic stacked layout.
    /// </summary>
    private void ApplyLaneGeometry()
    {
        if (LaneMode == LaneMode.Auto)
        {
            foreach (var evt in _loadedEventDtos)
            {
                evt.IsLaneCollapsed = false;
            }

            return;
        }

        double top = 0;
        foreach (var lane in _lanes)
        {
            var collapsed = _collapsedLaneKeys.Contains(lane.LaneKey);
            var rowHeight = collapsed
                ? LaneAssignment.CollapsedLaneRowHeight
                : LaneAssignment.LaneRowHeight;

            foreach (var evt in lane.Events)
            {
                evt.IsLaneCollapsed = collapsed;
                evt.PixelY = top + Math.Max(0, (LaneAssignment.LaneRowHeight - evt.Height) / 2.0);
            }

            top += rowHeight;
        }
    }

    /// <summary>
    /// Rebuilds the gutter row DTOs (labels, counts, collapse chevrons) from
    /// the cached lanes. Row heights match the lane geometry constants so the
    /// gutter rows stay aligned with the lane rows.
    /// </summary>
    private void RebuildLaneGutter()
    {
        LaneGutterRows.Clear();
        if (LaneMode == LaneMode.Auto)
            return;

        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var collapsed = _collapsedLaneKeys.Contains(lane.LaneKey);
            LaneGutterRows.Add(new LaneGutterRowDto
            {
                LaneKey = lane.LaneKey,
                Label = lane.Label,
                EventCount = lane.Events.Count,
                RowHeight = collapsed
                    ? LaneAssignment.CollapsedLaneRowHeight
                    : LaneAssignment.LaneRowHeight,
                IsCollapsed = collapsed,
                IsAlternate = i % 2 == 1
            });
        }
    }

    /// <summary>
    /// Collapses or expands one lane: its events hide and the row shrinks,
    /// rows below shift up. Pure re-layout of cached data - no reload, and
    /// nothing is persisted.
    /// </summary>
    public void ToggleLaneCollapse(string laneKey)
    {
        if (string.IsNullOrEmpty(laneKey))
            return;

        if (!_collapsedLaneKeys.Remove(laneKey))
        {
            _collapsedLaneKeys.Add(laneKey);
        }

        ApplyLaneGeometry();
        RebuildLaneGutter();
    }

    /// <summary>
    /// Loads eras for the current viewport.
    /// </summary>
    private async Task LoadErasForViewportAsync()
    {
        if (Viewport == null) return;

        // Last-request-wins: drop stale results if a newer load started.
        var sequence = ++_eraLoadSequence;

        try
        {
            var eras = (await _timelineService.GetErasForViewportAsync(Viewport)).ToList();
            if (sequence != _eraLoadSequence)
                return;

            _loadedEras = eras;

            var viewport = Viewport;
            if (viewport != null)
            {
                // Re-anchor to the live viewport (it may have moved mid-query).
                _timelineService.CalculateEraPositions(_loadedEras, viewport);
            }

            Eras.Clear();
            foreach (var era in _loadedEras.Where(e => e.IsVisible))
            {
                Eras.Add(era);
            }

            // Generate era bars and update filters
            GenerateEraBars(_loadedEras);

            _logger.LogDebug("Loaded {Count} visible eras", Eras.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading eras for viewport");
        }
    }

    /// <summary>
    /// Generates era bar data for display as thin horizontal lines.
    /// Stacks overlapping eras on different tracks.
    /// </summary>
    private void GenerateEraBars(IEnumerable<TimelineEraDto> eras)
    {
        EraBars.Clear();

        // Track assignment for non-overlapping display
        var tracks = new List<List<EraBarDto>>();
        const double barHeight = 4.0;
        const double trackSpacing = 8.0;

        foreach (var era in eras.OrderBy(e => e.StartDate))
        {
            var eraBar = EraBarDto.FromEraDto(era);

            // Find available track (non-overlapping)
            int trackIndex = FindAvailableEraTrack(tracks, eraBar);
            eraBar.TrackIndex = trackIndex;
            eraBar.TrackY = trackIndex * trackSpacing;

            EraBars.Add(eraBar);

            // Update filters if this is a new era
            if (!EraFilters.Any(f => f.EraId == era.EraId))
            {
                var filter = new EraFilterDto
                {
                    EraId = era.EraId,
                    Name = era.Name,
                    ColorCode = era.ColorCode,
                    IsVisible = true
                };

                // Re-evaluate VisibleEraBars when the user toggles this era's
                // checkbox (TwoWay-bound to IsVisible in TimelineControl.xaml);
                // without this the bar layer only refreshed on full reloads.
                filter.PropertyChanged += OnEraFilterPropertyChanged;

                EraFilters.Add(filter);
            }
        }

        _eraBarTrackCount = tracks.Count > 0 ? tracks.Count : 1;
        OnPropertyChanged(nameof(EraBarsHeight));
        OnPropertyChanged(nameof(VisibleEraBars));
    }

    /// <summary>
    /// Handles a single era filter checkbox toggle: re-evaluates the computed
    /// VisibleEraBars so the era's band is added/removed immediately without
    /// requiring a pan/zoom/reload. Marshals to the UI thread if needed.
    /// </summary>
    private void OnEraFilterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EraFilterDto.IsVisible)) return;

        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => OnPropertyChanged(nameof(VisibleEraBars)));
        }
        else
        {
            OnPropertyChanged(nameof(VisibleEraBars));
        }
    }

    /// <summary>
    /// Finds an available track for an era bar that doesn't overlap with existing bars.
    /// </summary>
    private int FindAvailableEraTrack(List<List<EraBarDto>> tracks, EraBarDto newBar)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            bool hasOverlap = track.Any(existing =>
            {
                // Check if date ranges overlap
                var existingEnd = existing.PixelX + existing.Width;
                var newEnd = newBar.PixelX + newBar.Width;
                return !(newBar.PixelX >= existingEnd || newEnd <= existing.PixelX);
            });

            if (!hasOverlap)
            {
                track.Add(newBar);
                return i;
            }
        }

        // Create new track
        tracks.Add(new List<EraBarDto> { newBar });
        return tracks.Count - 1;
    }

    /// <summary>
    /// Shows all eras.
    /// </summary>
    public void ShowAllEras()
    {
        foreach (var filter in EraFilters)
        {
            filter.IsVisible = true;
        }
        OnPropertyChanged(nameof(VisibleEraBars));
    }

    /// <summary>
    /// Hides all eras.
    /// </summary>
    public void HideAllEras()
    {
        foreach (var filter in EraFilters)
        {
            filter.IsVisible = false;
        }
        OnPropertyChanged(nameof(VisibleEraBars));
    }

    /// <summary>
    /// Generates time ruler ticks based on current viewport.
    /// Uses Adobe Premiere-style adaptive tick density algorithm.
    /// </summary>
    private void GenerateTimeRulerTicks()
    {
        if (Viewport == null) return;

        try
        {
            TimeRulerTicks.Clear();

            // Create coordinate converter from viewport
            var converter = TimelineCoordinateConverter.FromViewport(Viewport);

            // Get optimal ruler configuration based on zoom level
            var rulerConfig = TimeRulerConfig.Calculate(Viewport.PixelsPerDay);

            // Generate ticks
            var ticks = rulerConfig.GenerateTicks(converter);

            foreach (var tick in ticks)
            {
                TimeRulerTicks.Add(new TimeRulerTickDto
                {
                    Date = tick.Date,
                    PixelX = tick.ScreenX,
                    IsMajor = tick.IsMajor,
                    Label = tick.Label
                });
            }

            _logger.LogDebug("Generated {Count} time ruler ticks", TimeRulerTicks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating time ruler ticks");
        }
    }

    /// <summary>
    /// Zooms in the timeline.
    /// </summary>
    [RelayCommand]
    public async Task ZoomInAsync()
    {
        if (Viewport == null || IsLoading) return;

        try
        {
            IsLoading = true;
            StatusText = "Zooming in...";

            var centerDate = Viewport.StartDate.AddDays(Viewport.VisibleDays / 2.0);
            Viewport = await _timelineService.ZoomInAsync(Viewport, centerDate);
            CurrentZoomLevel = Viewport.ZoomLevel;

            await LoadEventsForViewportAsync();
            await LoadErasForViewportAsync();

            StatusText = $"Zoom: {TimelineScale.GetZoomLevelName(CurrentZoomLevel)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error zooming in");
            StatusText = "Error zooming in";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Zooms out the timeline.
    /// </summary>
    [RelayCommand]
    public async Task ZoomOutAsync()
    {
        if (Viewport == null || IsLoading) return;

        try
        {
            IsLoading = true;
            StatusText = "Zooming out...";

            var centerDate = Viewport.StartDate.AddDays(Viewport.VisibleDays / 2.0);
            Viewport = await _timelineService.ZoomOutAsync(Viewport, centerDate);
            CurrentZoomLevel = Viewport.ZoomLevel;

            await LoadEventsForViewportAsync();
            await LoadErasForViewportAsync();

            StatusText = $"Zoom: {TimelineScale.GetZoomLevelName(CurrentZoomLevel)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error zooming out");
            StatusText = "Error zooming out";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Performs cursor-anchored zoom following Adobe Premiere's algorithm.
    /// The timecode under the cursor stays visually fixed while the timeline expands/contracts.
    /// </summary>
    /// <param name="cursorScreenX">Cursor X position in pixels relative to viewport</param>
    /// <param name="wheelDelta">Raw mouse wheel delta (typically ±120 per tick)</param>
    public Task CursorAnchoredZoomAsync(double cursorScreenX, double wheelDelta)
    {
        if (Viewport == null) return Task.CompletedTask;

        try
        {
            // Calculate new viewport state using Premiere-style zoom
            var (newStartDate, newPixelsPerDay) = ZoomHelper.CalculateCursorAnchoredZoom(
                Viewport,
                cursorScreenX,
                wheelDelta,
                minPixelsPerDay: 0.01,  // ~100 years visible
                maxPixelsPerDay: 50.0    // ~20 days visible at most
            );

            // Calculate new end date
            var newVisibleDays = Viewport.ViewportWidth / newPixelsPerDay;
            var newEndDate = newStartDate.AddDays(newVisibleDays);
            var newCenterDate = newStartDate.AddDays(newVisibleDays / 2);

            // Update viewport
            Viewport.StartDate = newStartDate;
            Viewport.EndDate = newEndDate;
            Viewport.CenterDate = newCenterDate;
            Viewport.PixelsPerDay = newPixelsPerDay;
            Viewport.ZoomLevel = ZoomHelper.GetClosestZoomLevel(newPixelsPerDay);

            // Update current zoom level display
            CurrentZoomLevel = Viewport.ZoomLevel;

            // Immediate visual scale change on the loaded DTOs; the DB reload
            // is debounced until the wheel stops (~150ms after the last tick).
            ApplyViewportShift();
            ScheduleViewportReload();

            // The viewport instance was mutated in place - re-raise the
            // notification so the proportional scrollbar tracks the zoom.
            OnPropertyChanged(nameof(Viewport));

            StatusText = $"Zoom: {newPixelsPerDay:F2} px/day";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing cursor-anchored zoom");
            ErrorMessage = $"Could not zoom the timeline: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs center-anchored zoom (wheel zoom without cursor position).
    /// </summary>
    /// <param name="wheelDelta">Raw mouse wheel delta (typically ±120 per tick)</param>
    public async Task CenterAnchoredZoomAsync(double wheelDelta)
    {
        if (Viewport == null) return;
        await CursorAnchoredZoomAsync(Viewport.ViewportWidth / 2.0, wheelDelta);
    }

    /// <summary>
    /// Sets a specific zoom level.
    /// </summary>
    [RelayCommand]
    private async Task SetZoomLevelAsync(object? parameter)
    {
        if (Viewport == null || IsLoading || parameter == null) return;

        // Parse parameter (can be string or ZoomLevel)
        ZoomLevel zoomLevel;
        if (parameter is ZoomLevel zl)
        {
            zoomLevel = zl;
        }
        else if (parameter is string str && Enum.TryParse<ZoomLevel>(str, out var parsed))
        {
            zoomLevel = parsed;
        }
        else
        {
            _logger.LogWarning("Invalid zoom level parameter: {Parameter}", parameter);
            return;
        }

        try
        {
            IsLoading = true;
            StatusText = $"Setting zoom to {TimelineScale.GetZoomLevelName(zoomLevel)}...";

            var centerDate = Viewport.StartDate.AddDays(Viewport.VisibleDays / 2.0);
            await CreateViewportAsync(zoomLevel, centerDate);
            CurrentZoomLevel = zoomLevel;

            StatusText = $"Zoom: {TimelineScale.GetZoomLevelName(CurrentZoomLevel)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting zoom level");
            StatusText = "Error setting zoom level";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Pans the timeline by pixel offset. The shift itself is cheap and
    /// synchronous - viewport dates move and the ALREADY-LOADED DTOs are
    /// repositioned via the pure position calc (no DB) - while the database
    /// reload is debounced to fire once, ~150ms after the LAST pan/zoom tick.
    /// There is deliberately no IsLoading bail-out here: the old guard dropped
    /// ticks mid-load and lost the final position; the coalescer supersedes
    /// pending reloads instead, so the settled position always reloads.
    /// </summary>
    public async Task PanAsync(double pixelOffset)
    {
        if (Viewport == null) return;

        try
        {
            // Pure viewport math (Task.FromResult inside) - no DB work.
            Viewport = await _timelineService.PanAsync(Viewport, pixelOffset);
            ApplyViewportShift();
            ScheduleViewportReload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error panning timeline");
            ErrorMessage = $"Could not pan the timeline: {ex.Message}";
        }
    }

    /// <summary>
    /// Cheap synchronous re-layout after a viewport change: repositions the
    /// already-loaded event and era DTOs with the pure calcs and regenerates
    /// the ruler ticks. Never touches the database.
    /// </summary>
    private void ApplyViewportShift()
    {
        var viewport = Viewport;
        if (viewport == null) return;

        if (_loadedEventDtos.Count > 0)
        {
            _timelineService.CalculateEventPositions(_loadedEventDtos, viewport);
            ApplyLaneGeometry();

            // A zoom shift can flip events between pin and span; repartition
            // the render layers only when that actually happened (plain pans
            // never change widths, so this stays a cheap scan).
            var needsRepartition =
                SpanEvents.Any(e => e.RenderMode != EventRenderMode.Span) ||
                Events.Any(e => e.RenderMode != EventRenderMode.Pin);
            if (needsRepartition)
            {
                RebuildEventCollections();
            }
        }

        if (_loadedEras.Count > 0)
        {
            _timelineService.CalculateEraPositions(_loadedEras, viewport);
            GenerateEraBars(_loadedEras);
        }

        GenerateTimeRulerTicks();
    }

    /// <summary>
    /// Schedules the trailing debounced reload for the current gesture burst.
    /// Each tick supersedes the previous ticket; only the LAST tick's task
    /// survives the quiet period and performs the single coalesced reload.
    /// </summary>
    private void ScheduleViewportReload()
    {
        var ticket = _reloadCoalescer.RecordTick();
        _ = ReloadWhenSettledAsync(ticket);
    }

    /// <summary>
    /// Waits out the quiet period and, if no newer tick superseded this one,
    /// reloads events/eras/ruler for the settled viewport. Reads the LIVE
    /// viewport at fire time, so the final position always wins.
    /// </summary>
    private async Task ReloadWhenSettledAsync(long ticket)
    {
        try
        {
            await Task.Delay(_reloadCoalescer.QuietPeriod);
            if (!_reloadCoalescer.IsCurrent(ticket) || Viewport == null)
                return;

            await LoadEventsForViewportAsync();
            await LoadErasForViewportAsync();
            GenerateTimeRulerTicks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during debounced viewport reload");
            ErrorMessage = $"Could not refresh the timeline: {ex.Message}";
        }
    }

    /// <summary>
    /// Called by the control after it mutated the viewport instance directly
    /// (proportional-scrollbar edge drags): applies the immediate visual
    /// scale/position change, schedules the debounced reload, and re-raises
    /// the Viewport notification so dependents stay in sync.
    /// </summary>
    public void NotifyViewportChangedExternally()
    {
        var viewport = Viewport;
        if (viewport == null) return;

        CurrentZoomLevel = viewport.ZoomLevel;
        ApplyViewportShift();
        ScheduleViewportReload();
        OnPropertyChanged(nameof(Viewport));
    }

    /// <summary>
    /// Navigates to today's date.
    /// </summary>
    [RelayCommand]
    private async Task GoToTodayAsync()
    {
        if (IsLoading) return;

        try
        {
            IsLoading = true;
            StatusText = "Navigating to today...";

            await CreateViewportAsync(CurrentZoomLevel, DateTime.Now);
            StatusText = "Showing today";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to today");
            StatusText = "Error navigating";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Navigates to a specific date.
    /// </summary>
    [RelayCommand]
    private async Task GoToDateAsync(DateTime date)
    {
        if (IsLoading) return;

        try
        {
            IsLoading = true;
            StatusText = $"Navigating to {date:d}...";

            await CreateViewportAsync(CurrentZoomLevel, date);
            StatusText = $"Showing {date:d}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to date");
            StatusText = "Error navigating";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Sets the viewport to show a 3-year span with the specified year in the center third.
    /// Year-1 in left third, specified year in middle third, year+1 in right third.
    /// </summary>
    public async Task SetViewportToYearSpanAsync(int year)
    {
        if (IsLoading || ViewportWidth <= 0) return;

        try
        {
            IsLoading = true;
            StatusText = $"Navigating to {year}...";

            // Calculate the 3-year date range
            var startDate = new DateTime(year - 1, 1, 1);
            var endDate = new DateTime(year + 2, 1, 1); // Jan 1 of year+2 to include all of year+1
            var centerDate = new DateTime(year, 7, 1); // Middle of the target year

            // Calculate pixels per day to fit exactly 3 years in the viewport
            var totalDays = (endDate - startDate).TotalDays;
            var pixelsPerDay = ViewportWidth / totalDays;

            // Create a custom viewport for this specific date range
            Viewport = new TimelineViewport
            {
                StartDate = startDate,
                EndDate = endDate,
                CenterDate = centerDate,
                PixelsPerDay = pixelsPerDay,
                ZoomLevel = ZoomHelper.GetClosestZoomLevel(pixelsPerDay),
                ViewportWidth = ViewportWidth,
                ViewportHeight = ViewportHeight,
                ScrollPosition = 0
            };

            CurrentZoomLevel = Viewport.ZoomLevel;

            // Reload events and eras for the new viewport
            await LoadEventsForViewportAsync();
            await LoadErasForViewportAsync();
            GenerateTimeRulerTicks();

            StatusText = $"Showing {year - 1} - {year} - {year + 1}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting year span viewport");
            StatusText = "Error navigating";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Refreshes the timeline data.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        try
        {
            IsLoading = true;
            StatusText = "Refreshing...";
            ErrorMessage = null;

            TotalEventCount = await _eventService.GetTotalEventCountAsync();

            if (Viewport == null && ViewportWidth > 0 && ViewportHeight > 0)
            {
                // Create viewport if it doesn't exist but we have valid dimensions
                await CreateViewportAsync(CurrentZoomLevel, DateTime.Now);
            }
            else if (Viewport != null)
            {
                await LoadEventsForViewportAsync();
                await LoadErasForViewportAsync();
                GenerateTimeRulerTicks();
            }

            StatusText = $"Refreshed - {VisibleEventCount} events shown";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing timeline");
            StatusText = "Error refreshing";
            ErrorMessage = $"Could not refresh the timeline: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Selects an event. The viewport query does not load person links, so the
    /// selected event's people are fetched lazily for the details panel.
    /// </summary>
    [RelayCommand]
    private void SelectEvent(TimelineEventDto? eventDto)
    {
        SelectedEvent = eventDto;
        _logger.LogDebug("Selected event: {EventId}", eventDto?.EventId ?? "none");

        if (eventDto != null && !string.IsNullOrEmpty(eventDto.EventId))
        {
            SelectedEventMedia.Clear();
            SelectedEventMediaHeader = "Photos";
            _ = LoadPeopleForEventAsync(eventDto);
            _ = LoadMediaForSelectedEventAsync(eventDto);

            // Opening an event in the details panel is what the Jump List's
            // "Recent Events" group tracks (JumpListService subscribes).
            WeakReferenceMessenger.Default.Send(new EventOpenedMessage(eventDto.EventId));
        }
    }

    /// <summary>
    /// "View on timeline" / deep-link entry point (TimelinePage navigation
    /// parameter): moves the viewport to the event's start date and announces
    /// the event as opened so the Jump List tracks it. A missing event or a
    /// failed load is reported in the status line, never thrown.
    /// </summary>
    public async Task NavigateToEventAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        try
        {
            var targetEvent = await _eventService.GetEventByIdAsync(eventId);
            if (targetEvent == null)
            {
                StatusText = "That event no longer exists";
                _logger.LogWarning("Navigation requested for unknown event {EventId}", eventId);
                return;
            }

            if (GoToDateCommand.CanExecute(targetEvent.StartDate))
            {
                await GoToDateCommand.ExecuteAsync(targetEvent.StartDate);
            }

            WeakReferenceMessenger.Default.Send(new EventOpenedMessage(targetEvent.EventId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to event {EventId}", eventId);
            StatusText = "Could not navigate to the event";
        }
    }

    /// <summary>
    /// Loads the linked person names for an event DTO (best-effort) and applies
    /// them on the UI thread so the people row in the details panel updates.
    /// </summary>
    private async Task LoadPeopleForEventAsync(TimelineEventDto eventDto)
    {
        try
        {
            var people = await _personService.GetPersonsForEventAsync(eventDto.EventId);
            var names = people
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            void Apply() => eventDto.PeopleNames = names;

            if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                _dispatcherQueue.TryEnqueue(Apply);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load people for event: {EventId}", eventDto.EventId);
        }
    }

    #region Media Attachments (details-panel strip, lightbox, dialog staging)

    /// <summary>
    /// Loads the media attachments for the selected event (best-effort) and
    /// applies them on the UI thread so the details-panel strip updates.
    /// </summary>
    private async Task LoadMediaForSelectedEventAsync(TimelineEventDto eventDto)
    {
        try
        {
            var media = await _mediaService.GetForEventAsync(eventDto.EventId);

            void Apply()
            {
                // The selection may have moved on while the query ran.
                if (SelectedEvent?.EventId != eventDto.EventId)
                    return;

                ApplySelectedEventMedia(media);
                eventDto.MediaCount = media.Count;
            }

            if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                _dispatcherQueue.TryEnqueue(Apply);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load media for event: {EventId}", eventDto.EventId);
        }
    }

    /// <summary>
    /// Replaces the details-panel strip contents. Must run on the UI thread.
    /// </summary>
    private void ApplySelectedEventMedia(List<EventMedia> media)
    {
        SelectedEventMedia.Clear();
        foreach (var item in media)
        {
            SelectedEventMedia.Add(EventMediaThumbDto.FromMedia(item, _mediaService));
        }

        SelectedEventMediaHeader = media.Count == 0 ? "Photos" : $"Photos ({media.Count})";
    }

    /// <summary>
    /// Attaches files to the selected event, reporting progress in the status
    /// bar. Failures surface in the timeline error InfoBar; successes refresh
    /// the strip via <see cref="MediaAttachedMessage"/>. Returns the attached
    /// media (empty on failure) so the view can offer the EXIF-date
    /// reconciliation (F2: photo taken outside the event's date window).
    /// </summary>
    public async Task<List<EventMedia>> AttachMediaToSelectedEventAsync(IReadOnlyList<string> paths)
    {
        var target = SelectedEvent;
        if (target == null || paths.Count == 0)
            return new List<EventMedia>();

        try
        {
            ErrorMessage = null;
            var progress = new Progress<(int done, int total)>(p =>
                StatusText = $"Attaching photos... {p.done}/{p.total}");

            var attached = await _mediaService.AttachManyAsync(target.EventId, paths, progress);

            StatusText = attached.Count == 1
                ? "1 photo attached"
                : $"{attached.Count} photos attached";
            return attached;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attaching media to event {EventId}", target.EventId);
            StatusText = "Error attaching photos";
            ErrorMessage = $"Could not attach photos: {ex.Message}";
            return new List<EventMedia>();
        }
    }

    /// <summary>
    /// Attaches files to an event and THROWS on failure - used by the
    /// Add/Edit dialog, which shows the error in its own in-dialog InfoBar.
    /// (Files that did attach before a partial failure remain attached.)
    /// </summary>
    public Task<List<EventMedia>> AttachMediaAsync(string eventId, IReadOnlyList<string> paths)
    {
        var progress = new Progress<(int done, int total)>(p =>
            StatusText = $"Attaching photos... {p.done}/{p.total}");
        return _mediaService.AttachManyAsync(eventId, paths, progress);
    }

    /// <summary>
    /// Removes a media attachment (row + managed file + thumbnail) and updates
    /// the strip and badge. Throws on failure so the lightbox can show the
    /// reason and stay open.
    /// </summary>
    public async Task RemoveMediaAsync(EventMediaThumbDto media)
    {
        try
        {
            await _mediaService.RemoveAsync(media.MediaId, deleteFile: true);

            SelectedEventMedia.Remove(media);
            SelectedEventMediaHeader = SelectedEventMedia.Count == 0
                ? "Photos"
                : $"Photos ({SelectedEventMedia.Count})";

            var dto = FindLoadedEvent(media.EventId);
            if (dto != null && dto.MediaCount > 0)
            {
                dto.MediaCount--;
            }

            StatusText = "Photo removed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing media {MediaId}", media.MediaId);
            throw;
        }
    }

    /// <summary>
    /// Updates a media attachment's caption. Throws on failure so the lightbox
    /// can show the reason.
    /// </summary>
    public async Task UpdateMediaCaptionAsync(EventMediaThumbDto media, string? caption)
    {
        try
        {
            await _mediaService.UpdateCaptionAsync(media.MediaId, caption);
            media.Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating caption for media {MediaId}", media.MediaId);
            throw;
        }
    }

    // Photos picked/dropped in the Add Event dialog BEFORE the event exists;
    // attached right after the save creates the event id.
    private readonly List<string> _dialogPendingPhotoPaths = new();

    /// <summary>Pending-photos label shown in the event dialog's photo row.</summary>
    [ObservableProperty]
    private string _dialogPendingPhotosLabel = "";

    /// <summary>
    /// Stages photo paths for a NEW event (attached after save). Duplicate
    /// paths are ignored.
    /// </summary>
    public void AddDialogPendingPhotos(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !_dialogPendingPhotoPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _dialogPendingPhotoPaths.Add(path);
            }
        }

        UpdateDialogPendingPhotosLabel();
    }

    /// <summary>Clears the staged photos (dialog reset).</summary>
    public void ClearDialogPendingPhotos()
    {
        _dialogPendingPhotoPaths.Clear();
        DialogPendingPhotosLabel = "";
    }

    private void UpdateDialogPendingPhotosLabel()
    {
        DialogPendingPhotosLabel = _dialogPendingPhotoPaths.Count == 0
            ? ""
            : _dialogPendingPhotoPaths.Count == 1
                ? "1 photo will be attached on save"
                : $"{_dialogPendingPhotoPaths.Count} photos will be attached on save";
    }

    /// <summary>
    /// Attaches the staged dialog photos to the just-created event. The event
    /// itself already saved, so attachment failures are surfaced in the
    /// timeline InfoBar without failing the save.
    /// </summary>
    private async Task AttachPendingDialogPhotosAsync(string eventId)
    {
        if (_dialogPendingPhotoPaths.Count == 0)
            return;

        var paths = _dialogPendingPhotoPaths.ToList();
        ClearDialogPendingPhotos();

        try
        {
            StatusText = "Attaching photos...";
            var progress = new Progress<(int done, int total)>(p =>
                StatusText = $"Attaching photos... {p.done}/{p.total}");
            await _mediaService.AttachManyAsync(eventId, paths, progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attaching staged photos to event {EventId}", eventId);
            ErrorMessage = $"The event was saved, but some photos could not be attached: {ex.Message}";
        }
    }

    /// <summary>
    /// F2 EXIF-date reconciliation, decision half (the ContentDialog itself
    /// lives in the view — Core and the ViewModel stay dialog-free here).
    /// Given the media just attached to an event, returns the earliest EXIF
    /// CapturedAt when that date falls OUTSIDE the event's precision window
    /// (<see cref="DatePrecisionExtensions.GetWindow"/>), meaning the view
    /// should offer to update the event date — or null when no offer is
    /// warranted (no CapturedAt anywhere, date inside the window, event gone,
    /// or the check itself failed). Media without CapturedAt never trigger an
    /// offer. A batch yields at most ONE offer, keyed on the earliest capture.
    /// </summary>
    public async Task<DateTime?> GetExifDateOfferAsync(string eventId, IReadOnlyList<EventMedia> newlyAttached)
    {
        try
        {
            var capturedDates = newlyAttached
                .Where(m => m.CapturedAt.HasValue)
                .Select(m => m.CapturedAt!.Value)
                .ToList();
            if (capturedDates.Count == 0)
                return null;

            var earliest = capturedDates.Min();

            var existingEvent = await _eventService.GetEventByIdAsync(eventId);
            if (existingEvent == null)
                return null;

            // Compare at day granularity against the window the event's
            // precision implies (a "Summer 1998" event covers the whole
            // season - a photo from July 1998 needs no offer). For Unknown
            // precision the window is degenerate (the placeholder anchor), so
            // a dated photo practically always triggers the offer - which is
            // exactly right for an event with no usable date.
            var (windowStart, windowEnd) = DatePrecisionExtensions.GetWindow(
                existingEvent.StartDate, existingEvent.DatePrecision);

            return earliest.Date < windowStart.Date || earliest.Date > windowEnd.Date
                ? earliest
                : null;
        }
        catch (Exception ex)
        {
            // The attach itself already succeeded; a failed offer check must
            // not turn it into an error. Skipping the offer is safe.
            _logger.LogWarning(ex, "Could not evaluate EXIF date offer for event {EventId}", eventId);
            return null;
        }
    }

    /// <summary>
    /// Applies an accepted EXIF-date offer: moves the event to the photo's
    /// capture day (DatePrecision becomes Day - the EXIF timestamp pins the
    /// calendar day) and persists through the same service + messenger path
    /// as the Edit dialog, so every subscribed view refreshes via
    /// <see cref="EventUpdatedMessage"/>. Failures surface in the timeline
    /// error InfoBar.
    /// </summary>
    public async Task ApplyExifEventDateAsync(string eventId, DateTime capturedAt)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        if (IsLoading)
        {
            ErrorMessage = "The timeline is busy with another operation. Please try again.";
            return;
        }

        try
        {
            IsLoading = true;
            StatusText = "Updating event date...";
            ErrorMessage = null;

            var existingEvent = await _eventService.GetEventByIdAsync(eventId);
            if (existingEvent == null)
            {
                StatusText = "Event not found";
                ErrorMessage = "The event could not be found. It may have been deleted.";
                return;
            }

            var newDate = capturedAt.Date;
            existingEvent.StartDate = newDate;
            existingEvent.DatePrecision = DatePrecision.Day;

            await _eventService.UpdateEventAsync(existingEvent);

            // Notify other views; a subscriber failure must not fail the update.
            try
            {
                WeakReferenceMessenger.Default.Send(new EventUpdatedMessage(eventId, newDate));
            }
            catch (Exception messengerEx)
            {
                _logger.LogWarning(messengerEx, "Error publishing EventUpdatedMessage for event {EventId}", eventId);
            }

            // Refresh the timeline; re-point the selection at the reloaded DTO
            // (null when the new date moved the event out of the viewport).
            await LoadEventsForViewportAsync();
            if (SelectedEvent?.EventId == eventId)
            {
                SelectedEvent = Events.FirstOrDefault(e => e.EventId == eventId);
            }

            StatusText = $"Event date updated to {newDate.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}";
            _logger.LogInformation(
                "Event {EventId} date updated to {Date} from photo EXIF", eventId, newDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying EXIF date to event {EventId}", eventId);
            StatusText = "Error updating event date";
            ErrorMessage = $"Could not update the event date: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    /// <summary>
    /// Updates viewport dimensions when window is resized.
    /// Creates the viewport if it doesn't exist yet.
    /// </summary>
    public async Task UpdateViewportDimensionsAsync(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        var dimensionsChanged = width != ViewportWidth || height != ViewportHeight;

        ViewportWidth = width;
        ViewportHeight = height;

        if (Viewport == null)
        {
            // Viewport doesn't exist yet - create it with current dimensions
            await CreateViewportAsync(CurrentZoomLevel, DateTime.Now);
        }
        else if (dimensionsChanged)
        {
            // Update existing viewport dimensions
            Viewport.ViewportWidth = width;
            Viewport.ViewportHeight = height;

            await LoadEventsForViewportAsync();
            await LoadErasForViewportAsync();
            GenerateTimeRulerTicks();
        }
    }

    #region Event CRUD Operations

    /// <summary>
    /// Creates a new event on the timeline from the enriched event dialog,
    /// then links the dialog's people and tag chips to the created event.
    /// Brand-new person chips are created via <see cref="IPersonService"/>
    /// first (duplicate names resolve to the existing person).
    /// </summary>
    public async Task CreateEventAsync(
        string title,
        DateTime date,
        DateTime? endDate,
        string? description,
        string? category,
        string? location,
        string? eraId,
        DatePrecision datePrecision = DatePrecision.Day)
    {
        // Don't silently return when busy: the Add Event dialog awaits this call
        // and would close as if the save succeeded. Throw so it stays open.
        if (IsLoading)
            throw new InvalidOperationException("The timeline is busy with another operation. Please try again.");

        try
        {
            IsLoading = true;
            StatusText = "Creating event...";
            ErrorMessage = null;

            var eventData = new Event
            {
                Title = title,
                StartDate = date,
                EndDate = endDate,
                DatePrecision = datePrecision,
                Description = description,
                // Categories are canonically lowercase (EventCategory constants).
                Category = category?.ToLowerInvariant() ?? EventCategory.Other,
                Location = location,
                EraId = string.IsNullOrWhiteSpace(eraId) ? null : eraId
            };

            // EventService sends EventCreatedMessage itself - do not duplicate it.
            var createdEvent = await _eventService.CreateEventAsync(eventData);

            // Reconcile people/tag links against the (empty) original sets.
            var linkedPeople = await ResolveDialogPeopleAsync();
            await ReconcileEventLinksAsync(createdEvent.EventId, linkedPeople);

            // A successful real save consumes the draft the dialog was opened from.
            await ClearCurrentDraftAsync();

            // Attach photos staged in the dialog before the event id existed.
            await AttachPendingDialogPhotosAsync(createdEvent.EventId);

            TotalEventCount = await _eventService.GetTotalEventCountAsync();

            if (Viewport != null && !Viewport.IsDateVisible(date))
            {
                // The new event is outside the current viewport - navigate to it
                // so the user can see what they just created.
                await CreateViewportAsync(CurrentZoomLevel, date);
            }
            else
            {
                // Refresh the timeline to show the new event
                await LoadEventsForViewportAsync();
            }

            ApplyPeopleNamesToLoadedEvent(createdEvent.EventId, linkedPeople.Select(p => p.Name));

            StatusText = $"Event '{title}' created";
            _logger.LogInformation("Created event: {Title} on {Date}", title, date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event: {Title}", title);
            StatusText = $"Error creating event: {ex.Message}";
            ErrorMessage = $"Could not create event '{title}': {ex.Message}";

            // Rethrow so callers (e.g. the Add Event dialog) can keep the dialog
            // open and show the error to the user instead of silently closing.
            throw;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Updates an existing event on the timeline from the enriched event
    /// dialog, reconciles its people/tag links against the sets captured when
    /// the dialog opened, and sends <see cref="EventUpdatedMessage"/> on
    /// success.
    /// </summary>
    public async Task UpdateEventAsync(
        string eventId,
        string title,
        DateTime date,
        DateTime? endDate,
        string? description,
        string? category,
        string? location,
        string? eraId,
        DatePrecision datePrecision = DatePrecision.Day)
    {
        if (string.IsNullOrEmpty(eventId)) return;

        // Don't silently return when busy: the Edit Event dialog awaits this call
        // and would close as if the save succeeded. Throw so it stays open.
        if (IsLoading)
            throw new InvalidOperationException("The timeline is busy with another operation. Please try again.");

        try
        {
            IsLoading = true;
            StatusText = "Updating event...";
            ErrorMessage = null;

            var existingEvent = await _eventService.GetEventByIdAsync(eventId);
            if (existingEvent == null)
            {
                StatusText = "Event not found";
                throw new InvalidOperationException("The event could not be found. It may have been deleted.");
            }

            existingEvent.Title = title;
            existingEvent.StartDate = date;
            existingEvent.EndDate = endDate;
            existingEvent.DatePrecision = datePrecision;
            existingEvent.Description = description;
            // Categories are canonically lowercase (EventCategory constants).
            existingEvent.Category = category?.ToLowerInvariant() ?? EventCategory.Other;
            existingEvent.Location = location;
            existingEvent.EraId = string.IsNullOrWhiteSpace(eraId) ? null : eraId;

            await _eventService.UpdateEventAsync(existingEvent);

            // Reconcile people/tag links against the sets captured on dialog open.
            var linkedPeople = await ResolveDialogPeopleAsync();
            await ReconcileEventLinksAsync(eventId, linkedPeople);

            // A successful real save consumes the draft the dialog was opened from.
            await ClearCurrentDraftAsync();

            // Notify other views; a subscriber failure must not fail the save.
            try
            {
                WeakReferenceMessenger.Default.Send(new EventUpdatedMessage(eventId, date));
            }
            catch (Exception messengerEx)
            {
                _logger.LogWarning(messengerEx, "Error publishing EventUpdatedMessage for event {EventId}", eventId);
            }

            // Refresh the timeline to show the updated event
            await LoadEventsForViewportAsync();

            ApplyPeopleNamesToLoadedEvent(eventId, linkedPeople.Select(p => p.Name));

            // Update selected event if it was the one edited
            if (SelectedEvent?.EventId == eventId)
            {
                SelectedEvent = FindLoadedEvent(eventId);
            }

            StatusText = $"Event '{title}' updated";
            _logger.LogInformation("Updated event: {EventId} - {Title}", eventId, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating event: {EventId}", eventId);
            StatusText = "Error updating event";
            ErrorMessage = $"Could not update event '{title}': {ex.Message}";

            // Rethrow so callers (e.g. the Edit Event dialog) can keep the dialog
            // open and show the error to the user instead of silently closing.
            throw;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Deletes an event from the timeline.
    /// </summary>
    public async Task DeleteEventAsync(string eventId)
    {
        if (IsLoading || string.IsNullOrEmpty(eventId)) return;

        try
        {
            IsLoading = true;
            StatusText = "Deleting event...";

            await _eventService.DeleteEventAsync(eventId);
            TotalEventCount = await _eventService.GetTotalEventCountAsync();

            // Refresh the timeline
            await LoadEventsForViewportAsync();

            // Clear selection if the deleted event was selected
            if (SelectedEvent?.EventId == eventId)
            {
                SelectedEvent = null;
            }

            StatusText = "Event deleted";
            _logger.LogInformation("Deleted event: {EventId}", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting event: {EventId}", eventId);
            StatusText = "Error deleting event";
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Event Dialog State (people/tags/era/draft)

    /// <summary>
    /// People chips shown in the event dialog. Chips with a null
    /// <see cref="EventPersonChipDto.PersonId"/> are pending new persons that
    /// are created on save.
    /// </summary>
    public ObservableCollection<EventPersonChipDto> DialogPeople { get; } = new();

    /// <summary>Tag chips shown in the event dialog.</summary>
    public ObservableCollection<EventTagChipDto> DialogTags { get; } = new();

    /// <summary>Era options for the event dialog's era ComboBox ("None" first).</summary>
    public ObservableCollection<EventEraChoiceDto> DialogEraChoices { get; } = new();

    /// <summary>
    /// Id of the draft the event dialog was opened from (kept so "Save as
    /// Draft" updates it and a successful real save deletes it), or null.
    /// </summary>
    public string? CurrentDraftId { get; set; }

    // People/tag links present when the edit dialog opened; save diffs against these.
    private readonly List<string> _originalPeopleIds = new();
    private readonly List<string> _originalTagIds = new();

    /// <summary>
    /// Resets the event-dialog state (chips, draft id, era choices) and, for an
    /// edit, loads the event's current people and tags into chips while
    /// capturing the original link sets for the save-time diff. Era and link
    /// loading are best-effort: the dialog still opens when a lookup fails.
    /// </summary>
    /// <param name="eventId">The event being edited, or null for a create.</param>
    public async Task PrepareEventDialogAsync(string? eventId)
    {
        DialogPeople.Clear();
        DialogTags.Clear();
        _originalPeopleIds.Clear();
        _originalTagIds.Clear();
        CurrentDraftId = null;
        ClearDialogPendingPhotos();

        // Load era choices ("None" + all eras by start date).
        DialogEraChoices.Clear();
        DialogEraChoices.Add(EventEraChoiceDto.None);
        try
        {
            var eras = await _eraRepository.GetOrderedByDateAsync();
            foreach (var era in eras)
            {
                DialogEraChoices.Add(new EventEraChoiceDto { EraId = era.EraId, Name = era.Name });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading eras for the event dialog");
        }

        if (string.IsNullOrEmpty(eventId))
        {
            return;
        }

        try
        {
            var people = await _personService.GetPersonsForEventAsync(eventId);
            foreach (var person in people)
            {
                DialogPeople.Add(EventPersonChipDto.FromPerson(person));
                _originalPeopleIds.Add(person.PersonId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading people for event dialog: {EventId}", eventId);
        }

        try
        {
            var tags = await _eventService.GetEventTagsAsync(eventId);
            foreach (var tag in tags)
            {
                DialogTags.Add(new EventTagChipDto { Name = tag.TagName });
                _originalTagIds.Add(tag.TagId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading tags for event dialog: {EventId}", eventId);
        }
    }

    /// <summary>
    /// Loads an event draft, records its id in <see cref="CurrentDraftId"/>,
    /// and fills the dialog chip collections from the payload. Returns the
    /// payload so the dialog can prefill its text/date fields, or null when the
    /// draft is missing, of another type, or malformed.
    /// Call after <see cref="PrepareEventDialogAsync"/> with a null event id.
    /// </summary>
    public async Task<EventDraftPayload?> LoadEventDraftAsync(string draftId)
    {
        try
        {
            var draft = await _draftService.GetDraftAsync(draftId);
            if (draft == null || draft.DraftType != DraftTypes.Event)
            {
                _logger.LogWarning("Event draft not found or wrong type: {DraftId}", draftId);
                return null;
            }

            var payload = draft.GetPayload<EventDraftPayload>();
            if (payload == null)
            {
                _logger.LogWarning("Event draft payload is malformed: {DraftId}", draftId);
                return null;
            }

            CurrentDraftId = draft.DraftId;

            foreach (var personId in payload.PersonIds.Distinct(StringComparer.Ordinal))
            {
                var person = await _personService.GetPersonAsync(personId);
                if (person != null)
                {
                    AddPersonChip(person);
                }
            }

            foreach (var name in payload.PersonNames)
            {
                AddNewPersonChip(name);
            }

            foreach (var tagName in payload.TagNames)
            {
                AddTagChip(tagName);
            }

            // Re-stage the photos that were pending when the draft was saved
            // (AddDialogPendingPhotos refreshes the "N photos will be attached
            // on save" label). Paths whose files no longer exist are dropped
            // with a visible status note instead of failing the attach later.
            if (payload.PendingPhotoPaths is { Count: > 0 })
            {
                var stillPresent = payload.PendingPhotoPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .ToList();

                if (stillPresent.Count > 0)
                {
                    AddDialogPendingPhotos(stillPresent);
                }

                var dropped = payload.PendingPhotoPaths.Count - stillPresent.Count;
                if (dropped > 0)
                {
                    StatusText = dropped == 1
                        ? "1 photo staged in this draft could no longer be found and was skipped"
                        : $"{dropped} photos staged in this draft could no longer be found and were skipped";
                }
            }

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading event draft: {DraftId}", draftId);
            return null;
        }
    }

    /// <summary>
    /// Serializes the current dialog state as an <see cref="EventDraftPayload"/>
    /// and upserts it via <see cref="IDraftService"/> (updating the draft the
    /// dialog was opened from, when there is one). Throws on failure so the
    /// dialog can stay open and show the error.
    /// </summary>
    public async Task SaveEventDraftAsync(
        string title,
        string? description,
        DateTime? startDate,
        DateTime? endDate,
        string? category,
        string? eraId,
        string? location,
        DatePrecision datePrecision = DatePrecision.Day)
    {
        try
        {
            var payload = new EventDraftPayload
            {
                Title = title.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
                StartDate = startDate,
                EndDate = endDate,
                DatePrecision = datePrecision,
                Category = category,
                EraId = string.IsNullOrWhiteSpace(eraId) ? null : eraId,
                Location = string.IsNullOrWhiteSpace(location) ? null : location,
                TagNames = DialogTags.Select(t => t.Name).ToList(),
                PersonIds = DialogPeople
                    .Where(c => !string.IsNullOrEmpty(c.PersonId))
                    .Select(c => c.PersonId!)
                    .ToList(),
                PersonNames = DialogPeople
                    .Where(c => string.IsNullOrEmpty(c.PersonId))
                    .Select(c => c.Name)
                    .ToList(),
                // Photos staged for attach-on-save must survive "Save as
                // Draft" - the label promised they would be attached.
                PendingPhotoPaths = _dialogPendingPhotoPaths.Count == 0
                    ? null
                    : _dialogPendingPhotoPaths.ToList()
            };

            var json = JsonSerializer.Serialize(payload);
            var saved = await _draftService.SaveDraftAsync(DraftTypes.Event, payload.Title, json, CurrentDraftId);
            CurrentDraftId = saved.DraftId;

            StatusText = $"Draft '{saved.Title}' saved";
            _logger.LogInformation("Event draft saved: {DraftId}", saved.DraftId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving event draft: {Title}", title);
            throw;
        }
    }

    /// <summary>
    /// Adds a chip for an existing person (no-op when already present).
    /// </summary>
    public void AddPersonChip(PersonDto person)
    {
        if (DialogPeople.Any(c => string.Equals(c.PersonId, person.PersonId, StringComparison.Ordinal)))
        {
            return;
        }

        DialogPeople.Add(EventPersonChipDto.FromPerson(person));
    }

    /// <summary>
    /// Adds a pending new-person chip for a free-text name (no-op when a chip
    /// with the same name, existing or new, is already present).
    /// </summary>
    public void AddNewPersonChip(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 ||
            DialogPeople.Any(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        DialogPeople.Add(EventPersonChipDto.ForNewPerson(trimmed));
    }

    /// <summary>Removes a person chip from the dialog.</summary>
    public void RemovePersonChip(EventPersonChipDto chip) => DialogPeople.Remove(chip);

    /// <summary>Adds a tag chip (no-op for empty or duplicate names).</summary>
    public void AddTagChip(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 ||
            DialogTags.Any(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        DialogTags.Add(new EventTagChipDto { Name = trimmed });
    }

    /// <summary>Removes a tag chip from the dialog.</summary>
    public void RemoveTagChip(EventTagChipDto chip) => DialogTags.Remove(chip);

    /// <summary>
    /// Searches persons for the dialog's people picker, excluding those already
    /// chipped. Returns an empty list on failure (suggestions are best-effort).
    /// </summary>
    public async Task<IReadOnlyList<PersonDto>> SearchDialogPeopleAsync(string searchTerm)
    {
        try
        {
            var results = await _personService.SearchPersonsAsync(searchTerm);

            var chippedIds = DialogPeople
                .Where(c => !string.IsNullOrEmpty(c.PersonId))
                .Select(c => c.PersonId!)
                .ToHashSet(StringComparer.Ordinal);
            var chippedNames = DialogPeople
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return results
                .Where(p => !chippedIds.Contains(p.PersonId) && !chippedNames.Contains(p.Name))
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error searching persons for event dialog: {SearchTerm}", searchTerm);
            return Array.Empty<PersonDto>();
        }
    }

    /// <summary>
    /// Suggests existing tag names for the dialog's tag editor, excluding those
    /// already chipped. Returns an empty list on failure.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchDialogTagsAsync(string searchTerm)
    {
        try
        {
            var tags = string.IsNullOrWhiteSpace(searchTerm)
                ? await _tagRepository.GetOrderedByNameAsync()
                : await _tagRepository.SearchByNameAsync(searchTerm.Trim());

            var chippedNames = DialogTags
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return tags
                .Select(t => t.TagName)
                .Where(n => !chippedNames.Contains(n))
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error searching tags for event dialog: {SearchTerm}", searchTerm);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Resolves the dialog's people chips to person ids: existing chips keep
    /// their id; new-person chips are created via
    /// <see cref="IPersonService.CreatePersonAsync"/>, with duplicate-name
    /// failures resolved to the existing person.
    /// </summary>
    private async Task<List<(string PersonId, string Name)>> ResolveDialogPeopleAsync()
    {
        var resolved = new List<(string PersonId, string Name)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chip in DialogPeople.ToList())
        {
            string personId;
            string personName;

            if (!string.IsNullOrEmpty(chip.PersonId))
            {
                personId = chip.PersonId!;
                personName = chip.Name;
            }
            else
            {
                try
                {
                    var created = await _personService.CreatePersonAsync(new PersonDto { Name = chip.Name });
                    personId = created.PersonId;
                    personName = created.Name;
                }
                catch (InvalidOperationException)
                {
                    // Duplicate name: reuse the existing person.
                    var existing = await _personService.GetPersonByNameAsync(chip.Name);
                    if (existing == null)
                    {
                        throw;
                    }

                    personId = existing.PersonId;
                    personName = existing.Name;
                }
            }

            if (seenIds.Add(personId))
            {
                resolved.Add((personId, personName));
            }
        }

        return resolved;
    }

    /// <summary>
    /// Diffs the dialog's people and tag chips against the link sets captured
    /// when the dialog opened and applies the adds/removes through
    /// <see cref="IEventService"/>. Missing tags are created first.
    /// </summary>
    private async Task ReconcileEventLinksAsync(
        string eventId,
        List<(string PersonId, string Name)> linkedPeople)
    {
        // People links.
        var desiredPeopleIds = linkedPeople
            .Select(p => p.PersonId)
            .ToHashSet(StringComparer.Ordinal);
        var originalPeopleIds = _originalPeopleIds.ToHashSet(StringComparer.Ordinal);

        foreach (var personId in desiredPeopleIds.Except(originalPeopleIds))
        {
            await _eventService.AddPersonToEventAsync(eventId, personId);
        }

        foreach (var personId in originalPeopleIds.Except(desiredPeopleIds))
        {
            await _eventService.RemovePersonFromEventAsync(eventId, personId);
        }

        // Tag links (create missing tags, then diff by tag id).
        var desiredTagIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chip in DialogTags.ToList())
        {
            var tag = await FindOrCreateTagAsync(chip.Name);
            desiredTagIds.Add(tag.TagId);
        }

        var originalTagIds = _originalTagIds.ToHashSet(StringComparer.Ordinal);

        foreach (var tagId in desiredTagIds.Except(originalTagIds))
        {
            await _eventService.AddTagToEventAsync(eventId, tagId);
        }

        foreach (var tagId in originalTagIds.Except(desiredTagIds))
        {
            await _eventService.RemoveTagFromEventAsync(eventId, tagId);
        }
    }

    /// <summary>
    /// Finds a tag by name (case-insensitive) or creates it. The LIKE-based
    /// search is used so casing differences don't create duplicate tags.
    /// </summary>
    private async Task<Tag> FindOrCreateTagAsync(string name)
    {
        var candidates = await _tagRepository.SearchByNameAsync(name);
        var match = candidates.FirstOrDefault(t =>
            string.Equals(t.TagName, name, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        return await _tagRepository.AddAsync(new Tag { TagName = name });
    }

    /// <summary>
    /// Deletes the draft the dialog was opened from (best-effort) after a
    /// successful real save, and clears <see cref="CurrentDraftId"/>.
    /// </summary>
    private async Task ClearCurrentDraftAsync()
    {
        var draftId = CurrentDraftId;
        if (string.IsNullOrEmpty(draftId))
        {
            return;
        }

        try
        {
            await _draftService.DeleteDraftAsync(draftId);
        }
        catch (Exception ex)
        {
            // The event saved fine; a failed draft cleanup must not fail the save.
            _logger.LogWarning(ex, "Error deleting consumed draft: {DraftId}", draftId);
        }
        finally
        {
            CurrentDraftId = null;
        }
    }

    /// <summary>
    /// Applies people names to the reloaded event DTO (the viewport query does
    /// not load person links) so the details panel shows them immediately.
    /// </summary>
    private void ApplyPeopleNamesToLoadedEvent(string eventId, IEnumerable<string> names)
    {
        var dto = FindLoadedEvent(eventId);
        if (dto != null)
        {
            dto.PeopleNames = names
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    #endregion
}

/// <summary>
/// An era option in the event dialog's era ComboBox. <see cref="EraId"/> is
/// null for the "None" option.
/// </summary>
public sealed class EventEraChoiceDto
{
    /// <summary>The era id, or null for "None".</summary>
    public string? EraId { get; init; }

    /// <summary>Display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The shared "None" option (no era).</summary>
    public static EventEraChoiceDto None { get; } = new() { EraId = null, Name = "None" };
}

/// <summary>
/// A person chip in the event dialog: an existing person (with id) or a
/// pending new person (null id) that gets created on save.
/// </summary>
public sealed class EventPersonChipDto
{
    /// <summary>The person id, or null for a pending new person.</summary>
    public string? PersonId { get; init; }

    /// <summary>The person's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Up to two uppercase initials for the avatar dot.</summary>
    public string Initials { get; init; } = "?";

    /// <summary>Avatar color as a hex string.</summary>
    public string ColorHex { get; init; } = "#808080";

    /// <summary>True when this chip is a pending new person.</summary>
    public bool IsNew => string.IsNullOrEmpty(PersonId);

    /// <summary>Avatar color as a brush for the chip's initials dot.</summary>
    public SolidColorBrush ColorBrush => CreateBrush(ColorHex);

    /// <summary>Creates a chip for an existing person.</summary>
    public static EventPersonChipDto FromPerson(PersonDto person) => new()
    {
        PersonId = person.PersonId,
        Name = person.Name,
        Initials = person.Initials,
        ColorHex = person.EffectiveAvatarColor
    };

    /// <summary>
    /// Creates a pending new-person chip; initials and color are derived the
    /// same way <see cref="PersonDto"/> derives them, so the chip looks
    /// identical after the person is really created.
    /// </summary>
    public static EventPersonChipDto ForNewPerson(string name)
    {
        var probe = new PersonDto { Name = name };
        return new EventPersonChipDto
        {
            PersonId = null,
            Name = name,
            Initials = probe.Initials,
            ColorHex = probe.EffectiveAvatarColor
        };
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        try
        {
            var value = hex.Replace("#", string.Empty);
            if (value.Length == 6)
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(
                    255,
                    Convert.ToByte(value.Substring(0, 2), 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16)));
            }
        }
        catch
        {
            // Fall through to gray.
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
    }
}

/// <summary>
/// A tag chip in the event dialog.
/// </summary>
public sealed class EventTagChipDto
{
    /// <summary>The tag name.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// One row of the swimlane gutter panel. Immutable - the gutter collection is
/// rebuilt on reload/mode-change/collapse-toggle (never per scroll tick), so
/// no change notification is needed.
/// </summary>
public sealed class LaneGutterRowDto
{
    public string LaneKey { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public int EventCount { get; init; }

    /// <summary>Row height in pixels; matches the lane row the events use.</summary>
    public double RowHeight { get; init; }

    public bool IsCollapsed { get; init; }

    /// <summary>Every other row gets a subtle stripe.</summary>
    public bool IsAlternate { get; init; }

    /// <summary>Chevron: down when expanded, right when collapsed.</summary>
    public string ChevronGlyph => IsCollapsed ? "\uE70E" : "\uE70D";

    public string CountDisplay => EventCount == 1 ? "1 event" : $"{EventCount} events";

    /// <summary>Stripe layer opacity (rows alternate 0 / 1 over a subtle brush).</summary>
    public double StripeOpacity => IsAlternate ? 1.0 : 0.0;

    public string AutomationLabel => $"Collapse or expand the {Label} lane";
}

/// <summary>
/// A media attachment shown in the event details photo strip and lightbox.
/// Paths are absolute, resolved through <see cref="IMediaService"/> from the
/// relative paths stored in the database.
/// </summary>
public sealed partial class EventMediaThumbDto : ObservableObject
{
    public string MediaId { get; init; } = string.Empty;

    public string EventId { get; init; } = string.Empty;

    public MediaType MediaType { get; init; }

    /// <summary>Absolute path of the copied file in the managed media tree.</summary>
    public string AbsolutePath { get; init; } = string.Empty;

    /// <summary>Absolute path of the generated thumbnail, or null.</summary>
    public string? AbsoluteThumbnailPath { get; init; }

    /// <summary>User caption (editable in the lightbox).</summary>
    [ObservableProperty]
    private string? _caption;

    public bool IsImage => MediaType == MediaType.Image;

    public string FileName => Path.GetFileName(AbsolutePath);

    /// <summary>Placeholder glyph for non-image attachments.</summary>
    public string FileGlyph => MediaType switch
    {
        MediaType.Video => "\uE714",    // Video
        MediaType.Audio => "\uE8D6",    // Audio
        MediaType.Document => "\uE8A5", // Document
        _ => "\uEB9F"                   // Photo
    };

    private BitmapImage? _thumbnailImage;
    private bool _thumbnailResolved;

    /// <summary>
    /// Strip thumbnail: the generated 256px JPEG when available, else the
    /// original image decoded small, else null (non-images show the glyph).
    /// Created lazily on first binding evaluation, which happens on the UI
    /// thread (BitmapImage is a DependencyObject and must not be constructed
    /// on a background thread).
    /// </summary>
    public BitmapImage? ThumbnailImage
    {
        get
        {
            if (!_thumbnailResolved)
            {
                _thumbnailResolved = true;
                _thumbnailImage = TryCreateImage(
                    AbsoluteThumbnailPath ?? (IsImage ? AbsolutePath : null),
                    decodePixelWidth: 144);
            }

            return _thumbnailImage;
        }
    }

    /// <summary>True when a strip thumbnail image could be created.</summary>
    public bool HasImage => ThumbnailImage != null;

    /// <summary>Full-resolution image for the lightbox (null for non-images).</summary>
    public BitmapImage? CreateFullImage()
        => IsImage ? TryCreateImage(AbsolutePath, decodePixelWidth: null) : null;

    /// <summary>Creates a display DTO from an entity, resolving absolute paths.</summary>
    public static EventMediaThumbDto FromMedia(EventMedia media, IMediaService mediaService) => new()
    {
        MediaId = media.MediaId,
        EventId = media.EventId,
        MediaType = media.MediaType,
        AbsolutePath = mediaService.GetAbsolutePath(media),
        AbsoluteThumbnailPath = mediaService.GetAbsoluteThumbnailPath(media),
        Caption = media.Caption
    };

    private static BitmapImage? TryCreateImage(string? path, int? decodePixelWidth)
    {
        // Defensive display formatting (mirrors EventPersonChipDto.CreateBrush):
        // a missing/unreadable file falls back to the glyph placeholder.
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage { UriSource = new Uri(path) };
            if (decodePixelWidth.HasValue)
            {
                image.DecodePixelWidth = decodePixelWidth.Value;
            }

            return image;
        }
        catch
        {
            return null;
        }
    }
}
