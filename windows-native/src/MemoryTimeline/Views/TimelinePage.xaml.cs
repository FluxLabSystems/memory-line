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
    /// <summary>
    /// Navigation parameter sent by the Jump List "New Event" quick action
    /// (App.HandleActivationAction): opens the blank add-event dialog.
    /// </summary>
    public const string NewEventParameter = "new-event";

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
                OpenEventDialogWhenReady(() => TimelineControl.ShowEventDialogFromDraftAsync(draftId));
            }
            return;
        }

        // Jump List "New Event": open the blank add-event dialog dated today.
        if (string.Equals(parameter, NewEventParameter, StringComparison.OrdinalIgnoreCase))
        {
            OpenEventDialogWhenReady(() => TimelineControl.ShowNewEventDialogAsync());
            return;
        }

        // "View on timeline" navigations (Search, Ask, Connections, Map, Home,
        // People, toast and Jump List "event:" deep links) pass an event id;
        // the ViewModel moves the viewport to it, reports a missing event in
        // its status line, and announces the event as opened (Jump List).
        await ViewModel.NavigateToEventAsync(parameter);
    }

    /// <summary>
    /// Runs a dialog-opening action once the timeline control is loaded (a
    /// ContentDialog needs a live XamlRoot), deferring the actual show through
    /// the DispatcherQueue so the layout pass has completed. Used for draft
    /// resume and the Jump List "New Event" action.
    /// </summary>
    private void OpenEventDialogWhenReady(Func<Task> showDialogAsync)
    {
        void ShowDeferred()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    await showDialogAsync();
                }
                catch (Exception ex)
                {
                    // Opening the dialog is best-effort; the timeline still
                    // renders normally. Say so instead of staying silent.
                    ViewModel.StatusText = $"Could not open the event dialog: {ex.Message}";
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
