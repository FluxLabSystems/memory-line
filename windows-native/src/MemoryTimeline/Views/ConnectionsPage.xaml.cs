using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MemoryTimeline.ViewModels;

namespace MemoryTimeline.Views;

/// <summary>
/// Page for displaying event connections, cross-references, and AI-powered insights.
/// </summary>
public sealed partial class ConnectionsPage : Page
{
    public ConnectionsViewModel ViewModel { get; }

    public ConnectionsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.GetRequiredService<ConnectionsViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Check embedding-service availability so the Settings CTA shows even
        // when the page is opened without an event.
        await ViewModel.InitializeAsync();

        // Check if an event ID was passed as a parameter
        if (e.Parameter is string eventId && !string.IsNullOrEmpty(eventId))
        {
            await ViewModel.LoadConnectionsForEventAsync(eventId);
        }
    }

    /// <summary>
    /// Invokes the ViewModel's NavigateToEventCommand for the similar-event item
    /// whose "View on timeline" button was clicked. A Click handler is used because
    /// x:Bind inside a DataTemplate is scoped to the item's x:DataType and cannot
    /// reach the page's ViewModel property.
    /// </summary>
    private void SimilarEvent_ViewOnTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string eventId } && !string.IsNullOrEmpty(eventId))
        {
            ViewModel.NavigateToEventCommand.Execute(eventId);
        }
    }

    /// <summary>
    /// Invokes AddSuggestedTagCommand for the suggestion whose "Add" button was
    /// clicked (same x:Bind scoping reason as above; the button's Tag carries
    /// the suggestion item itself).
    /// </summary>
    private void SuggestedTag_Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TagSuggestionDto suggestion })
        {
            ViewModel.AddSuggestedTagCommand.Execute(suggestion);
        }
    }
}
