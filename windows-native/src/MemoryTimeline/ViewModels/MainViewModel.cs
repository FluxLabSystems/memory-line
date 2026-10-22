using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Services;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the application shell (<see cref="MainWindow"/>). Owns the
/// navigation badge counts: the Queue item shows how many captures are waiting
/// or failed (<c>recording_queue</c> rows in <c>pending</c>/<c>failed</c>), the
/// Review item shows how many extracted events await a decision.
/// <para>
/// The counts are re-queried (never incremented locally) whenever something
/// that can move them happens: a queue item changes status
/// (<see cref="IQueueService.QueueItemStatusChanged"/>), the Queue page changes
/// the queue (<see cref="QueueChangedMessage"/>), the Review page approves or
/// rejects (<see cref="PendingEventsChangedMessage"/>), an event is created
/// anywhere (<see cref="EventCreatedMessage"/> - approval creates one), or the
/// archive is restored from a backup (<see cref="ArchiveRestoredMessage"/>).
/// Service events and messages arrive on arbitrary threads and are marshalled
/// to the UI thread before the bound properties change; overlapping requests
/// are coalesced into one trailing refresh.
/// </para>
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IQueueService _queueService;
    private readonly IEventExtractionService _extractionService;
    private readonly ILogger<MainViewModel> _logger;

    // Captured on construction (UI thread) so background callbacks can marshal
    // bound-state mutations back onto the dispatcher.
    private readonly DispatcherQueue? _dispatcherQueue;

    // UI-thread-confined coalescing state: one refresh runs at a time and a
    // request that arrives mid-flight re-runs the query loop once more.
    private bool _isRefreshing;
    private bool _refreshQueued;
    private bool _disposed;

    /// <summary>Queue items needing attention: pending plus failed.</summary>
    [ObservableProperty]
    private int _queueBadgeCount;

    /// <summary>Extracted events awaiting review.</summary>
    [ObservableProperty]
    private int _reviewBadgeCount;

    /// <summary>InfoBadge visibility: a zero count hides the badge instead of showing "0".</summary>
    public bool IsQueueBadgeVisible => QueueBadgeCount > 0;

    /// <summary>InfoBadge visibility: a zero count hides the badge instead of showing "0".</summary>
    public bool IsReviewBadgeVisible => ReviewBadgeCount > 0;

    partial void OnQueueBadgeCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsQueueBadgeVisible));
    }

    partial void OnReviewBadgeCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsReviewBadgeVisible));
    }

    public MainViewModel(
        IQueueService queueService,
        IEventExtractionService extractionService,
        ILogger<MainViewModel> logger)
    {
        _queueService = queueService;
        _extractionService = extractionService;
        _logger = logger;

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // The queue service is a singleton that outlives nothing here - the
        // shell ViewModel lives for the whole app - but the subscription is
        // still released in Dispose for symmetry with the page ViewModels.
        _queueService.QueueItemStatusChanged += OnQueueItemStatusChanged;

        WeakReferenceMessenger.Default.Register<QueueChangedMessage>(this, static (recipient, _) =>
        {
            ((MainViewModel)recipient).RequestRefresh();
        });
        WeakReferenceMessenger.Default.Register<PendingEventsChangedMessage>(this, static (recipient, _) =>
        {
            ((MainViewModel)recipient).RequestRefresh();
        });
        WeakReferenceMessenger.Default.Register<EventCreatedMessage>(this, static (recipient, _) =>
        {
            ((MainViewModel)recipient).RequestRefresh();
        });
        WeakReferenceMessenger.Default.Register<ArchiveRestoredMessage>(this, static (recipient, _) =>
        {
            ((MainViewModel)recipient).RequestRefresh();
        });
    }

    /// <summary>
    /// Loads the initial badge counts. Called once from the MainWindow
    /// constructor; failures are logged and leave the badges hidden.
    /// </summary>
    public Task InitializeAsync()
    {
        return RefreshBadgesAsync();
    }

    /// <summary>
    /// Requests a badge refresh from any thread. The actual query runs on the
    /// UI thread so the coalescing flags and bound properties are never touched
    /// concurrently.
    /// </summary>
    public void RequestRefresh()
    {
        if (_disposed)
        {
            return;
        }

        RunOnUi(() => _ = RefreshBadgesAsync());
    }

    /// <summary>
    /// Re-queries both counts. Must run on the UI thread (see <see cref="RequestRefresh"/>).
    /// </summary>
    public async Task RefreshBadgesAsync()
    {
        if (_isRefreshing)
        {
            _refreshQueued = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            do
            {
                _refreshQueued = false;

                var pending = await _queueService.GetQueueCountByStatusAsync(QueueStatus.Pending);
                var failed = await _queueService.GetQueueCountByStatusAsync(QueueStatus.Failed);
                var awaitingReview = await _extractionService.GetPendingEventCountAsync(false);

                QueueBadgeCount = pending + failed;
                ReviewBadgeCount = awaitingReview;
            }
            while (_refreshQueued);
        }
        catch (Exception ex)
        {
            // A badge is decoration; a failed count must never surface as a
            // crash or a dialog, but it is still logged rather than swallowed.
            _logger.LogWarning(ex, "Could not refresh the navigation badge counts");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnQueueItemStatusChanged(object? sender, QueueItemStatusChangedEventArgs e)
    {
        RequestRefresh();
    }

    /// <summary>
    /// Marshals the supplied action onto the UI dispatcher thread. Runs inline
    /// when already on it (or when no dispatcher was captured).
    /// </summary>
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
    /// Unsubscribes from the queue service and the messenger. Called when the
    /// main window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queueService.QueueItemStatusChanged -= OnQueueItemStatusChanged;
        WeakReferenceMessenger.Default.UnregisterAll(this);

        GC.SuppressFinalize(this);
    }
}
