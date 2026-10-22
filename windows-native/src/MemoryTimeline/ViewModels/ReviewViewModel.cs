using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Services;
using System.Collections.ObjectModel;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for reviewing and managing pending events extracted from recordings,
/// the per-event person suggestions, and the saved editor drafts list.
/// </summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly IEventExtractionService _extractionService;
    private readonly ILogger<ReviewViewModel> _logger;
    private readonly IDraftService _draftService;
    private readonly INavigationService _navigationService;

    // Captured at construction time (the ViewModel is created on the UI thread) so
    // messenger callbacks arriving from background threads can marshal to the UI thread.
    private readonly DispatcherQueue? _dispatcherQueue;

    // Per-pending-event suggestion UI state, cached so each card's suggestions
    // are only fetched once (cleared when the pending list reloads).
    private readonly Dictionary<string, PersonSuggestionsState> _suggestionStates = new();

    [ObservableProperty]
    private ObservableCollection<PendingEventDto> _pendingEvents = new();

    [ObservableProperty]
    private PendingEventDto? _selectedEvent;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _approvedCount;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showApprovedEvents;

    /// <summary>
    /// True while the edit panel for <see cref="SelectedEvent"/> is open.
    /// </summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// Saved editor drafts (events, eras, people), most recently updated first.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<DraftDto> _drafts = new();

    /// <summary>
    /// Total number of saved drafts (drives the Drafts pivot header).
    /// </summary>
    [ObservableProperty]
    private int _draftCount;

    /// <summary>
    /// True while the drafts error InfoBar is open (closable by the user).
    /// </summary>
    [ObservableProperty]
    private bool _isDraftsErrorOpen;

    /// <summary>
    /// Message shown in the drafts error InfoBar.
    /// </summary>
    [ObservableProperty]
    private string _draftsErrorMessage = string.Empty;

    // Computed properties
    public bool HasPendingEvents => PendingEvents.Any();
    public bool IsEmpty => !HasPendingEvents;

    /// <summary>Header text for the pending-events pivot, e.g. "Pending (4)".</summary>
    public string PendingPivotHeader => $"Pending ({PendingCount})";

    /// <summary>Header text for the drafts pivot, e.g. "Drafts (2)".</summary>
    public string DraftsPivotHeader => $"Drafts ({DraftCount})";

    /// <summary>True when at least one draft exists.</summary>
    public bool HasDrafts => Drafts.Count > 0;

    /// <summary>True when no drafts exist (drives the drafts empty state).</summary>
    public bool IsDraftsEmpty => !HasDrafts;

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(PendingPivotHeader));
    }

    partial void OnDraftCountChanged(int value)
    {
        OnPropertyChanged(nameof(DraftsPivotHeader));
    }

    /// <summary>
    /// Set by the view to show a confirmation dialog before destructive actions.
    /// Arguments: title, message, confirm-button label. Returns true when the
    /// user confirms. Rejecting a pending event hard-deletes it (including its
    /// transcript) with no recovery path, so confirmation is required.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ConfirmDestructiveActionAsync { get; set; }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        var handler = ConfirmDestructiveActionAsync;
        if (handler == null)
        {
            // No view attached (e.g. unit tests): keep the command functional.
            return true;
        }

        return await handler(title, message, confirmLabel);
    }

    public ReviewViewModel(
        IEventExtractionService extractionService,
        ILogger<ReviewViewModel> logger,
        IDraftService draftService,
        INavigationService navigationService)
    {
        _extractionService = extractionService;
        _logger = logger;
        _draftService = draftService;
        _navigationService = navigationService;

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Refresh the drafts list whenever a draft is saved or deleted anywhere
        // in the app (event/era/person editors, this page's delete button).
        WeakReferenceMessenger.Default.Register<DraftsChangedMessage>(this, static (recipient, _) =>
        {
            ((ReviewViewModel)recipient).HandleDraftsChangedMessage();
        });
    }

    /// <summary>
    /// Handles a <see cref="DraftsChangedMessage"/> from any thread by marshalling
    /// the drafts reload onto the UI thread.
    /// </summary>
    private void HandleDraftsChangedMessage()
    {
        void Handle() => _ = RefreshDraftsAsync();

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
    /// Initializes the view by loading pending events and drafts.
    /// </summary>
    public async Task InitializeAsync()
    {
        await RefreshPendingEventsAsync();
        await RefreshDraftsAsync();
    }

    /// <summary>
    /// Refreshes the list of pending events.
    /// </summary>
    [RelayCommand]
    private async Task RefreshPendingEventsAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading pending events...";

            var events = await _extractionService.GetAllPendingEventsAsync();

            PendingEvents.Clear();

            // The pending list is being rebuilt; drop the cached per-card
            // suggestion states so re-expanded cards fetch fresh suggestions.
            _suggestionStates.Clear();
            foreach (var evt in events)
            {
                var dto = PendingEventDto.FromPendingEvent(evt);

                // Wire up commands
                dto.ApproveCommand = new RelayCommand(() => _ = ApproveEventAsync(dto));
                dto.RejectCommand = new RelayCommand(() => _ = RejectEventAsync(dto));
                dto.EditCommand = new RelayCommand(() => SelectEventForEdit(dto));

                PendingEvents.Add(dto);
            }

            await UpdateCountsAsync();

            OnPropertyChanged(nameof(HasPendingEvents));
            OnPropertyChanged(nameof(IsEmpty));

            StatusMessage = $"Loaded {PendingEvents.Count} pending events";
            _logger.LogInformation("Loaded {Count} pending events", PendingEvents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing pending events");
            StatusMessage = "Error loading pending events";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Approves a pending event and creates it as a real event.
    /// </summary>
    [RelayCommand]
    private async Task ApproveEventAsync(PendingEventDto? eventDto)
    {
        if (eventDto == null) return;

        try
        {
            StatusMessage = $"Approving: {eventDto.Title}";

            var createdEvent = await _extractionService.ApprovePendingEventAsync(eventDto.PendingEventId);

            PendingEvents.Remove(eventDto);

            OnPropertyChanged(nameof(HasPendingEvents));
            OnPropertyChanged(nameof(IsEmpty));

            await UpdateCountsAsync();

            // Let the shell re-count its Review badge (rejection raises no Core message).
            WeakReferenceMessenger.Default.Send(new PendingEventsChangedMessage());
            StatusMessage = $"Approved: {eventDto.Title}";
            _logger.LogInformation("Approved pending event: {EventId}", createdEvent.EventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving pending event");
            StatusMessage = $"Error approving event: {ex.Message}";
        }
    }

    /// <summary>
    /// Rejects and deletes a pending event after user confirmation
    /// (rejection permanently deletes the event and its transcript).
    /// </summary>
    [RelayCommand]
    private async Task RejectEventAsync(PendingEventDto? eventDto)
    {
        if (eventDto == null) return;

        var confirmed = await ConfirmAsync(
            "Reject Pending Event",
            $"Reject and permanently delete \"{eventDto.Title}\"? " +
            "The extracted event and its transcript cannot be recovered.",
            "Reject");
        if (!confirmed)
        {
            StatusMessage = "Reject cancelled";
            return;
        }

        try
        {
            StatusMessage = $"Rejecting: {eventDto.Title}";

            await _extractionService.RejectPendingEventAsync(eventDto.PendingEventId);

            PendingEvents.Remove(eventDto);

            OnPropertyChanged(nameof(HasPendingEvents));
            OnPropertyChanged(nameof(IsEmpty));

            await UpdateCountsAsync();

            // Let the shell re-count its Review badge (rejection raises no Core message).
            WeakReferenceMessenger.Default.Send(new PendingEventsChangedMessage());
            StatusMessage = $"Rejected: {eventDto.Title}";
            _logger.LogInformation("Rejected pending event: {EventId}", eventDto.PendingEventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting pending event");
            StatusMessage = $"Error rejecting event: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves changes to a pending event.
    /// </summary>
    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        if (SelectedEvent == null) return;

        try
        {
            StatusMessage = "Saving changes...";

            var pendingEvent = SelectedEvent.ToPendingEvent();
            await _extractionService.UpdatePendingEventAsync(pendingEvent);

            IsEditing = false;
            StatusMessage = $"Changes saved: {pendingEvent.Title}";
            _logger.LogInformation("Updated pending event: {EventId}", pendingEvent.PendingEventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving changes");
            StatusMessage = $"Error saving changes: {ex.Message}";
        }
    }

    /// <summary>
    /// Approves all pending events.
    /// </summary>
    [RelayCommand]
    private async Task ApproveAllAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Approving all events...";

            var eventsToApprove = PendingEvents.ToList();
            var affectedQueues = new HashSet<string>(StringComparer.Ordinal);
            int approvedCount = 0;

            foreach (var evt in eventsToApprove)
            {
                try
                {
                    // Publishing is deferred: a batch must produce one capture
                    // status per capture, not one per event.
                    await _extractionService.ApprovePendingEventAsync(
                        evt.PendingEventId, publishCaptureStatus: false);
                    PendingEvents.Remove(evt);
                    approvedCount++;

                    if (!string.IsNullOrWhiteSpace(evt.QueueId))
                    {
                        affectedQueues.Add(evt.QueueId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to approve event: {Title}", evt.Title);
                }
            }

            await PublishCaptureStatusesAsync(affectedQueues);

            OnPropertyChanged(nameof(HasPendingEvents));
            OnPropertyChanged(nameof(IsEmpty));

            await UpdateCountsAsync();

            // Let the shell re-count its Review badge (rejection raises no Core message).
            WeakReferenceMessenger.Default.Send(new PendingEventsChangedMessage());
            StatusMessage = $"Approved {approvedCount} of {eventsToApprove.Count} events";
            _logger.LogInformation("Bulk approved {Count} events", approvedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in approve all");
            StatusMessage = "Error approving all events";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Rejects all pending events after user confirmation
    /// (rejection permanently deletes each event and its transcript).
    /// </summary>
    [RelayCommand]
    private async Task RejectAllAsync()
    {
        var pendingCount = PendingEvents.Count;
        if (pendingCount == 0) return;

        var confirmed = await ConfirmAsync(
            "Reject All Pending Events",
            $"Reject and permanently delete {pendingCount} pending event{(pendingCount == 1 ? "" : "s")}? " +
            "The extracted events and their transcripts cannot be recovered.",
            "Reject All");
        if (!confirmed)
        {
            StatusMessage = "Reject all cancelled";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Rejecting all events...";

            var eventsToReject = PendingEvents.ToList();
            var affectedQueues = new HashSet<string>(StringComparer.Ordinal);
            int rejectedCount = 0;

            foreach (var evt in eventsToReject)
            {
                try
                {
                    // Publishing is deferred: a batch must produce one capture
                    // status per capture, not one per event.
                    await _extractionService.RejectPendingEventAsync(
                        evt.PendingEventId, publishCaptureStatus: false);
                    PendingEvents.Remove(evt);
                    rejectedCount++;

                    if (!string.IsNullOrWhiteSpace(evt.QueueId))
                    {
                        affectedQueues.Add(evt.QueueId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reject event: {Title}", evt.Title);
                }
            }

            await PublishCaptureStatusesAsync(affectedQueues);

            OnPropertyChanged(nameof(HasPendingEvents));
            OnPropertyChanged(nameof(IsEmpty));

            await UpdateCountsAsync();

            // Let the shell re-count its Review badge (rejection raises no Core message).
            WeakReferenceMessenger.Default.Send(new PendingEventsChangedMessage());
            StatusMessage = $"Rejected {rejectedCount} of {eventsToReject.Count} events";
            _logger.LogInformation("Bulk rejected {Count} events", rejectedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in reject all");
            StatusMessage = "Error rejecting all events";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Selects an event and opens the edit panel for it.
    /// </summary>
    private void SelectEventForEdit(PendingEventDto eventDto)
    {
        SelectedEvent = eventDto;
        IsEditing = true;
        StatusMessage = $"Editing: {eventDto.Title}";
    }

    /// <summary>
    /// Closes the edit panel without persisting changes. Note: edits already made
    /// in the panel live on the DTO (TwoWay bindings) but are not saved to the
    /// database until SaveChanges is invoked; a refresh restores stored values.
    /// </summary>
    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        StatusMessage = "Edit cancelled";
    }

    partial void OnSelectedEventChanged(PendingEventDto? value)
    {
        // The edited item disappeared (approved/rejected/refreshed) or selection
        // was cleared: close the edit panel instead of editing a stale target.
        if (value == null)
        {
            IsEditing = false;
        }
    }

    /// <summary>
    /// Republishes the processing status of every capture a bulk approve/reject
    /// touched — once per capture, after the batch, rather than once per event
    /// (design §19 Phase 3). The service swallows its own publish failures, so
    /// this cannot break the review flow.
    /// </summary>
    private async Task PublishCaptureStatusesAsync(IEnumerable<string> queueIds)
    {
        foreach (var queueId in queueIds)
        {
            await _extractionService.PublishCaptureStatusAsync(queueId);
        }
    }

    /// <summary>
    /// Updates the event counts.
    /// </summary>
    private async Task UpdateCountsAsync()
    {
        try
        {
            PendingCount = await _extractionService.GetPendingEventCountAsync(false);
            ApprovedCount = await _extractionService.GetPendingEventCountAsync(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating counts");
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        // Filter events based on search text
        // This would require implementing a filtered view
        StatusMessage = string.IsNullOrEmpty(value) ? "All events" : $"Filtered: {value}";
    }

    #region Drafts

    /// <summary>
    /// Refreshes the drafts list and count. Never throws (safe to fire-and-forget
    /// from messenger callbacks); failures surface via the drafts InfoBar.
    /// </summary>
    [RelayCommand]
    private async Task RefreshDraftsAsync()
    {
        try
        {
            var drafts = await _draftService.GetDraftsAsync();

            Drafts.Clear();
            foreach (var draft in drafts)
            {
                Drafts.Add(draft);
            }

            DraftCount = Drafts.Count;

            OnPropertyChanged(nameof(HasDrafts));
            OnPropertyChanged(nameof(IsDraftsEmpty));

            _logger.LogInformation("Loaded {Count} drafts", Drafts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading drafts");
            DraftsErrorMessage = "Could not load drafts. Use Refresh to try again.";
            IsDraftsErrorOpen = true;
        }
    }

    /// <summary>
    /// Resumes a draft in its owning editor by navigating to the matching page
    /// with the navigation parameter <c>"draft:&lt;draftId&gt;"</c>
    /// (event → Timeline, era → Eras, person → People).
    /// </summary>
    public void ResumeDraft(DraftDto? draft)
    {
        if (draft == null) return;

        var pageKey = draft.DraftType switch
        {
            DraftTypes.Event => "Timeline",
            DraftTypes.Era => "Eras",
            DraftTypes.Person => "People",
            _ => null
        };

        if (pageKey == null)
        {
            _logger.LogWarning(
                "Cannot resume draft {DraftId}: unknown draft type '{DraftType}'",
                draft.DraftId, draft.DraftType);
            StatusMessage = $"Cannot resume draft: unknown type '{draft.DraftType}'";
            return;
        }

        try
        {
            _navigationService.NavigateTo(pageKey, $"draft:{draft.DraftId}");
            StatusMessage = $"Resuming draft: {draft.Title}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming draft {DraftId}", draft.DraftId);
            StatusMessage = $"Error resuming draft: {draft.Title}";
        }
    }

    /// <summary>
    /// Deletes a draft after user confirmation. The drafts list refreshes via
    /// the <see cref="DraftsChangedMessage"/> published by the draft service.
    /// </summary>
    public async Task DeleteDraftAsync(DraftDto? draft)
    {
        if (draft == null) return;

        var confirmed = await ConfirmAsync(
            "Delete Draft",
            $"Delete the draft \"{draft.Title}\"? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            StatusMessage = "Delete cancelled";
            return;
        }

        try
        {
            await _draftService.DeleteDraftAsync(draft.DraftId);
            StatusMessage = $"Deleted draft: {draft.Title}";
            _logger.LogInformation("Deleted draft: {DraftId}", draft.DraftId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting draft {DraftId}", draft.DraftId);
            DraftsErrorMessage = $"Could not delete draft \"{draft.Title}\".";
            IsDraftsErrorOpen = true;
        }
    }

    #endregion

    #region Person suggestions

    /// <summary>
    /// Gets (or creates and caches) the suggestions UI state for one pending
    /// event card. Creating the state is cheap; the suggestions themselves are
    /// only fetched on first expand via <see cref="EnsureSuggestionsLoadedAsync"/>.
    /// </summary>
    public PersonSuggestionsState GetOrCreateSuggestionsState(string pendingEventId)
    {
        if (_suggestionStates.TryGetValue(pendingEventId, out var existing))
        {
            return existing;
        }

        var state = new PersonSuggestionsState(pendingEventId);
        state.ApplyAllCommand = new AsyncRelayCommand(() => ApplyAllSuggestionsAsync(state));

        if (!string.IsNullOrEmpty(pendingEventId))
        {
            _suggestionStates[pendingEventId] = state;
        }

        return state;
    }

    /// <summary>
    /// Loads the person suggestions for a card the first time its expander is
    /// opened. No-ops when already loaded or loading; never throws (errors
    /// surface via the card's InfoBar, and a re-expand retries).
    /// </summary>
    public async Task EnsureSuggestionsLoadedAsync(PersonSuggestionsState? state)
    {
        if (state == null || state.HasLoaded || state.IsLoading || string.IsNullOrEmpty(state.PendingEventId))
        {
            return;
        }

        try
        {
            state.IsLoading = true;
            state.IsErrorOpen = false;

            var suggestions = await _extractionService.GetPersonSuggestionsAsync(state.PendingEventId);

            state.Suggestions.Clear();
            foreach (var suggestion in suggestions)
            {
                state.Suggestions.Add(suggestion);
            }

            state.HasLoaded = true;
            state.RefreshComputed();

            _logger.LogInformation(
                "Loaded {Count} person suggestions for pending event {PendingEventId}",
                state.Suggestions.Count, state.PendingEventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading person suggestions for pending event {PendingEventId}",
                state.PendingEventId);
            state.ErrorMessage = "Could not load people suggestions. Collapse and expand to retry.";
            state.IsErrorOpen = true;
        }
        finally
        {
            state.IsLoading = false;
        }
    }

    /// <summary>
    /// Applies a single person suggestion (create person / update details).
    /// Never throws; errors surface via the card's InfoBar.
    /// </summary>
    public async Task ApplySuggestionAsync(PersonSuggestionDto? suggestion)
    {
        if (suggestion == null || !suggestion.CanApply)
        {
            return;
        }

        _suggestionStates.TryGetValue(suggestion.PendingEventId, out var state);

        try
        {
            await _extractionService.ApplyPersonSuggestionAsync(suggestion);
            StatusMessage = $"Applied person suggestion: {suggestion.Name}";
            _logger.LogInformation("Applied person suggestion for '{Name}' on pending event {PendingEventId}",
                suggestion.Name, suggestion.PendingEventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying person suggestion for '{Name}'", suggestion.Name);
            if (state != null)
            {
                state.ErrorMessage = $"Could not apply the suggestion for '{suggestion.Name}'.";
                state.IsErrorOpen = true;
            }
            else
            {
                StatusMessage = $"Error applying suggestion for {suggestion.Name}";
            }
        }
        finally
        {
            state?.RefreshComputed();
        }
    }

    /// <summary>
    /// Applies every still-applicable suggestion on one card sequentially,
    /// continuing past individual failures and reporting them via the card's
    /// InfoBar. Never throws.
    /// </summary>
    private async Task ApplyAllSuggestionsAsync(PersonSuggestionsState state)
    {
        var applicable = state.Suggestions.Where(s => s.CanApply).ToList();
        if (applicable.Count == 0)
        {
            return;
        }

        state.IsErrorOpen = false;
        var failures = 0;

        foreach (var suggestion in applicable)
        {
            try
            {
                await _extractionService.ApplyPersonSuggestionAsync(suggestion);
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogError(ex, "Error applying person suggestion for '{Name}' during apply-all",
                    suggestion.Name);
            }
        }

        state.RefreshComputed();

        if (failures > 0)
        {
            state.ErrorMessage = $"{failures} of {applicable.Count} suggestions could not be applied.";
            state.IsErrorOpen = true;
        }
        else
        {
            StatusMessage = $"Applied {applicable.Count} person suggestion{(applicable.Count == 1 ? "" : "s")}";
        }
    }

    #endregion
}

/// <summary>
/// Per-pending-event UI state for the lazily loaded person suggestions shown
/// inside a Review card's "People suggestions" expander. Created and cached by
/// <see cref="ReviewViewModel.GetOrCreateSuggestionsState"/>.
/// </summary>
public partial class PersonSuggestionsState : ObservableObject
{
    /// <summary>Creates state for one pending event card.</summary>
    public PersonSuggestionsState(string pendingEventId)
    {
        PendingEventId = pendingEventId;
    }

    /// <summary>Id of the pending event this state belongs to.</summary>
    public string PendingEventId { get; }

    /// <summary>The loaded suggestions (empty until the first expand completes).</summary>
    public ObservableCollection<PersonSuggestionDto> Suggestions { get; } = new();

    /// <summary>True while the suggestions are being fetched.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>True once the suggestions have been fetched successfully.</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    /// <summary>True while the card's error InfoBar is open (closable by the user).</summary>
    [ObservableProperty]
    private bool _isErrorOpen;

    /// <summary>Message shown in the card's error InfoBar.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>
    /// Applies every still-applicable suggestion on this card; assigned by the
    /// owning <see cref="ReviewViewModel"/> at creation time.
    /// </summary>
    public IAsyncRelayCommand? ApplyAllCommand { get; set; }

    /// <summary>True when loading finished and no suggestions were found.</summary>
    public bool IsEmpty => HasLoaded && Suggestions.Count == 0;

    /// <summary>True when at least one suggestion is loaded.</summary>
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>True when at least one suggestion can still be applied.</summary>
    public bool HasApplicableSuggestions => Suggestions.Any(s => s.CanApply);

    partial void OnHasLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Re-notifies the computed properties after the suggestions collection or
    /// individual suggestions' applied state changed.
    /// </summary>
    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasApplicableSuggestions));
    }
}
