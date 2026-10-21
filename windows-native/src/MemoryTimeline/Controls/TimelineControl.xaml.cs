using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using MemoryTimeline.Services;
using MemoryTimeline.ViewModels;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data.Models;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MemoryTimeline.Controls;

/// <summary>
/// Custom timeline visualization control with pen/touch navigation support.
/// Supports pinch-to-zoom, touch panning, momentum scrolling, and double-tap gestures.
/// Uses a Premiere Pro-style proportional scrollbar for navigation.
/// </summary>
public sealed partial class TimelineControl : UserControl
{
    private TimelineViewModel? _viewModel;
    private double _lastScale = 1.0;
    private Windows.Foundation.Point _lastManipulationPosition;
    private bool _isManipulating;
    private Windows.Foundation.Point _lastRightTapPosition;
    private TimelineEventDto? _contextMenuEvent;
    private string? _editingEventId;
    private bool _isUpdatingScrollbar;

    // Cached last hovered date (day-resolution) to avoid re-formatting/updating the hover
    // TextBlock on every pointer move within the same day.
    private DateTime _lastHoverDate = DateTime.MinValue;

    // Default sequence duration in days (100 years = ~36525 days)
    private const double DefaultSequenceDurationDays = 36525;

    /// <summary>
    /// Gets or sets the ViewModel for the timeline.
    /// </summary>
    public TimelineViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            InitializeTimeline();

            // The compiled bindings inside this control are rooted at the (plain CLR)
            // ViewModel property, which does not raise change notifications. Force the
            // x:Bind expressions to re-evaluate now that the ViewModel is available,
            // otherwise the timeline can render empty until the next refresh.
            Bindings.Update();
        }
    }

    public TimelineControl()
    {
        InitializeComponent();
        Loaded += TimelineControl_Loaded;
        Unloaded += TimelineControl_Unloaded;
        SizeChanged += TimelineControl_SizeChanged;

        // Wire up pointer wheel for cursor-anchored zoom
        TimelineCanvas.PointerWheelChanged += TimelineCanvas_PointerWheelChanged;
    }

    private void TimelineControl_Unloaded(object sender, RoutedEventArgs e)
    {
        // The ViewModel is a long-lived singleton; failing to unsubscribe here roots this
        // (transient) control for the lifetime of the app, leaking a control per navigation.
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }

    private async void TimelineControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;

        // Initialize with actual control size
        var width = (int)ActualWidth;
        var height = (int)ActualHeight;

        if (width > 0 && height > 0)
        {
            // Only initialize if not already initialized (preserves state across navigation)
            if (_viewModel.Viewport == null)
            {
                await _viewModel.InitializeAsync(width, height);
            }
            else
            {
                // Just update viewport dimensions if already initialized
                await _viewModel.UpdateViewportDimensionsAsync(width, height);
            }
            UpdateTimelineSize();
            UpdateScrollbarFromViewport();
        }

        // The ViewModel is a singleton; reflect its current lane mode in the
        // freshly created dropdown.
        SyncLaneModeRadios();
    }

    private async void TimelineControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel == null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;

        await _viewModel.UpdateViewportDimensionsAsync(
            (int)e.NewSize.Width,
            (int)e.NewSize.Height);

        UpdateTimelineSize();
    }

    private void InitializeTimeline()
    {
        if (_viewModel == null)
            return;

        // Set up property changed listeners.
        // Unsubscribe first so repeated ViewModel assignments never double-subscribe.
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = _viewModel;
        if (vm == null)
            return;

        void Apply()
        {
            // Guard against updates that arrive after the control was unloaded.
            if (_viewModel == null)
                return;

            if (e.PropertyName == nameof(TimelineViewModel.IsLoading))
            {
                LoadingOverlay.Visibility = _viewModel.IsLoading
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else if (e.PropertyName == nameof(TimelineViewModel.Viewport))
            {
                UpdateTimelineSize();
                UpdateScrollbarFromViewport();
            }
        }

        // The ViewModel may raise change notifications from a background thread
        // (e.g. after an awaited service call); marshal UI updates to the UI thread.
        var dispatcher = DispatcherQueue;
        if (dispatcher == null || dispatcher.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            dispatcher.TryEnqueue(Apply);
        }
    }

    private void UpdateTimelineSize()
    {
        if (_viewModel?.Viewport == null)
            return;

        // Update axis line to span the full width (only if width is valid)
        var containerWidth = TimelineContainer.ActualWidth;
        if (containerWidth > 0 && !double.IsNaN(containerWidth))
        {
            TimelineAxis.X2 = containerWidth;
        }
    }

    /// <summary>
    /// Updates the proportional scrollbar to reflect the current viewport state.
    /// </summary>
    private void UpdateScrollbarFromViewport()
    {
        if (_viewModel?.Viewport == null || _isUpdatingScrollbar)
            return;

        _isUpdatingScrollbar = true;
        try
        {
            var viewport = _viewModel.Viewport;

            // Calculate the full sequence range (all possible time)
            // Use MinDate to MaxDate as the full scrollable range
            var sequenceStartDate = viewport.MinDate;
            var sequenceEndDate = viewport.MaxDate;
            var sequenceDuration = (sequenceEndDate - sequenceStartDate).TotalDays;

            // View duration is the visible days in the viewport
            var viewDuration = viewport.VisibleDays;

            // Scroll offset is how far the viewport start is from the sequence start
            var scrollOffset = (viewport.StartDate - sequenceStartDate).TotalDays;

            // Update the scrollbar
            TimelineScrollBar.SequenceDuration = Math.Max(1, sequenceDuration);
            TimelineScrollBar.ViewDuration = Math.Max(1, Math.Min(viewDuration, sequenceDuration));
            TimelineScrollBar.ScrollOffset = Math.Clamp(scrollOffset, 0, Math.Max(0, sequenceDuration - viewDuration));
        }
        finally
        {
            _isUpdatingScrollbar = false;
        }
    }

    #region Proportional Scrollbar Handlers

    /// <summary>
    /// Handles scroll offset changes from the proportional scrollbar.
    /// </summary>
    private async void TimelineScrollBar_ScrollOffsetChanged(object sender, ScrollOffsetChangedEventArgs e)
    {
        if (_viewModel?.Viewport == null || _isUpdatingScrollbar)
            return;

        _isUpdatingScrollbar = true;
        try
        {
            // Calculate the new start date based on scroll offset
            var sequenceStartDate = _viewModel.Viewport.MinDate;
            var newStartDate = sequenceStartDate.AddDays(e.NewScrollOffset);

            // Calculate how much we need to pan
            var currentStartDate = _viewModel.Viewport.StartDate;
            var daysDelta = (newStartDate - currentStartDate).TotalDays;
            var pixelDelta = daysDelta * _viewModel.Viewport.PixelsPerDay;

            // Pan the viewport
            if (Math.Abs(pixelDelta) > 0.1)
            {
                await _viewModel.PanAsync(-pixelDelta);
            }
        }
        finally
        {
            _isUpdatingScrollbar = false;
        }
    }

    /// <summary>
    /// Handles view duration (zoom) changes from the proportional scrollbar
    /// edges. The visual scale change applies immediately to the already-
    /// loaded items (cheap, in-memory); the database reload is debounced by
    /// the ViewModel so an edge drag costs one reload, not one per tick.
    /// </summary>
    private void TimelineScrollBar_ViewDurationChanged(object sender, ViewDurationChangedEventArgs e)
    {
        if (_viewModel?.Viewport == null || _isUpdatingScrollbar)
            return;

        _isUpdatingScrollbar = true;
        try
        {
            // Calculate new pixels per day based on new view duration
            var newPixelsPerDay = _viewModel.Viewport.ViewportWidth / e.NewViewDuration;

            // Calculate the new start date
            var sequenceStartDate = _viewModel.Viewport.MinDate;
            var newStartDate = sequenceStartDate.AddDays(e.NewScrollOffset);
            var newEndDate = newStartDate.AddDays(e.NewViewDuration);
            var newCenterDate = newStartDate.AddDays(e.NewViewDuration / 2);

            // Update viewport directly
            _viewModel.Viewport.StartDate = newStartDate;
            _viewModel.Viewport.EndDate = newEndDate;
            _viewModel.Viewport.CenterDate = newCenterDate;
            _viewModel.Viewport.PixelsPerDay = newPixelsPerDay;
            _viewModel.Viewport.ZoomLevel = ZoomHelper.GetClosestZoomLevel(newPixelsPerDay);

            // Immediate re-layout of loaded items + trailing debounced reload.
            _viewModel.NotifyViewportChangedExternally();
        }
        finally
        {
            _isUpdatingScrollbar = false;
        }
    }

    #endregion

    #region Swimlane Handlers

    /// <summary>
    /// Applies the lane mode chosen in the CommandBar "Lanes" dropdown. The
    /// ViewModel re-lays-out the already-loaded events without a reload.
    /// </summary>
    private void LaneModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not RadioMenuFlyoutItem item)
            return;

        if (Enum.TryParse<LaneMode>(item.Tag?.ToString(), out var mode) &&
            _viewModel.LaneMode != mode)
        {
            _viewModel.LaneMode = mode;
        }
    }

    /// <summary>Collapses/expands the lane whose gutter chevron was clicked.</summary>
    private void LaneCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LaneGutterRowDto row })
        {
            _viewModel?.ToggleLaneCollapse(row.LaneKey);
        }
    }

    /// <summary>
    /// Syncs the Lanes dropdown check state with the (singleton) ViewModel's
    /// current mode - the control is recreated on every navigation.
    /// </summary>
    private void SyncLaneModeRadios()
    {
        if (_viewModel == null)
            return;

        var current = _viewModel.LaneMode.ToString();
        foreach (var item in new[]
                 {
                     LaneModeAutoItem, LaneModeCategoryItem, LaneModePersonItem,
                     LaneModeLocationItem, LaneModeEraItem, LaneModeTagItem
                 })
        {
            item.IsChecked = string.Equals(item.Tag?.ToString(), current, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    private void ZoomLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || e.AddedItems.Count == 0)
            return;

        var selectedItem = e.AddedItems[0] as ComboBoxItem;
        var tag = selectedItem?.Tag?.ToString();

        if (tag != null)
        {
            _viewModel.SetZoomLevelCommand.Execute(tag);
        }
    }

    private void EventBubble_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TimelineEventDto eventDto)
        {
            _viewModel?.SelectEventCommand.Execute(eventDto);
        }
    }

    #region Touch Gesture Handlers

    /// <summary>
    /// Handles pinch-to-zoom and touch panning gestures.
    /// </summary>
    private async void TimelineCanvas_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_viewModel == null || _isManipulating)
            return;

        _isManipulating = true;

        try
        {
            // Handle pinch-to-zoom
            if (e.Delta.Scale != 1.0)
            {
                var scaleDelta = e.Delta.Scale;
                var cumulativeScale = e.Cumulative.Scale;

                // Determine zoom direction
                if (scaleDelta > 1.0 && _viewModel.CanZoomIn)
                {
                    // Zoom in - scale > 1.0 means pinch out
                    await _viewModel.ZoomInAsync();
                    _lastScale = cumulativeScale;
                }
                else if (scaleDelta < 1.0 && _viewModel.CanZoomOut)
                {
                    // Zoom out - scale < 1.0 means pinch in
                    await _viewModel.ZoomOutAsync();
                    _lastScale = cumulativeScale;
                }
            }

            // Handle touch panning
            if (e.Delta.Translation.X != 0 || e.Delta.Translation.Y != 0)
            {
                // Pan the timeline based on touch movement
                var panDelta = e.Delta.Translation.X;
                if (Math.Abs(panDelta) > 2)
                {
                    await _viewModel.PanAsync(-panDelta);
                }

                _lastManipulationPosition = e.Position;
            }
        }
        finally
        {
            _isManipulating = false;
        }
    }

    /// <summary>
    /// Handles momentum/inertia at the end of a manipulation gesture.
    /// </summary>
    private async void TimelineCanvas_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (_viewModel == null)
            return;

        // Apply inertia/momentum for smooth deceleration
        var velocityX = e.Velocities.Linear.X;

        if (Math.Abs(velocityX) > 0.5)
        {
            // Calculate momentum pan distance based on velocity
            var momentumDistance = velocityX * 50; // Scale factor for momentum

            // Apply momentum pan
            await _viewModel.PanAsync(-momentumDistance);
        }

        // Snap back to boundaries if panned beyond limits
        _viewModel.Viewport?.SnapToBoundaries();

        _lastScale = 1.0;
        _lastManipulationPosition = default;
    }

    /// <summary>
    /// Handles double-tap to zoom in on a specific location.
    /// </summary>
    private async void TimelineCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.CanZoomIn)
            return;

        // Get the position where user double-tapped
        var position = e.GetPosition(TimelineCanvas);

        // Calculate the date at the tapped position using viewport's actual PixelsPerDay
        if (_viewModel.Viewport != null)
        {
            var viewport = _viewModel.Viewport;
            var dateAtPosition = viewport.PixelToDate(position.X);

            // Zoom in centered on the tapped date
            await _viewModel.ZoomInAsync();

            // Pan to center the tapped location (optional - provides better UX)
            var centerOffsetX = position.X - (ActualWidth / 2);
            if (Math.Abs(centerOffsetX) > 50)
            {
                await _viewModel.PanAsync(-centerOffsetX);
            }
        }
    }

    #endregion

    #region Right-Click Context Menu Handlers

    private void TimelineCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Store the position for "Add Event Here" functionality
        _lastRightTapPosition = e.GetPosition(TimelineCanvas);
    }

    private void EventBubble_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TimelineEventDto eventDto)
        {
            _contextMenuEvent = eventDto;
        }
    }

    #endregion

    #region Pointer/Cursor Handlers

    /// <summary>
    /// Height of the time ruler zone (in pixels from top).
    /// Wheel zoom over this area triggers cursor-anchored zoom.
    /// </summary>
    private const double TimeRulerZoneHeight = 70.0;

    /// <summary>
    /// Handles mouse wheel for zoom and scroll (Premiere-style).
    /// When cursor is over time ruler: cursor-anchored zoom
    /// When cursor is over track area with Ctrl: center-anchored zoom
    /// When cursor is over track area without Ctrl: horizontal scroll
    /// </summary>
    private async void TimelineCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel?.Viewport == null)
            return;

        var point = e.GetCurrentPoint(TimelineCanvas);
        var position = point.Position;
        var wheelDelta = point.Properties.MouseWheelDelta;

        // Check if cursor is in the time ruler zone (bottom area - Row 2)
        var canvasHeight = TimelineCanvas.ActualHeight;
        bool isInTimeRulerZone = position.Y >= canvasHeight - 80; // Time ruler is 80px at bottom

        // Check for Ctrl key (zoom modifier for track area)
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool isCtrlPressed = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (isInTimeRulerZone)
        {
            // Cursor-anchored zoom: timecode under cursor stays fixed
            await _viewModel.CursorAnchoredZoomAsync(position.X, wheelDelta);
            e.Handled = true;
        }
        else if (isCtrlPressed)
        {
            // Center-anchored zoom with Ctrl held
            await _viewModel.CenterAnchoredZoomAsync(wheelDelta);
            e.Handled = true;
        }
        else
        {
            // Default: horizontal scroll via panning
            // Positive wheelDelta = scroll up = pan left (earlier dates)
            // Negative wheelDelta = scroll down = pan right (later dates)
            var scrollAmount = wheelDelta * 0.5; // Scale factor for comfortable scrolling
            await _viewModel.PanAsync(scrollAmount);
            e.Handled = true;
        }
    }

    private void TimelineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel?.Viewport == null)
            return;

        // Get current pointer position
        var point = e.GetCurrentPoint(TimelineCanvas);
        var position = point.Position;

        // Calculate date at pointer position using viewport's actual PixelsPerDay
        var dateAtPointer = _viewModel.Viewport.PixelToDate(position.X);

        // The hover label is day-resolution, so skip re-formatting/updating the TextBlock
        // when the pointer moves within the same day (avoids per-move string allocation).
        if (dateAtPointer.Date == _lastHoverDate)
        {
            if (HoverDateText.Visibility != Visibility.Visible)
                HoverDateText.Visibility = Visibility.Visible;
            return;
        }

        _lastHoverDate = dateAtPointer.Date;
        HoverDateText.Text = dateAtPointer.ToString("MMMM d, yyyy");
        HoverDateText.Visibility = Visibility.Visible;
    }

    private void EventBubble_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Change cursor to hand when hovering over events
        if (sender is FrameworkElement element)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        }
    }

    private void EventBubble_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Reset cursor when leaving events
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    #endregion

    #region Date Navigation Helpers

    /// <summary>
    /// Parses a date string in mmddyy, mmddyyyy, or yyyy format.
    /// Returns null if parsing fails.
    /// Note: No artificial date restrictions - any valid DateTime year (1-9999) is accepted.
    /// </summary>
    private (DateTime? date, bool isYearOnly) ParseDateString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (null, false);

        input = input.Trim();

        // Check for 4-digit year only (e.g., "2006", "1850")
        if (input.Length == 4 && int.TryParse(input, out int year))
        {
            // Accept any valid DateTime year (1-9999)
            if (year >= 1 && year <= 9999)
            {
                return (new DateTime(year, 1, 1), true);
            }
        }

        // Check for 6-digit mmddyy format (e.g., "010190")
        if (input.Length == 6 && int.TryParse(input, out _))
        {
            if (int.TryParse(input.Substring(0, 2), out int month) &&
                int.TryParse(input.Substring(2, 2), out int day) &&
                int.TryParse(input.Substring(4, 2), out int yy))
            {
                // Convert 2-digit year to 4-digit
                // 00-29 = 2000-2029, 30-99 = 1930-1999
                int fullYear = yy <= 29 ? 2000 + yy : 1900 + yy;

                try
                {
                    return (new DateTime(fullYear, month, day), false);
                }
                catch
                {
                    return (null, false);
                }
            }
        }

        // Check for 8-digit mmddyyyy format (e.g., "01011990")
        if (input.Length == 8 && int.TryParse(input, out _))
        {
            if (int.TryParse(input.Substring(0, 2), out int month) &&
                int.TryParse(input.Substring(2, 2), out int day) &&
                int.TryParse(input.Substring(4, 4), out int fullYear))
            {
                try
                {
                    return (new DateTime(fullYear, month, day), false);
                }
                catch
                {
                    return (null, false);
                }
            }
        }

        return (null, false);
    }

    private async void QuickDateBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _viewModel == null)
            return;

        var (date, isYearOnly) = ParseDateString(QuickDateBox.Text);
        if (date.HasValue)
        {
            if (isYearOnly)
            {
                // Show 3-year span: year-1 (left), year (center), year+1 (right)
                await _viewModel.SetViewportToYearSpanAsync(date.Value.Year);
            }
            else
            {
                await _viewModel.GoToDateCommand.ExecuteAsync(date.Value);
            }
            QuickDateBox.Text = "";
        }
    }

    private async void GoToDateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        GoToDateTextBox.Text = "";
        GoToDateDialog.XamlRoot = XamlRoot;
        await GoToDateDialog.ShowAsync();
    }

    private async void GoToDateDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_viewModel == null)
        {
            args.Cancel = true;
            return;
        }

        var (date, isYearOnly) = ParseDateString(GoToDateTextBox.Text);
        if (!date.HasValue)
        {
            args.Cancel = true;
            return;
        }

        if (isYearOnly)
        {
            // Show 3-year span: year-1 (left), year (center), year+1 (right)
            await _viewModel.SetViewportToYearSpanAsync(date.Value.Year);
        }
        else
        {
            await _viewModel.GoToDateCommand.ExecuteAsync(date.Value);
        }
    }

    private void GoToDateTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Allow Enter to trigger the primary button
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // The dialog will handle the primary button click
        }
    }

    #endregion

    #region Event CRUD Handlers

    private bool _isSyncingDateFields;

    private void EventDateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingDateFields)
            return;

        var (date, _) = ParseDateString(EventDateTextBox.Text);
        if (date.HasValue)
        {
            _isSyncingDateFields = true;
            EventDatePicker.Date = new DateTimeOffset(date.Value);
            _isSyncingDateFields = false;

            // The guarded picker update above suppresses DateChanged, so keep
            // the precision preview current here.
            UpdatePrecisionPreview();
        }
    }

    private void EventDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_isSyncingDateFields || !args.NewDate.HasValue)
            return;

        _isSyncingDateFields = true;
        EventDateTextBox.Text = args.NewDate.Value.ToString("MMddyy");
        _isSyncingDateFields = false;

        UpdatePrecisionPreview();
    }

    /// <summary>
    /// Returns the precision selected in the event dialog's precision combo,
    /// defaulting to Day when nothing is selected.
    /// </summary>
    private DatePrecision GetSelectedPrecision()
    {
        if (EventPrecisionCombo.SelectedItem is ComboBoxItem item)
        {
            return DatePrecisionParser.Parse(item.Tag?.ToString());
        }

        return DatePrecision.Day;
    }

    /// <summary>
    /// Selects the precision combo item matching <paramref name="precision"/>
    /// (falls back to Day for safety).
    /// </summary>
    private void SelectPrecisionComboItem(DatePrecision precision)
    {
        var value = DatePrecisionParser.ToStringValue(precision);
        int fallbackIndex = 1; // "Day"

        for (int i = 0; i < EventPrecisionCombo.Items.Count; i++)
        {
            if (EventPrecisionCombo.Items[i] is not ComboBoxItem item)
                continue;

            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                EventPrecisionCombo.SelectedIndex = i;
                return;
            }
        }

        EventPrecisionCombo.SelectedIndex = fallbackIndex;
    }

    private void EventPrecisionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePrecisionPreview();
    }

    /// <summary>
    /// Shows the precision-honest human form of the chosen date ("Summer 1998")
    /// while the precision is coarser than Day; hidden otherwise. The MMDDYY
    /// fast-entry box is disabled at coarse precisions — the calendar picker
    /// stays enabled because it is how the anchor month/season/year is chosen.
    /// </summary>
    private void UpdatePrecisionPreview()
    {
        var precision = GetSelectedPrecision();
        var isDayGranular = precision <= DatePrecision.Day;

        EventDateTextBox.IsEnabled = isDayGranular;

        if (isDayGranular || !EventDatePicker.Date.HasValue)
        {
            EventPrecisionPreview.Visibility = Visibility.Collapsed;
            return;
        }

        EventPrecisionPreview.Text = DateDisplay.FormatPrecise(EventDatePicker.Date.Value.DateTime, precision);
        EventPrecisionPreview.Visibility = Visibility.Visible;
    }

    private async void AddEventButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddEventDialogAsync(DateTime.Now);
    }

    private async void AddEventMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.Viewport == null)
            return;

        // Calculate the date at the right-click position using viewport's actual PixelsPerDay
        var dateAtPosition = _viewModel.Viewport.PixelToDate(_lastRightTapPosition.X);

        await ShowAddEventDialogAsync(dateAtPosition);
    }

    /// <summary>
    /// Opens the blank create-event dialog dated today. Entry point for the
    /// Jump List "New Event" quick action (TimelinePage navigation parameter
    /// "new-event"); the toolbar button uses the same dialog.
    /// </summary>
    public Task ShowNewEventDialogAsync()
    {
        return ShowAddEventDialogAsync(DateTime.Now);
    }

    private async Task ShowAddEventDialogAsync(DateTime defaultDate)
    {
        _editingEventId = null;
        _dialogSessionAttachedMedia.Clear();
        EventDialog.Title = "Add Event";

        // Reset dialog state (chips, era choices, draft id) in the ViewModel.
        if (_viewModel != null)
        {
            await _viewModel.PrepareEventDialogAsync(null);
        }

        // Clear and set defaults
        EventTitleBox.Text = "";
        _isSyncingDateFields = true;
        EventDateTextBox.Text = defaultDate.ToString("MMddyy");
        EventDatePicker.Date = new DateTimeOffset(defaultDate);
        _isSyncingDateFields = false;
        SelectPrecisionComboItem(DatePrecision.Day);
        UpdatePrecisionPreview();
        EventEndDatePicker.Date = null;
        EventDescriptionBox.Text = "";
        // Pre-select the canonical default category instead of leaving the field
        // unselected (an unselected combo used to silently fall back to an invalid
        // capitalized literal and fail validation - see FEATURE-AUDIT Bug 1).
        SelectCategoryComboItem(EventCategory.Other);
        SelectEraComboItem(null);
        EventLocationBox.Text = "";
        ResetChipEditors();
        EventDialogErrorBar.IsOpen = false;

        EventDialog.XamlRoot = XamlRoot;
        await EventDialog.ShowAsync();
    }

    /// <summary>
    /// Opens the create-event dialog prefilled from a saved event draft
    /// (navigation parameter "draft:&lt;draftId&gt;"). The draft's id is kept so
    /// "Save as Draft" updates it and a successful real save deletes it.
    /// </summary>
    public async Task ShowEventDialogFromDraftAsync(string draftId)
    {
        if (_viewModel == null)
            return;

        _editingEventId = null;
        _dialogSessionAttachedMedia.Clear();
        EventDialog.Title = "Add Event";

        await _viewModel.PrepareEventDialogAsync(null);
        var payload = await _viewModel.LoadEventDraftAsync(draftId);

        var startDate = payload?.StartDate ?? DateTime.Now;
        EventTitleBox.Text = payload?.Title ?? "";
        _isSyncingDateFields = true;
        EventDateTextBox.Text = startDate.ToString("MMddyy");
        EventDatePicker.Date = new DateTimeOffset(startDate);
        _isSyncingDateFields = false;
        SelectPrecisionComboItem(payload?.DatePrecision ?? DatePrecision.Day);
        UpdatePrecisionPreview();
        EventEndDatePicker.Date = payload?.EndDate is DateTime draftEnd
            ? new DateTimeOffset(draftEnd)
            : null;
        EventDescriptionBox.Text = payload?.Description ?? "";
        SelectCategoryComboItem(payload?.Category ?? EventCategory.Other);
        SelectEraComboItem(payload?.EraId);
        EventLocationBox.Text = payload?.Location ?? "";
        ResetChipEditors();
        EventDialogErrorBar.IsOpen = false;

        if (payload == null)
        {
            ShowEventDialogError("The draft could not be loaded. Starting a blank event instead.");
        }

        EventDialog.XamlRoot = XamlRoot;
        await EventDialog.ShowAsync();
    }

    private void ViewEventMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuEvent != null)
        {
            _viewModel?.SelectEventCommand.Execute(_contextMenuEvent);
        }
    }

    private async void EditEventMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuEvent != null)
        {
            await ShowEditEventDialogAsync(_contextMenuEvent);
        }
    }

    private async void EditSelectedEvent_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedEvent != null)
        {
            await ShowEditEventDialogAsync(_viewModel.SelectedEvent);
        }
    }

    private async Task ShowEditEventDialogAsync(TimelineEventDto eventDto)
    {
        _editingEventId = eventDto.EventId;
        _dialogSessionAttachedMedia.Clear();
        EventDialog.Title = "Edit Event";

        // Reset dialog state and load the event's current people/tags into
        // chips (also captures the original link sets for the save-time diff).
        if (_viewModel != null)
        {
            await _viewModel.PrepareEventDialogAsync(eventDto.EventId);
        }

        // Populate with existing values
        EventTitleBox.Text = eventDto.Title ?? "";
        _isSyncingDateFields = true;
        EventDateTextBox.Text = eventDto.StartDate.ToString("MMddyy");
        EventDatePicker.Date = new DateTimeOffset(eventDto.StartDate);
        _isSyncingDateFields = false;
        SelectPrecisionComboItem(eventDto.DatePrecision);
        UpdatePrecisionPreview();
        EventEndDatePicker.Date = eventDto.EndDate is DateTime endDate
            ? new DateTimeOffset(endDate)
            : null;
        EventDescriptionBox.Text = eventDto.Description ?? "";
        EventLocationBox.Text = eventDto.Location ?? "";

        // Select the category (case-insensitive; falls back to "other" when the
        // stored category isn't in the list, so the Edit path can never save an
        // invalid category or silently keep a stale selection).
        SelectCategoryComboItem(eventDto.Category ?? EventCategory.Other);
        SelectEraComboItem(eventDto.EraId);
        ResetChipEditors();
        EventDialogErrorBar.IsOpen = false;

        EventDialog.XamlRoot = XamlRoot;
        await EventDialog.ShowAsync();

        // Photos attached while the Edit dialog was open could not show the
        // EXIF-date offer then (one ContentDialog at a time); offer now, once,
        // re-evaluated against the event's current (possibly just-saved) date.
        if (_dialogSessionAttachedMedia.Count > 0)
        {
            var attachedDuringDialog = _dialogSessionAttachedMedia.ToList();
            _dialogSessionAttachedMedia.Clear();
            await OfferExifDateAdjustAsync(eventDto.EventId, attachedDuringDialog);
        }
    }

    /// <summary>
    /// Selects the era combo item matching <paramref name="eraId"/>; null (or an
    /// unknown id) selects the "None" option.
    /// </summary>
    private void SelectEraComboItem(string? eraId)
    {
        var choices = _viewModel?.DialogEraChoices;
        if (choices == null || choices.Count == 0)
        {
            EventEraCombo.SelectedIndex = -1;
            return;
        }

        var match = choices.FirstOrDefault(c => string.Equals(c.EraId, eraId, StringComparison.Ordinal))
            ?? choices[0];
        EventEraCombo.SelectedItem = match;
    }

    /// <summary>
    /// Clears the people/tag AutoSuggestBoxes (chip collections themselves live
    /// in the ViewModel and are reset by PrepareEventDialogAsync).
    /// </summary>
    private void ResetChipEditors()
    {
        PersonSuggestBox.Text = "";
        PersonSuggestBox.ItemsSource = null;
        TagSuggestBox.Text = "";
        TagSuggestBox.ItemsSource = null;
    }

    /// <summary>
    /// Selects the combo item whose Tag matches <paramref name="category"/>
    /// (case-insensitive). Unknown categories fall back to <see cref="EventCategory.Other"/>.
    /// </summary>
    private void SelectCategoryComboItem(string category)
    {
        int fallbackIndex = -1;

        for (int i = 0; i < EventCategoryCombo.Items.Count; i++)
        {
            if (EventCategoryCombo.Items[i] is not ComboBoxItem item)
                continue;

            var tag = item.Tag?.ToString();
            if (string.Equals(tag, category, StringComparison.OrdinalIgnoreCase))
            {
                EventCategoryCombo.SelectedIndex = i;
                return;
            }

            if (string.Equals(tag, EventCategory.Other, StringComparison.OrdinalIgnoreCase))
            {
                fallbackIndex = i;
            }
        }

        EventCategoryCombo.SelectedIndex = fallbackIndex;
    }

    /// <summary>Opens the revision-history dialog for the right-clicked event (F12).</summary>
    private async void EventHistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuEvent == null) return;
        var historyDialog = new EventHistoryDialog(_contextMenuEvent.EventId, _contextMenuEvent.Title);
        historyDialog.XamlRoot = XamlRoot;
        await historyDialog.ShowAsync();
    }

    private async void DeleteEventMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuEvent != null)
        {
            await ConfirmAndDeleteEventAsync(_contextMenuEvent);
        }
    }

    private async void DeleteSelectedEvent_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedEvent != null)
        {
            await ConfirmAndDeleteEventAsync(_viewModel.SelectedEvent);
        }
    }

    private async Task ConfirmAndDeleteEventAsync(TimelineEventDto eventDto)
    {
        DeleteConfirmDialog.XamlRoot = XamlRoot;
        var result = await DeleteConfirmDialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            await _viewModel!.DeleteEventAsync(eventDto.EventId);
        }
    }

    private async void EventDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Take a deferral so the dialog stays open while we await the save and
        // can be kept open (args.Cancel) with a visible error if the save fails.
        var deferral = args.GetDeferral();
        try
        {
            // Validate input - keep the dialog open and show a summary of every
            // problem instead of silently cancelling the close.
            var validationErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(EventTitleBox.Text))
            {
                validationErrors.Add("Enter a title for the event.");
            }

            if (!EventDatePicker.Date.HasValue)
            {
                validationErrors.Add("Select a start date for the event.");
            }

            var endDate = EventEndDatePicker.Date?.DateTime;
            if (endDate.HasValue && EventDatePicker.Date.HasValue &&
                endDate.Value.Date < EventDatePicker.Date.Value.DateTime.Date)
            {
                validationErrors.Add("The end date cannot be before the start date.");
            }

            if (validationErrors.Count > 0)
            {
                args.Cancel = true;
                ShowEventDialogError(string.Join(Environment.NewLine, validationErrors));
                return;
            }

            if (_viewModel == null)
            {
                args.Cancel = true;
                ShowEventDialogError("The timeline is still loading. Please try again.");
                return;
            }

            // Categories are canonically lowercase (EventCategory.AllCategories).
            // The dialog pre-selects "Other", and the service normalizes casing
            // too - this fallback is defense in depth, never "Other" (capital O),
            // which used to fail validation (FEATURE-AUDIT Bug 1).
            var category = EventCategory.Other;
            if (EventCategoryCombo.SelectedItem is ComboBoxItem selectedCategory)
            {
                category = selectedCategory.Tag?.ToString()?.ToLowerInvariant()
                    ?? EventCategory.Other;
            }

            var eraId = (EventEraCombo.SelectedItem as EventEraChoiceDto)?.EraId;
            var datePrecision = GetSelectedPrecision();

            EventDialogErrorBar.IsOpen = false;

            if (_editingEventId == null)
            {
                // Create new event (the ViewModel links people/tags afterwards)
                await _viewModel.CreateEventAsync(
                    EventTitleBox.Text,
                    EventDatePicker.Date!.Value.DateTime,
                    endDate,
                    EventDescriptionBox.Text,
                    category,
                    EventLocationBox.Text,
                    eraId,
                    datePrecision);
            }
            else
            {
                // Update existing event (the ViewModel reconciles people/tags)
                await _viewModel.UpdateEventAsync(
                    _editingEventId,
                    EventTitleBox.Text,
                    EventDatePicker.Date!.Value.DateTime,
                    endDate,
                    EventDescriptionBox.Text,
                    category,
                    EventLocationBox.Text,
                    eraId,
                    datePrecision);
            }
        }
        catch (Exception ex)
        {
            // The ViewModel logs and rethrows; keep the dialog open and show
            // the reason in-dialog instead of closing as if the save succeeded.
            args.Cancel = true;
            ShowEventDialogError(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// "Save as Draft": serializes the dialog state (including people/tag
    /// chips) through the ViewModel and closes the dialog on success. Failures
    /// keep the dialog open with the reason shown in-dialog.
    /// </summary>
    private async void EventDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (_viewModel == null)
            {
                args.Cancel = true;
                ShowEventDialogError("The timeline is still loading. Please try again.");
                return;
            }

            string? category = null;
            if (EventCategoryCombo.SelectedItem is ComboBoxItem selectedCategory)
            {
                category = selectedCategory.Tag?.ToString()?.ToLowerInvariant();
            }

            var eraId = (EventEraCombo.SelectedItem as EventEraChoiceDto)?.EraId;

            await _viewModel.SaveEventDraftAsync(
                EventTitleBox.Text,
                EventDescriptionBox.Text,
                EventDatePicker.Date?.DateTime,
                EventEndDatePicker.Date?.DateTime,
                category,
                eraId,
                EventLocationBox.Text,
                GetSelectedPrecision());
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowEventDialogError($"Could not save the draft: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Shows an error message inside the Add/Edit Event dialog.
    /// </summary>
    private void ShowEventDialogError(string message)
    {
        EventDialogErrorBar.Message = message;
        EventDialogErrorBar.IsOpen = true;
    }

    /// <summary>Clears the optional end date in the event dialog.</summary>
    private void ClearEndDateButton_Click(object sender, RoutedEventArgs e)
    {
        EventEndDatePicker.Date = null;
    }

    #endregion

    #region Event Dialog People/Tag Pickers

    /// <summary>
    /// Queries person suggestions as the user types in the people picker.
    /// </summary>
    private async void PersonSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _viewModel == null)
            return;

        var term = sender.Text?.Trim() ?? "";
        if (term.Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }

        var suggestions = await _viewModel.SearchDialogPeopleAsync(term);

        // The user may have kept typing while the query ran; only apply results
        // that still match the current text.
        if (string.Equals(sender.Text?.Trim(), term, StringComparison.Ordinal))
        {
            sender.ItemsSource = suggestions;
        }
    }

    /// <summary>
    /// Keeps the box text readable while navigating suggestions (the chip is
    /// added by the QuerySubmitted handler that follows).
    /// </summary>
    private void PersonSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is PersonDto person)
        {
            sender.Text = person.Name;
        }
    }

    /// <summary>
    /// Adds a person chip: a chosen suggestion links that person; free text
    /// matching an existing contact (case-insensitive) links it, and free text
    /// matching nothing adds a pending "new" person chip created on save.
    /// </summary>
    private async void PersonSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_viewModel == null)
            return;

        if (args.ChosenSuggestion is PersonDto person)
        {
            _viewModel.AddPersonChip(person);
        }
        else
        {
            var text = args.QueryText?.Trim() ?? "";
            if (text.Length == 0)
                return;

            var matches = await _viewModel.SearchDialogPeopleAsync(text);
            var exact = matches.FirstOrDefault(p =>
                string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                _viewModel.AddPersonChip(exact);
            }
            else
            {
                _viewModel.AddNewPersonChip(text);
            }
        }

        sender.Text = "";
        sender.ItemsSource = null;
    }

    /// <summary>Removes the clicked person chip.</summary>
    private void PersonChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventPersonChipDto chip })
        {
            _viewModel?.RemovePersonChip(chip);
        }
    }

    /// <summary>
    /// Queries existing-tag suggestions as the user types in the tag editor.
    /// </summary>
    private async void TagSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _viewModel == null)
            return;

        var term = sender.Text?.Trim() ?? "";
        var suggestions = await _viewModel.SearchDialogTagsAsync(term);

        if (string.Equals(sender.Text?.Trim(), term, StringComparison.Ordinal))
        {
            sender.ItemsSource = suggestions;
        }
    }

    /// <summary>Shows the chosen tag name in the box before submission.</summary>
    private void TagSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string tagName)
        {
            sender.Text = tagName;
        }
    }

    /// <summary>Adds a tag chip (existing suggestion or free text).</summary>
    private void TagSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_viewModel == null)
            return;

        var name = args.ChosenSuggestion as string ?? args.QueryText;
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel.AddTagChip(name);
        }

        sender.Text = "";
        sender.ItemsSource = null;
    }

    /// <summary>Removes the clicked tag chip.</summary>
    private void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventTagChipDto chip })
        {
            _viewModel?.RemoveTagChip(chip);
        }
    }

    #endregion

    #region Media Attachments (picker, drag-drop, lightbox)

    // Extensions offered by the "Add photos..." pickers. Drag-drop accepts the
    // full IMediaService allowlist; the service validates every file anyway.
    private static readonly string[] PickerImageExtensions =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic"
    };

    // Lightbox state: the items being paged through and the current index.
    private List<EventMediaThumbDto> _lightboxItems = new();
    private int _lightboxIndex = -1;
    private bool _lightboxRemoveArmed;

    // Media attached immediately while the EDIT Event dialog is open. Only one
    // ContentDialog can be open at a time, so the F2 EXIF-date offer for these
    // attachments is deferred until the Edit dialog closes (see
    // ShowEditEventDialogAsync); details-panel attachments offer right away.
    private readonly List<EventMedia> _dialogSessionAttachedMedia = new();

    /// <summary>
    /// Opens a multi-select file picker for photos (unpackaged-app interop:
    /// the picker must be initialized with the window handle).
    /// </summary>
    private async Task<List<string>> PickMediaFilesAsync()
    {
        var openPicker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        foreach (var extension in PickerImageExtensions)
        {
            openPicker.FileTypeFilter.Add(extension);
        }

        var hwnd = WindowNative.GetWindowHandle(App.Current.Window);
        InitializeWithWindow.Initialize(openPicker, hwnd);

        var files = await openPicker.PickMultipleFilesAsync();
        return files?
            .Select(f => f.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList() ?? new List<string>();
    }

    /// <summary>Accepts file drags over the media drop targets.</summary>
    private void MediaDropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    /// <summary>
    /// Extracts local file paths from a drop's storage items. The deferral is
    /// completed before any (slow) attachment work starts so the drag-drop
    /// interaction finishes promptly.
    /// </summary>
    private static async Task<List<string>> GetDroppedFilePathsAsync(DragEventArgs e)
    {
        var paths = new List<string>();
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return paths;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths.AddRange(items
                .OfType<StorageFile>()
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p)));
        }
        finally
        {
            deferral.Complete();
        }

        return paths;
    }

    /// <summary>"Add photos..." on the details-panel strip.</summary>
    private async void AddPhotosToSelectedEvent_Click(object sender, RoutedEventArgs e)
    {
        var target = _viewModel?.SelectedEvent;
        if (_viewModel == null || target == null)
            return;

        var paths = await PickMediaFilesAsync();
        if (paths.Count > 0)
        {
            var attached = await _viewModel.AttachMediaToSelectedEventAsync(paths);
            await OfferExifDateAdjustAsync(target.EventId, attached);
        }
    }

    /// <summary>Files dropped on the details-panel strip.</summary>
    private async void DetailsMediaStrip_Drop(object sender, DragEventArgs e)
    {
        var target = _viewModel?.SelectedEvent;
        if (_viewModel == null || target == null)
            return;

        try
        {
            var paths = await GetDroppedFilePathsAsync(e);
            if (paths.Count > 0)
            {
                var attached = await _viewModel.AttachMediaToSelectedEventAsync(paths);
                await OfferExifDateAdjustAsync(target.EventId, attached);
            }
        }
        catch (Exception ex)
        {
            // Errors surface in the timeline InfoBar.
            if (_viewModel != null)
            {
                _viewModel.ErrorMessage = $"Could not attach the dropped files: {ex.Message}";
            }
        }
    }

    /// <summary>"Add photos..." inside the Add/Edit Event dialog.</summary>
    private async void EventDialogAddPhotos_Click(object sender, RoutedEventArgs e)
    {
        var paths = await PickMediaFilesAsync();
        await AddPhotosToDialogAsync(paths);
    }

    /// <summary>Files dropped on the Add/Edit Event dialog's photo section.</summary>
    private async void EventDialogPhotos_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var paths = await GetDroppedFilePathsAsync(e);
            await AddPhotosToDialogAsync(paths);
        }
        catch (Exception ex)
        {
            ShowEventDialogError($"Could not attach the dropped files: {ex.Message}");
        }
    }

    /// <summary>
    /// Routes dialog photos: a NEW event stages them (attached after save
    /// creates the event id); an EXISTING event attaches immediately.
    /// Failures show in the dialog's own error bar.
    /// </summary>
    private async Task AddPhotosToDialogAsync(List<string> paths)
    {
        if (_viewModel == null || paths.Count == 0)
            return;

        if (_editingEventId == null)
        {
            _viewModel.AddDialogPendingPhotos(paths);
            return;
        }

        try
        {
            var attached = await _viewModel.AttachMediaAsync(_editingEventId, paths);
            _viewModel.DialogPendingPhotosLabel = attached.Count == 1
                ? "1 photo attached"
                : $"{attached.Count} photos attached";

            // The EXIF-date offer cannot show now (the Edit dialog is a
            // ContentDialog and only one can be open); collect the batch and
            // offer once after the dialog closes.
            _dialogSessionAttachedMedia.AddRange(attached);
        }
        catch (Exception ex)
        {
            ShowEventDialogError($"Could not attach photos: {ex.Message}");
        }
    }

    /// <summary>
    /// F2 EXIF-date reconciliation, offer half: when the just-attached batch
    /// contains a photo whose EXIF capture date falls outside the event's
    /// precision window (the ViewModel decides — media without CapturedAt
    /// never qualify), offers to move the event to the capture day. At most
    /// ONE offer per batch, keyed on the earliest capture date. "Update"
    /// persists through the ViewModel's event-update path (which publishes
    /// EventUpdatedMessage); "Keep" leaves the event untouched.
    /// </summary>
    private async Task OfferExifDateAdjustAsync(string eventId, IReadOnlyList<EventMedia> newlyAttached)
    {
        if (_viewModel == null || newlyAttached.Count == 0)
            return;

        var capturedAt = await _viewModel.GetExifDateOfferAsync(eventId, newlyAttached);
        if (capturedAt == null)
            return;

        var exifDateText = capturedAt.Value.ToString(
            "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

        var offerDialog = new ContentDialog
        {
            Title = "Update event date?",
            Content = $"Photo taken {exifDateText} — update event date to match?",
            PrimaryButtonText = "Update",
            CloseButtonText = "Keep",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await offerDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _viewModel.ApplyExifEventDateAsync(eventId, capturedAt.Value);
        }
    }

    /// <summary>Opens the lightbox at the clicked thumbnail.</summary>
    private async void MediaThumb_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;

        if (sender is FrameworkElement { DataContext: EventMediaThumbDto media })
        {
            var items = _viewModel.SelectedEventMedia.ToList();
            var index = items.FindIndex(m => m.MediaId == media.MediaId);
            if (index < 0)
                return;

            _lightboxItems = items;
            _lightboxIndex = index;
            ApplyLightboxItem();

            MediaLightboxDialog.XamlRoot = XamlRoot;
            await MediaLightboxDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Shows the current lightbox item (image or file placeholder, counter,
    /// nav enablement, caption) and resets the two-step Remove confirmation.
    /// </summary>
    private void ApplyLightboxItem()
    {
        _lightboxRemoveArmed = false;
        MediaLightboxDialog.SecondaryButtonText = "Remove";
        LightboxStatusText.Text = "";

        if (_lightboxIndex < 0 || _lightboxIndex >= _lightboxItems.Count)
            return;

        var item = _lightboxItems[_lightboxIndex];
        MediaLightboxDialog.Title = item.FileName;

        if (item.IsImage)
        {
            LightboxImage.Source = item.CreateFullImage();
            LightboxImage.Visibility = Visibility.Visible;
            LightboxFilePlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            LightboxImage.Source = null;
            LightboxImage.Visibility = Visibility.Collapsed;
            LightboxFilePlaceholder.Visibility = Visibility.Visible;
            LightboxFileGlyph.Glyph = item.FileGlyph;
            LightboxFileName.Text = item.FileName;
        }

        LightboxCounter.Text = $"{_lightboxIndex + 1} of {_lightboxItems.Count}";
        LightboxPrevButton.IsEnabled = _lightboxIndex > 0;
        LightboxNextButton.IsEnabled = _lightboxIndex < _lightboxItems.Count - 1;
        LightboxCaptionBox.Text = item.Caption ?? "";
    }

    private void LightboxPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_lightboxIndex > 0)
        {
            _lightboxIndex--;
            ApplyLightboxItem();
        }
    }

    private void LightboxNext_Click(object sender, RoutedEventArgs e)
    {
        if (_lightboxIndex < _lightboxItems.Count - 1)
        {
            _lightboxIndex++;
            ApplyLightboxItem();
        }
    }

    /// <summary>
    /// "Save caption": persists the caption and keeps the lightbox open,
    /// showing the outcome (or the failure reason) in the status line.
    /// </summary>
    private async void MediaLightbox_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // saving the caption never closes the lightbox
        var deferral = args.GetDeferral();
        try
        {
            if (_viewModel == null || _lightboxIndex < 0 || _lightboxIndex >= _lightboxItems.Count)
                return;

            var item = _lightboxItems[_lightboxIndex];
            await _viewModel.UpdateMediaCaptionAsync(item, LightboxCaptionBox.Text);
            LightboxStatusText.Text = "Caption saved.";
        }
        catch (Exception ex)
        {
            LightboxStatusText.Text = $"Could not save the caption: {ex.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// "Remove" with confirmation: the first click arms (button becomes
    /// "Confirm remove"), the second click deletes the attachment and its
    /// managed file. The lightbox advances to a neighbor, or closes when the
    /// last item was removed. Failures keep it open with the reason shown.
    /// </summary>
    private async void MediaLightbox_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_lightboxRemoveArmed)
        {
            args.Cancel = true;
            _lightboxRemoveArmed = true;
            MediaLightboxDialog.SecondaryButtonText = "Confirm remove";
            LightboxStatusText.Text = "This permanently deletes the copied file. Click 'Confirm remove' to proceed.";
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            if (_viewModel == null || _lightboxIndex < 0 || _lightboxIndex >= _lightboxItems.Count)
                return;

            var item = _lightboxItems[_lightboxIndex];
            await _viewModel.RemoveMediaAsync(item);

            _lightboxItems.RemoveAt(_lightboxIndex);
            if (_lightboxItems.Count > 0)
            {
                args.Cancel = true; // keep browsing the remaining items
                if (_lightboxIndex >= _lightboxItems.Count)
                {
                    _lightboxIndex = _lightboxItems.Count - 1;
                }

                ApplyLightboxItem();
            }
            // else: nothing left to show - let the dialog close.
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            _lightboxRemoveArmed = false;
            MediaLightboxDialog.SecondaryButtonText = "Remove";
            LightboxStatusText.Text = $"Could not remove the photo: {ex.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }

    #endregion

    #region Error Surface / Navigation Handlers

    /// <summary>
    /// Clears the ViewModel error when the user dismisses the timeline error bar,
    /// so the OneWay IsOpen binding doesn't immediately reopen it and a repeat of
    /// the same error message still re-raises the bar.
    /// </summary>
    private void TimelineErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_viewModel != null)
        {
            _viewModel.ErrorMessage = null;
        }
    }

    /// <summary>
    /// Navigates to the Connections page for the currently selected event.
    /// </summary>
    private void ViewConnections_Click(object sender, RoutedEventArgs e)
    {
        var selectedEvent = _viewModel?.SelectedEvent;
        if (selectedEvent == null || string.IsNullOrEmpty(selectedEvent.EventId))
            return;

        var navigationService = App.Current.Services.GetRequiredService<INavigationService>();
        navigationService.NavigateTo("Connections", selectedEvent.EventId);
    }

    #endregion

    #region Era Filter Handlers

    private void ShowAllEras_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ShowAllEras();
    }

    private void HideAllEras_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.HideAllEras();
    }

    #endregion

    #region Narrative Generation

    /// <summary>
    /// Opens the shared narrative-generation dialog scoped to the CURRENT
    /// viewport date range ("tell the story of what I'm looking at"). The
    /// dialog owns its own ViewModel, progress, and cancel-on-close state.
    /// </summary>
    private async void GenerateStoryButton_Click(object sender, RoutedEventArgs e)
    {
        var viewport = _viewModel?.Viewport;
        if (viewport == null)
        {
            return;
        }

        var request = new Core.Services.NarrativeRequest
        {
            Scope = Core.Services.NarrativeScope.DateRange,
            From = viewport.StartDate,
            To = viewport.EndDate
        };

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var label = $"{viewport.StartDate.ToString("d MMM yyyy", culture)} – {viewport.EndDate.ToString("d MMM yyyy", culture)}";

        var dialog = new NarrativeDialog(request, label)
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    #endregion
}
