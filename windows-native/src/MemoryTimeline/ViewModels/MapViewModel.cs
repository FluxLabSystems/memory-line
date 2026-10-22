using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.Globalization;
using Windows.Foundation;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the offline Map page (F10): located places rendered as pins
/// over an equirectangular projection (no tiles, no network), grid clustering
/// at low zoom, a year-range scrubber, a per-year travel animation, the
/// unlocated-places manager (pin drop + optional geocoding) and the geocoding
/// privacy toggle.
///
/// DEFERRED (F10, still open 2026-10): the spec's per-event location chip on
/// the timeline (a pin glyph on event bubbles that jumps to the Map page) is
/// not implemented. The pieces it needs are all absent today: TimelineEventDto
/// carries only the location NAME (no coordinates or "located" flag), the
/// bubble templates in TimelineControl have no secondary hit target, and this
/// page takes no navigation parameter. When it is picked up: add a located
/// flag to the DTO, navigate with NavigationService.NavigateTo("Map", eventId),
/// read the parameter in MapPage.OnNavigatedTo after InitializeAsync, and
/// center the viewport on the pin whose event references include that id.
/// </summary>
public partial class MapViewModel : ObservableObject
{
    /// <summary>Screen-space cell size used for pin clustering.</summary>
    private const double ClusterCellSizePixels = 48.0;

    /// <summary>Half of the single-pin visual (28px) - centers the pin on its coordinate.</summary>
    private const double PinHalfSize = 14.0;

    /// <summary>Half of the cluster badge visual (36px).</summary>
    private const double ClusterHalfSize = 18.0;

    /// <summary>Half of the travel marker visual (14px).</summary>
    private const double TravelMarkerHalf = 7.0;

    /// <summary>Duration of each travel-animation leg.</summary>
    private const double TravelSegmentDurationMs = 1200.0;

    /// <summary>Travel timer tick interval (~30 fps).</summary>
    private const double TravelTickMs = 33.0;

    private readonly ILocationRepository _locationRepository;
    private readonly IGeocodingService _geocodingService;
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<MapViewModel> _logger;
    private readonly DispatcherQueue? _dispatcherQueue;

    /// <summary>All located places with their event references (unfiltered).</summary>
    private List<LocationPinData> _locatedData = new();

    private bool _suppressSettingWrites;
    private bool _isApplyingYearBounds;
    private bool _viewportInitialized;
    private bool _centeredOnData;

    // Pin-drop state
    private string? _pinDropLocationId;
    private string? _pinDropName;

    // Travel-animation state
    private DispatcherQueueTimer? _travelTimer;
    private List<TravelSegmentDto> _travelSegments = new();
    private int _travelSegmentIndex;
    private double _travelSegmentProgress;

    /// <summary>The current map window. Mutated in place by pan/zoom.</summary>
    public MapViewport Viewport { get; } = new();

    /// <summary>Current map surface size in pixels (set by the page on SizeChanged).</summary>
    public double ViewWidth { get; private set; }

    public double ViewHeight { get; private set; }

    /// <summary>
    /// Raised with the projected polyline points when a travel path is built
    /// (empty list = clear). The page mirrors them into Polyline.Points.
    /// </summary>
    public event EventHandler<IReadOnlyList<Point>>? TravelPathChanged;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Confirmation / informational line (Informational InfoBar).</summary>
    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _locatedSummary = string.Empty;

    /// <summary>True when no location has coordinates yet - shows the map empty state.</summary>
    [ObservableProperty]
    private bool _showMapEmptyState;

    [ObservableProperty]
    private ObservableCollection<MapDisplayPin> _displayPins = new();

    [ObservableProperty]
    private ObservableCollection<UnlocatedPlaceItem> _unlocatedPlaces = new();

    [ObservableProperty]
    private bool _hasUnlocated;

    [ObservableProperty]
    private bool _isGeocodingEnabled;

    [ObservableProperty]
    private string _geocodingProviderText = "Provider: OpenStreetMap Nominatim";

    [ObservableProperty]
    private bool _isGeocodingBusy;

    [ObservableProperty]
    private string? _geocodeProgressText;

    // Year-range scrubber (two sliders acting as a RangeSelector)
    [ObservableProperty]
    private double _dataMinYear = DateTime.Now.Year;

    [ObservableProperty]
    private double _dataMaxYear = DateTime.Now.Year;

    [ObservableProperty]
    private double _yearRangeStart = DateTime.Now.Year;

    [ObservableProperty]
    private double _yearRangeEnd = DateTime.Now.Year;

    [ObservableProperty]
    private string _yearRangeText = string.Empty;

    // Travel animation
    [ObservableProperty]
    private ObservableCollection<string> _travelYears = new();

    [ObservableProperty]
    private string? _selectedTravelYear;

    [ObservableProperty]
    private bool _isTravelPlaying;

    [ObservableProperty]
    private bool _isTravelMarkerVisible;

    [ObservableProperty]
    private double _travelMarkerX;

    [ObservableProperty]
    private double _travelMarkerY;

    [ObservableProperty]
    private string? _travelStatusText;

    // Pin-drop mode
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinInteractionEnabled))]
    private bool _isPinDropMode;

    [ObservableProperty]
    private string? _pinDropPrompt;

    /// <summary>Pins ignore the pointer while a pin drop is armed, so the map click lands on the surface.</summary>
    public bool IsPinInteractionEnabled => !IsPinDropMode;

    public MapViewModel(
        ILocationRepository locationRepository,
        IGeocodingService geocodingService,
        ISettingsService settingsService,
        INavigationService navigationService,
        ILogger<MapViewModel> logger)
    {
        _locationRepository = locationRepository;
        _geocodingService = geocodingService;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>Marshals the action onto the UI dispatcher thread when needed.</summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// Loads settings and map data. Called from MapPage.OnNavigatedTo; safe to
    /// call again (each navigation refreshes the page).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var enabled = await _settingsService.GetSettingAsync<string>(SettingKeys.GeocodingEnabled, "false");
            var provider = await _settingsService.GetSettingAsync<string>(SettingKeys.GeocodingProvider, "nominatim");
            var defaultZoom = await _settingsService.GetSettingAsync<string>(SettingKeys.MapDefaultZoom, "1.0");

            RunOnUi(() =>
            {
                // Reflect the stored setting without writing it back.
                _suppressSettingWrites = true;
                IsGeocodingEnabled = string.Equals(enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                _suppressSettingWrites = false;

                GeocodingProviderText = string.Equals(provider?.Trim(), "nominatim", StringComparison.OrdinalIgnoreCase)
                    ? "Provider: OpenStreetMap Nominatim"
                    : $"Provider: {provider}";

                if (!_viewportInitialized)
                {
                    if (double.TryParse(defaultZoom, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
                    {
                        Viewport.ZoomScale = zoom;
                    }

                    Viewport.Clamp();
                    _viewportInitialized = true;
                }
            });

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the Map page");
            RunOnUi(() => ErrorMessage = $"Could not load the map: {ex.Message}");
        }
        finally
        {
            RunOnUi(() => IsLoading = false);
        }
    }

    /// <summary>Reloads locations, links and derived collections.</summary>
    private async Task LoadDataAsync()
    {
        var located = (await _locationRepository.GetLocatedAsync()).ToList();
        var unlocated = (await _locationRepository.GetUnlocatedAsync()).ToList();
        var links = (await _locationRepository.GetEventLocationLinksAsync()).ToList();

        var eventsByLocation = links
            .Where(l => l.Event != null)
            .GroupBy(l => l.LocationId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => new LocationEventRef(
                        l.Event.EventId, l.Event.Title, l.Event.StartDate, l.Event.DatePrecision))
                    .OrderBy(r => r.StartDate)
                    .ToList());

        var locatedData = located
            .Select(l => new LocationPinData(
                l.LocationId,
                l.Name,
                l.Latitude!.Value,
                l.Longitude!.Value,
                eventsByLocation.TryGetValue(l.LocationId, out var events) ? events : new List<LocationEventRef>()))
            .ToList();

        RunOnUi(() =>
        {
            _locatedData = locatedData;

            // Year bounds from located events; the scrubber resets to the full
            // range on each load so freshly located places are never filtered
            // away invisibly.
            var years = _locatedData.SelectMany(d => d.Events).Select(e => e.StartDate.Year).ToList();
            _isApplyingYearBounds = true;
            if (years.Count > 0)
            {
                DataMinYear = years.Min();
                DataMaxYear = years.Max();
            }
            else
            {
                DataMinYear = DataMaxYear = DateTime.Now.Year;
            }

            YearRangeStart = DataMinYear;
            YearRangeEnd = DataMaxYear;
            _isApplyingYearBounds = false;
            UpdateYearRangeText();

            // First load with data: land the view on the pins.
            if (!_centeredOnData && _locatedData.Count > 0)
            {
                Viewport.CenterLat = _locatedData.Average(d => d.Latitude);
                Viewport.CenterLon = _locatedData.Average(d => d.Longitude);
                Viewport.Clamp();
                _centeredOnData = true;
            }

            // Travel years (descending); keep the current selection when possible.
            var travelYears = _locatedData
                .SelectMany(d => d.Events)
                .Select(e => e.StartDate.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .Select(y => y.ToString(CultureInfo.InvariantCulture))
                .ToList();
            var previousSelection = SelectedTravelYear;
            TravelYears.Clear();
            foreach (var year in travelYears)
            {
                TravelYears.Add(year);
            }

            SelectedTravelYear = previousSelection != null && travelYears.Contains(previousSelection)
                ? previousSelection
                : travelYears.FirstOrDefault();

            // Unlocated places manager.
            UnlocatedPlaces.Clear();
            foreach (var location in unlocated)
            {
                var eventCount = eventsByLocation.TryGetValue(location.LocationId, out var events) ? events.Count : 0;
                var item = new UnlocatedPlaceItem
                {
                    LocationId = location.LocationId,
                    Name = location.Name,
                    EventCountText = eventCount == 1 ? "1 event" : $"{eventCount} events",
                    CanLookUp = IsGeocodingEnabled
                };
                item.SetLocationCommand = new RelayCommand(() => StartPinDrop(item));
                item.LookUpCommand = new AsyncRelayCommand(() => LookUpAsync(item));
                UnlocatedPlaces.Add(item);
            }

            HasUnlocated = UnlocatedPlaces.Count > 0;
            var total = located.Count + unlocated.Count;
            LocatedSummary = total == 0
                ? "No places yet - locations appear here once events mention them"
                : $"{located.Count} of {total} places have coordinates";
            ShowMapEmptyState = located.Count == 0;

            GeocodeAllMissingCommand.NotifyCanExecuteChanged();
            PlayTravelCommand.NotifyCanExecuteChanged();
            RebuildDisplayPins();
        });
    }

    #region Pan / zoom / view size (called by the page's pointer handlers)

    /// <summary>The page reports the map surface size here (SizeChanged).</summary>
    public void SetViewSize(double width, double height)
    {
        if (width <= 0 || height <= 0 || (Math.Abs(width - ViewWidth) < 0.5 && Math.Abs(height - ViewHeight) < 0.5))
        {
            return;
        }

        ViewWidth = width;
        ViewHeight = height;
        if (IsTravelPlaying)
        {
            StopTravel();
        }

        RebuildDisplayPins();
    }

    /// <summary>Drag pan by a pixel delta (content follows the pointer).</summary>
    public void PanBy(double deltaX, double deltaY)
    {
        if (ViewWidth <= 0)
        {
            return;
        }

        if (IsTravelPlaying)
        {
            StopTravel();
        }

        Viewport.Pan(deltaX, deltaY, ViewWidth);
        UpdatePinPositions();
    }

    /// <summary>Pointer-wheel zoom about the cursor position.</summary>
    public void ZoomAboutPoint(double factor, double anchorX, double anchorY)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0)
        {
            return;
        }

        if (IsTravelPlaying)
        {
            StopTravel();
        }

        Viewport.ZoomAbout(factor, anchorX, anchorY, ViewWidth, ViewHeight);
        // Cluster granularity depends on zoom, so rebuild rather than reposition.
        RebuildDisplayPins();
    }

    /// <summary>Cluster badge click: zoom in about the cluster centroid.</summary>
    public void ZoomIntoCluster(MapDisplayPin pin)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0)
        {
            return;
        }

        if (IsTravelPlaying)
        {
            StopTravel();
        }

        Viewport.CenterLat = pin.Latitude;
        Viewport.CenterLon = pin.Longitude;
        Viewport.ZoomScale = Math.Clamp(Viewport.ZoomScale * 2.5, MapViewport.MinZoom, MapViewport.MaxZoom);
        Viewport.Clamp();
        RebuildDisplayPins();
    }

    #endregion

    #region Pins

    /// <summary>Recomputes clustering and rebuilds the pin collection (zoom / filter / data changes).</summary>
    private void RebuildDisplayPins()
    {
        DisplayPins.Clear();
        if (ViewWidth <= 0 || ViewHeight <= 0 || _locatedData.Count == 0)
        {
            return;
        }

        var startYear = (int)Math.Round(Math.Min(YearRangeStart, YearRangeEnd));
        var endYear = (int)Math.Round(Math.Max(YearRangeStart, YearRangeEnd));

        var pinDtos = new List<MapPinDto>();
        var eventsForPin = new Dictionary<string, List<LocationEventRef>>(StringComparer.Ordinal);
        foreach (var data in _locatedData)
        {
            var filtered = data.Events
                .Where(e => e.StartDate.Year >= startYear && e.StartDate.Year <= endYear)
                .ToList();

            // A place whose events all fall outside the scrubbed range drops
            // out; a place with no events at all is always shown.
            if (data.Events.Count > 0 && filtered.Count == 0)
            {
                continue;
            }

            pinDtos.Add(new MapPinDto
            {
                LocationId = data.LocationId,
                Name = data.Name,
                Latitude = data.Latitude,
                Longitude = data.Longitude,
                EventCount = filtered.Count,
                EarliestEvent = filtered.Count > 0 ? filtered[0].StartDate : null,
                LatestEvent = filtered.Count > 0 ? filtered[^1].StartDate : null
            });
            eventsForPin[data.LocationId] = filtered;
        }

        var clusters = GridClusterer.Cluster(pinDtos, ClusterCellSizePixels, Viewport, ViewWidth, ViewHeight);
        foreach (var cluster in clusters)
        {
            if (cluster.IsCluster)
            {
                DisplayPins.Add(new MapDisplayPin
                {
                    IsCluster = true,
                    Label = $"{cluster.Pins.Count} places, {cluster.TotalEventCount} events - click to zoom in",
                    CountText = cluster.Pins.Count.ToString(CultureInfo.InvariantCulture),
                    Latitude = cluster.CentroidLatitude,
                    Longitude = cluster.CentroidLongitude,
                    X = cluster.X - ClusterHalfSize,
                    Y = cluster.Y - ClusterHalfSize
                });
                continue;
            }

            var pin = cluster.Pins[0];
            var item = new MapDisplayPin
            {
                IsCluster = false,
                LocationId = pin.LocationId,
                Label = pin.Name,
                CountText = pin.EventCount.ToString(CultureInfo.InvariantCulture),
                EventCountText = pin.EventCount == 1 ? "1 event here" : $"{pin.EventCount} events here",
                Latitude = pin.Latitude,
                Longitude = pin.Longitude,
                X = cluster.X - PinHalfSize,
                Y = cluster.Y - PinHalfSize
            };

            foreach (var eventRef in eventsForPin[pin.LocationId])
            {
                var eventItem = new MapPinEventItem
                {
                    EventId = eventRef.EventId,
                    Title = eventRef.Title,
                    DateText = DateDisplay.FormatPrecise(eventRef.StartDate, eventRef.Precision)
                };
                eventItem.OpenCommand = new RelayCommand(() => OpenEvent(eventItem.EventId));
                item.Events.Add(eventItem);
            }

            DisplayPins.Add(item);
        }
    }

    /// <summary>Repositions existing pins in place (pan only - clustering unchanged).</summary>
    private void UpdatePinPositions()
    {
        foreach (var item in DisplayPins)
        {
            var (x, y) = MapProjection.Project(item.Latitude, item.Longitude, ViewWidth, ViewHeight, Viewport);
            var half = item.IsCluster ? ClusterHalfSize : PinHalfSize;
            item.X = x - half;
            item.Y = y - half;
        }
    }

    /// <summary>Pin flyout event click: open the event on the timeline.</summary>
    private void OpenEvent(string eventId)
    {
        _navigationService.NavigateTo("Timeline", eventId);
    }

    #endregion

    #region Year-range scrubber

    partial void OnYearRangeStartChanged(double value)
    {
        if (_isApplyingYearBounds)
        {
            return;
        }

        if (YearRangeEnd < value)
        {
            YearRangeEnd = value; // its handler re-filters
            return;
        }

        UpdateYearRangeText();
        RebuildDisplayPins();
    }

    partial void OnYearRangeEndChanged(double value)
    {
        if (_isApplyingYearBounds)
        {
            return;
        }

        if (value < YearRangeStart)
        {
            YearRangeStart = value; // its handler re-filters
            return;
        }

        UpdateYearRangeText();
        RebuildDisplayPins();
    }

    private void UpdateYearRangeText()
    {
        var start = (int)Math.Round(Math.Min(YearRangeStart, YearRangeEnd));
        var end = (int)Math.Round(Math.Max(YearRangeStart, YearRangeEnd));
        YearRangeText = start == end ? start.ToString(CultureInfo.InvariantCulture) : $"{start} - {end}";
    }

    #endregion

    #region Travel animation

    private bool CanPlayTravel() => !IsTravelPlaying && !string.IsNullOrEmpty(SelectedTravelYear) && _locatedData.Count > 0;

    partial void OnSelectedTravelYearChanged(string? value)
    {
        PlayTravelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTravelPlayingChanged(bool value)
    {
        PlayTravelCommand.NotifyCanExecuteChanged();
        StopTravelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPlayTravel))]
    private void PlayTravel()
    {
        if (!int.TryParse(SelectedTravelYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            return;
        }

        var stops = BuildTravelStops(year);
        if (stops.Count < 2)
        {
            StatusMessage = $"The travel animation needs at least two located events in {year}.";
            return;
        }

        if (_dispatcherQueue == null)
        {
            _logger.LogWarning("No dispatcher queue available; cannot run the travel animation.");
            return;
        }

        _travelSegments = new List<TravelSegmentDto>();
        for (var i = 0; i < stops.Count - 1; i++)
        {
            _travelSegments.Add(new TravelSegmentDto
            {
                FromEventId = stops[i].EventId,
                FromTitle = stops[i].Title,
                FromDate = stops[i].Date,
                FromLatitude = stops[i].Latitude,
                FromLongitude = stops[i].Longitude,
                ToEventId = stops[i + 1].EventId,
                ToTitle = stops[i + 1].Title,
                ToDate = stops[i + 1].Date,
                ToLatitude = stops[i + 1].Latitude,
                ToLongitude = stops[i + 1].Longitude
            });
        }

        var pathPoints = stops
            .Select(s =>
            {
                var (x, y) = MapProjection.Project(s.Latitude, s.Longitude, ViewWidth, ViewHeight, Viewport);
                return new Point(x, y);
            })
            .ToList();
        TravelPathChanged?.Invoke(this, pathPoints);

        _travelSegmentIndex = 0;
        _travelSegmentProgress = 0;
        MoveTravelMarker(stops[0].Latitude, stops[0].Longitude);
        IsTravelMarkerVisible = true;
        IsTravelPlaying = true;
        TravelStatusText = $"{stops.Count} stops in {year}";

        _travelTimer ??= _dispatcherQueue.CreateTimer();
        _travelTimer.Interval = TimeSpan.FromMilliseconds(TravelTickMs);
        _travelTimer.Tick -= OnTravelTick;
        _travelTimer.Tick += OnTravelTick;
        _travelTimer.Start();
    }

    [RelayCommand(CanExecute = nameof(IsTravelPlaying))]
    private void StopTravel()
    {
        _travelTimer?.Stop();
        IsTravelPlaying = false;
        IsTravelMarkerVisible = false;
        TravelStatusText = null;
        TravelPathChanged?.Invoke(this, Array.Empty<Point>());
    }

    private void OnTravelTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsTravelPlaying || _travelSegmentIndex >= _travelSegments.Count)
        {
            StopTravel();
            return;
        }

        _travelSegmentProgress += TravelTickMs / TravelSegmentDurationMs;
        if (_travelSegmentProgress >= 1.0)
        {
            _travelSegmentIndex++;
            _travelSegmentProgress = 0;
            if (_travelSegmentIndex >= _travelSegments.Count)
            {
                StopTravel();
                return;
            }
        }

        var segment = _travelSegments[_travelSegmentIndex];

        // Smoothstep easing: gentle start/stop per leg.
        var t = _travelSegmentProgress;
        t = t * t * (3.0 - 2.0 * t);

        var latitude = segment.FromLatitude + (segment.ToLatitude - segment.FromLatitude) * t;
        var longitude = segment.FromLongitude + (segment.ToLongitude - segment.FromLongitude) * t;
        MoveTravelMarker(latitude, longitude);
        TravelStatusText = $"{segment.FromTitle}  →  {segment.ToTitle}";
    }

    private void MoveTravelMarker(double latitude, double longitude)
    {
        var (x, y) = MapProjection.Project(latitude, longitude, ViewWidth, ViewHeight, Viewport);
        TravelMarkerX = x - TravelMarkerHalf;
        TravelMarkerY = y - TravelMarkerHalf;
    }

    /// <summary>
    /// The year's located events in chronological order, each mapped to its
    /// first located place. An event linked to several located places uses the
    /// first one; an event with only unlocated places is skipped.
    /// </summary>
    private List<TravelStop> BuildTravelStops(int year)
    {
        var stops = new Dictionary<string, TravelStop>(StringComparer.Ordinal);
        foreach (var data in _locatedData)
        {
            foreach (var eventRef in data.Events)
            {
                if (eventRef.StartDate.Year != year || stops.ContainsKey(eventRef.EventId))
                {
                    continue;
                }

                stops[eventRef.EventId] = new TravelStop(
                    eventRef.EventId, eventRef.Title, eventRef.StartDate, data.Latitude, data.Longitude);
            }
        }

        return stops.Values
            .OrderBy(s => s.Date)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    #endregion

    #region Unlocated places: pin drop + geocoding

    private void StartPinDrop(UnlocatedPlaceItem item)
    {
        _pinDropLocationId = item.LocationId;
        _pinDropName = item.Name;
        PinDropPrompt = $"Click the map where '{item.Name}' belongs";
        IsPinDropMode = true;
    }

    [RelayCommand]
    private void CancelPinDrop()
    {
        IsPinDropMode = false;
        PinDropPrompt = null;
        _pinDropLocationId = null;
        _pinDropName = null;
    }

    /// <summary>
    /// Pin-drop completion: the page's map-click coordinates unproject to a
    /// geographic coordinate that is persisted with source "manual" (GeocodedAt
    /// stays null - the name never left the machine).
    /// </summary>
    public async Task CompletePinDropAsync(double clickX, double clickY)
    {
        var locationId = _pinDropLocationId;
        var name = _pinDropName;
        if (!IsPinDropMode || locationId == null || ViewWidth <= 0 || ViewHeight <= 0)
        {
            return;
        }

        try
        {
            var (latitude, longitude) = MapProjection.Unproject(clickX, clickY, ViewWidth, ViewHeight, Viewport);
            (latitude, longitude) = MapProjection.ClampToWorld(latitude, longitude);

            await _locationRepository.SetCoordinatesAsync(locationId, latitude, longitude, "manual");

            RunOnUi(() => StatusMessage =
                $"'{name}' placed at {latitude.ToString("0.####", CultureInfo.InvariantCulture)}, {longitude.ToString("0.####", CultureInfo.InvariantCulture)}.");
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set coordinates for location {LocationId}", locationId);
            RunOnUi(() => ErrorMessage = $"Could not set the location: {ex.Message}");
        }
        finally
        {
            RunOnUi(CancelPinDrop);
        }
    }

    private bool CanGeocodeAll() => IsGeocodingEnabled && !IsGeocodingBusy && HasUnlocated;

    partial void OnIsGeocodingEnabledChanged(bool value)
    {
        foreach (var item in UnlocatedPlaces)
        {
            item.CanLookUp = value;
        }

        GeocodeAllMissingCommand.NotifyCanExecuteChanged();

        if (_suppressSettingWrites)
        {
            return;
        }

        _ = PersistGeocodingEnabledAsync(value);
    }

    partial void OnIsGeocodingBusyChanged(bool value)
    {
        GeocodeAllMissingCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasUnlocatedChanged(bool value)
    {
        GeocodeAllMissingCommand.NotifyCanExecuteChanged();
    }

    private async Task PersistGeocodingEnabledAsync(bool value)
    {
        try
        {
            await _settingsService.SetSettingAsync(SettingKeys.GeocodingEnabled, value ? "true" : "false");
            RunOnUi(() => StatusMessage = value
                ? "Geocoding is on: place names you look up are sent to OpenStreetMap Nominatim."
                : "Geocoding is off: no place names leave this device.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the geocoding setting");
            RunOnUi(() =>
            {
                ErrorMessage = $"Could not save the geocoding setting: {ex.Message}";
                // Revert the toggle to the persisted state.
                _suppressSettingWrites = true;
                IsGeocodingEnabled = !value;
                _suppressSettingWrites = false;
            });
        }
    }

    /// <summary>Per-item "Look up": geocode one place and refresh.</summary>
    private async Task LookUpAsync(UnlocatedPlaceItem item)
    {
        if (IsGeocodingBusy)
        {
            return;
        }

        IsGeocodingBusy = true;
        try
        {
            var found = await _geocodingService.GeocodeLocationAsync(item.LocationId);
            RunOnUi(() => StatusMessage = found
                ? $"Found coordinates for '{item.Name}'."
                : $"No match found for '{item.Name}'. Try 'Set location' to place it by hand.");
            if (found)
            {
                await LoadDataAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Geocoding lookup failed for location {LocationId}", item.LocationId);
            RunOnUi(() => ErrorMessage = $"Could not look up '{item.Name}': {ex.Message}");
        }
        finally
        {
            RunOnUi(() => IsGeocodingBusy = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGeocodeAll))]
    private async Task GeocodeAllMissingAsync()
    {
        IsGeocodingBusy = true;
        GeocodeProgressText = "Looking up places…";
        try
        {
            // Progress<T> posts to the UI synchronization context it was
            // created on (this UI thread), so the lambda mutates safely.
            var progress = new Progress<(int done, int total)>(p =>
                GeocodeProgressText = $"Looked up {p.done} of {p.total} places…");
            var succeeded = await _geocodingService.GeocodeMissingAsync(progress);
            RunOnUi(() => StatusMessage = succeeded == 1
                ? "1 place geocoded."
                : $"{succeeded} places geocoded.");
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch geocoding failed");
            RunOnUi(() => ErrorMessage = $"Could not geocode places: {ex.Message}");
        }
        finally
        {
            RunOnUi(() =>
            {
                IsGeocodingBusy = false;
                GeocodeProgressText = null;
            });
        }
    }

    #endregion

    /// <summary>Stops timers when the page navigates away (transient VM).</summary>
    public void Cleanup()
    {
        if (IsTravelPlaying)
        {
            StopTravel();
        }

        _travelTimer?.Stop();
    }

    /// <summary>One located place with its (unfiltered) event references.</summary>
    private sealed record LocationPinData(
        string LocationId,
        string Name,
        double Latitude,
        double Longitude,
        List<LocationEventRef> Events);

    private sealed record LocationEventRef(string EventId, string Title, DateTime StartDate, DatePrecision Precision);

    private sealed record TravelStop(string EventId, string Title, DateTime Date, double Latitude, double Longitude);
}

/// <summary>
/// One rendered map item: a single place pin (flyout with its events) or a
/// cluster badge (click zooms in). X/Y are the top-left of the visual,
/// already centered on the projected coordinate; they update in place on pan.
/// </summary>
public partial class MapDisplayPin : ObservableObject
{
    /// <summary>Null for clusters.</summary>
    public string? LocationId { get; init; }

    public string Label { get; init; } = string.Empty;

    /// <summary>Number shown inside the pin: events here (pin) or member places (cluster).</summary>
    public string CountText { get; init; } = string.Empty;

    /// <summary>Flyout line, e.g. "3 events here".</summary>
    public string EventCountText { get; init; } = string.Empty;

    public bool IsCluster { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    /// <summary>The place's events (filtered to the scrubbed year range), for the flyout.</summary>
    public List<MapPinEventItem> Events { get; } = new();

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;
}

/// <summary>One event row in a pin's flyout; the command is set by the ViewModel.</summary>
public class MapPinEventItem
{
    public string EventId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Precision-honest date text (e.g. "Summer 1998").</summary>
    public string DateText { get; set; } = string.Empty;

    public IRelayCommand? OpenCommand { get; set; }
}

/// <summary>One row in the "Unlocated places" panel; commands are set by the ViewModel.</summary>
public partial class UnlocatedPlaceItem : ObservableObject
{
    public string LocationId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string EventCountText { get; init; } = string.Empty;

    /// <summary>Mirrors the geocoding toggle so the per-item "Look up" button hides when geocoding is off.</summary>
    [ObservableProperty]
    private bool _canLookUp;

    public IRelayCommand? SetLocationCommand { get; set; }

    public IAsyncRelayCommand? LookUpCommand { get; set; }
}
