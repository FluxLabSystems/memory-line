using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Services;
using MemoryTimeline.Views;
using MemoryTimeline.ViewModels;

namespace MemoryTimeline;

/// <summary>
/// Main window for the Memory Timeline application.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;

    // True while the NavigationView selection is being synced to a navigation
    // that already happened (programmatic NavigateTo, back/forward, deep link).
    // SelectionChanged must not navigate again in that case: it would create a
    // second page instance, drop the original navigation parameter's effect,
    // and push a duplicate back-stack entry.
    private bool _isSyncingSelection;

    /// <summary>
    /// Shell state (navigation badge counts) bound by MainWindow.xaml via x:Bind.
    /// </summary>
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel, INavigationService navigationService)
    {
        ViewModel = viewModel;
        _navigationService = navigationService;
        InitializeComponent();

        // Use standard title bar to avoid overlap with navigation
        // (ExtendsContentIntoTitleBar causes the drag region to block navigation clicks)

        // Subscribe BEFORE attaching the frame so the very first navigation
        // already syncs the NavigationView selection and back-button state.
        _navigationService.Navigated += OnNavigationServiceNavigated;
        _navigationService.Frame = ContentFrame;
        RegisterPages();

        Closed += OnWindowClosed;

        // Land on Home immediately (no blank frame while settings load), then
        // asynchronously honor home_is_default_page: when the user has turned
        // the Home landing off, flip to Timeline - but only if nothing else
        // (deep link, activation action) has navigated away in the meantime.
        // The Navigated handler selects the matching NavigationView item.
        _navigationService.NavigateTo("Home");
        _ = ApplyDefaultLandingPreferenceAsync();

        // Shell badge counts (Queue / Review). Fire-and-forget from the
        // constructor like the landing preference: the ViewModel logs failures
        // and leaves the badges hidden.
        _ = ViewModel.InitializeAsync();
    }

    /// <summary>
    /// Reads home_is_default_page (seeded "true") and, when it is explicitly
    /// "false", moves the initial navigation from Home to Timeline. Runs
    /// fire-and-forget from the constructor: awaits resume on the UI thread
    /// (the constructor runs under the DispatcherQueueSynchronizationContext),
    /// so touching ContentFrame afterwards is safe. Failures leave the app on
    /// Home - a fully functional fallback - and are logged.
    /// </summary>
    private async Task ApplyDefaultLandingPreferenceAsync()
    {
        try
        {
            var settingsService = App.Current.Services.GetRequiredService<ISettingsService>();
            var homeIsDefault = await settingsService.GetSettingAsync<string>(SettingKeys.HomeIsDefaultPage, "true");

            if (string.Equals(homeIsDefault?.Trim(), "false", StringComparison.OrdinalIgnoreCase) &&
                ContentFrame.CurrentSourcePageType == typeof(HomePage))
            {
                _navigationService.NavigateTo("Timeline");
            }
        }
        catch (Exception ex)
        {
            var logger = App.Current.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogWarning(ex, "Could not read the default landing page setting; staying on Home");
        }
    }

    private void RegisterPages()
    {
        _navigationService.RegisterPage("Home", typeof(HomePage));
        _navigationService.RegisterPage("Timeline", typeof(TimelinePage));
        _navigationService.RegisterPage("Ask", typeof(AskPage));
        _navigationService.RegisterPage("Queue", typeof(QueuePage));
        _navigationService.RegisterPage("Review", typeof(ReviewPage));
        _navigationService.RegisterPage("People", typeof(ContactsPage));
        _navigationService.RegisterPage("Connections", typeof(ConnectionsPage));
        _navigationService.RegisterPage("Eras", typeof(ErasPage));
        _navigationService.RegisterPage("Map", typeof(MapPage));
        _navigationService.RegisterPage("Search", typeof(SearchPage));
        _navigationService.RegisterPage("Analytics", typeof(AnalyticsPage));
        _navigationService.RegisterPage("Settings", typeof(SettingsPage));
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _navigationService.Navigated -= OnNavigationServiceNavigated;
        ViewModel.Dispose();
    }

    /// <summary>
    /// Keeps the shell in step with whatever the Frame actually shows: selects
    /// the NavigationView item for the page (without re-navigating) and enables
    /// the back button when there is history to go back to. Fires for every
    /// navigation path - item clicks, programmatic "view on timeline" / draft
    /// resume / activation navigations, and back/forward.
    /// </summary>
    private void OnNavigationServiceNavigated(object? sender, PageNavigatedEventArgs e)
    {
        NavigationView.IsBackEnabled = e.CanGoBack;

        if (!string.IsNullOrEmpty(e.PageKey))
        {
            SelectNavigationItem(e.PageKey);
        }
    }

    private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingSelection)
        {
            // Selection is being mirrored from a navigation that already
            // happened; navigating again here would double it.
            return;
        }

        if (args.IsSettingsSelected)
        {
            _navigationService.NavigateTo("Settings");
        }
        else if (args.SelectedItemContainer != null)
        {
            var navItemTag = args.SelectedItemContainer.Tag?.ToString();
            if (!string.IsNullOrEmpty(navItemTag))
            {
                _navigationService.NavigateTo(navItemTag);
            }
        }
    }

    private void NavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        _navigationService.GoBack();
    }

    #region Keyboard Navigation Handlers

    private void NavigateTo(string pageTag)
    {
        // The Navigated handler selects the matching NavigationView item.
        _navigationService.NavigateTo(pageTag);
    }

    /// <summary>
    /// Selects the NavigationView item whose Tag matches the page key (or the
    /// built-in Settings item) WITHOUT triggering a navigation: the
    /// SelectionChanged handler is suppressed for the duration.
    /// </summary>
    private void SelectNavigationItem(string tag)
    {
        _isSyncingSelection = true;
        try
        {
            if (tag == "Settings")
            {
                NavigationView.SelectedItem = NavigationView.SettingsItem;
                return;
            }

            foreach (var item in NavigationView.MenuItems)
            {
                if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == tag)
                {
                    NavigationView.SelectedItem = navItem;
                    return;
                }
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void NavigateToTimeline_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Timeline");
        args.Handled = true;
    }

    private void NavigateToQueue_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Queue");
        args.Handled = true;
    }

    private void NavigateToReview_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Review");
        args.Handled = true;
    }

    private void NavigateToConnections_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Connections");
        args.Handled = true;
    }

    private void NavigateToSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Search");
        args.Handled = true;
    }

    private void NavigateToAnalytics_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Analytics");
        args.Handled = true;
    }

    private void NavigateToSettings_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo("Settings");
        args.Handled = true;
    }

    private void PasteCapture_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // The parameterized navigation carries "paste" to QueuePage.OnNavigatedTo,
        // which opens the paste-capture dialog; the Navigated handler then syncs
        // the NavigationView selection without navigating a second time.
        _navigationService.NavigateTo("Queue", "paste");
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // On the timeline, refresh the data in place: re-navigating the Frame
        // no-ops for the singleton TimelineViewModel (its viewport/state is
        // preserved across navigation), so the user would see nothing happen.
        if (ContentFrame.CurrentSourcePageType == typeof(TimelinePage))
        {
            var timelineViewModel = App.Current.Services.GetRequiredService<TimelineViewModel>();
            if (timelineViewModel.RefreshCommand.CanExecute(null))
            {
                timelineViewModel.RefreshCommand.Execute(null);
            }
        }
        else if (ContentFrame.CurrentSourcePageType != null)
        {
            // Other pages reload their data on navigation; re-navigate to refresh.
            ContentFrame.Navigate(ContentFrame.CurrentSourcePageType);
        }
        args.Handled = true;
    }

    private void GoBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _navigationService.GoBack();
        args.Handled = true;
    }

    private void GoForward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _navigationService.GoForward();
        args.Handled = true;
    }

    #endregion
}
