using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace MemoryTimeline.Services;

/// <summary>
/// Service interface for application navigation.
/// </summary>
public interface INavigationService
{
    Frame? Frame { get; set; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    void NavigateTo(string pageKey);
    void NavigateTo(string pageKey, object? parameter);
    void GoBack();
    void GoForward();
    void RegisterPage(string key, Type pageType);

    /// <summary>
    /// Raised after the attached <see cref="Frame"/> completed any navigation —
    /// a page selected in the NavigationView, a programmatic
    /// <see cref="NavigateTo(string, object?)"/> ("view on timeline", draft
    /// resume, Jump List / toast activation), or back/forward. The shell uses
    /// it to keep the NavigationView selection and back-button state in sync
    /// with the page actually shown, whichever path got there.
    /// </summary>
    event EventHandler<PageNavigatedEventArgs>? Navigated;
}

/// <summary>
/// Describes a completed Frame navigation in terms of the page keys registered
/// with <see cref="INavigationService.RegisterPage"/>.
/// </summary>
public sealed class PageNavigatedEventArgs : EventArgs
{
    public PageNavigatedEventArgs(string? pageKey, Type pageType, bool canGoBack, bool canGoForward)
    {
        PageKey = pageKey;
        PageType = pageType;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
    }

    /// <summary>The registered key of the page navigated to, or null when the page type was never registered.</summary>
    public string? PageKey { get; }

    /// <summary>The page type navigated to.</summary>
    public Type PageType { get; }

    /// <summary>Whether the frame can navigate back after this navigation.</summary>
    public bool CanGoBack { get; }

    /// <summary>Whether the frame can navigate forward after this navigation.</summary>
    public bool CanGoForward { get; }
}

/// <summary>
/// Navigation service implementation.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly Dictionary<string, Type> _pages = new();
    private readonly ILogger<NavigationService>? _logger;
    private Frame? _frame;

    public NavigationService(ILogger<NavigationService>? logger = null)
    {
        _logger = logger;
    }

    public event EventHandler<PageNavigatedEventArgs>? Navigated;

    public Frame? Frame
    {
        get => _frame;
        set
        {
            if (ReferenceEquals(_frame, value))
            {
                return;
            }

            // Detach from the previous frame to avoid leaking the handler / handling
            // navigation events for a frame we no longer own.
            if (_frame != null)
            {
                _frame.Navigated -= OnNavigated;
            }

            _frame = value;

            if (_frame != null)
            {
                _frame.Navigated += OnNavigated;
            }
        }
    }

    public bool CanGoBack => Frame?.CanGoBack ?? false;

    public bool CanGoForward => Frame?.CanGoForward ?? false;

    public void RegisterPage(string key, Type pageType)
    {
        if (!_pages.ContainsKey(key))
        {
            _pages.Add(key, pageType);
        }
    }

    public void NavigateTo(string pageKey)
    {
        NavigateTo(pageKey, null);
    }

    public void NavigateTo(string pageKey, object? parameter)
    {
        if (Frame == null)
        {
            _logger?.LogWarning("Navigation to '{PageKey}' requested before a Frame was attached", pageKey);
            return;
        }

        if (_pages.TryGetValue(pageKey, out var pageType))
        {
            Frame.Navigate(pageType, parameter);
        }
        else
        {
            _logger?.LogWarning("Navigation requested for unknown page key '{PageKey}'; no page is registered under that key", pageKey);
        }
    }

    public void GoBack()
    {
        if (CanGoBack)
        {
            Frame?.GoBack();
        }
    }

    public void GoForward()
    {
        if (CanGoForward)
        {
            Frame?.GoForward();
        }
    }

    /// <summary>
    /// Translates the Frame's navigation back into a page key and raises
    /// <see cref="Navigated"/>. Frame.Navigated fires on the UI thread for
    /// every navigation mode (New, Back, Forward, Refresh), so subscribers can
    /// touch UI directly.
    /// </summary>
    private void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var pageType = e.SourcePageType;
        string? pageKey = null;
        foreach (var registration in _pages)
        {
            if (registration.Value == pageType)
            {
                pageKey = registration.Key;
                break;
            }
        }

        Navigated?.Invoke(this, new PageNavigatedEventArgs(pageKey, pageType, CanGoBack, CanGoForward));
    }
}
