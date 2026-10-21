using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MemoryTimeline.ViewModels;

namespace MemoryTimeline.Views;

/// <summary>
/// Timeline visualization page.
/// </summary>
public sealed partial class TimelinePage : Page
{
    public TimelineViewModel ViewModel { get; }

    public TimelinePage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.GetRequiredService<TimelineViewModel>();

        // Set the ViewModel on the TimelineControl
        TimelineControl.ViewModel = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not string parameter || string.IsNullOrWhiteSpace(parameter))
        {
            return;
        }

        // "draft:<draftId>" navigations (from the Review page's Drafts tab)
        // resume an event draft in the create-event dialog.
        if (parameter.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            var draftId = parameter.Substring("draft:".Length).Trim();
            if (!string.IsNullOrEmpty(draftId))
            {
                OpenEventDraftWhenReady(draftId);
            }
            return;
        }

        // "View on timeline" navigations (Search, Ask, Connections, Map, Home,
        // People, toast and Jump List "event:" deep links) pass an event id;
        // the ViewModel moves the viewport to it, reports a missing event in
        // its status line, and announces the event as opened (Jump List).
        await ViewModel.NavigateToEventAsync(parameter);
    }

    /// <summary>
    /// Opens the create-event dialog prefilled from the given draft once the
    /// timeline control is loaded (a ContentDialog needs a live XamlRoot), then
    /// defers the actual show through the DispatcherQueue so the layout pass
    /// has completed.
    /// </summary>
    private void OpenEventDraftWhenReady(string draftId)
    {
        void ShowDeferred()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    await TimelineControl.ShowEventDialogFromDraftAsync(draftId);
                }
                catch
                {
                    // Draft resume is best-effort; the timeline still renders normally.
                }
            });
        }

        if (TimelineControl.IsLoaded)
        {
            ShowDeferred();
            return;
        }

        Microsoft.UI.Xaml.RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            TimelineControl.Loaded -= loadedHandler;
            ShowDeferred();
        };
        TimelineControl.Loaded += loadedHandler;
    }
}
