using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the Connections page, displaying cross-references and similar events.
/// </summary>
public partial class ConnectionsViewModel : ObservableObject
{
    private readonly IRagService _ragService;
    private readonly IEventService _eventService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITagRepository _tagRepository;
    private readonly INavigationService _navigationService;
    private readonly ILogger<ConnectionsViewModel> _logger;

    // Start date of the selected event, kept so a tag change can publish
    // EventUpdatedMessage (which carries it) without a second lookup.
    private DateTime _selectedEventStartDate;

    [ObservableProperty]
    private string? _selectedEventId;

    [ObservableProperty]
    private string? _selectedEventTitle;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasSelectedEvent;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// True when an embedding API key is configured (embedding service usable).
    /// </summary>
    [ObservableProperty]
    private bool _isEmbeddingServiceAvailable = true;

    /// <summary>
    /// True when the CTA "add an embedding API key in Settings" should be shown.
    /// </summary>
    [ObservableProperty]
    private bool _showEmbeddingCta;

    /// <summary>
    /// True when the embedding service is available but the selected event has
    /// no embedding yet — prompts the user to run "Generate Embeddings" first.
    /// </summary>
    [ObservableProperty]
    private bool _selectedEventNeedsEmbedding;

    [ObservableProperty]
    private ObservableCollection<SimilarEventDto> _similarEvents = new();

    [ObservableProperty]
    private ObservableCollection<CrossReferenceDto> _crossReferences = new();

    [ObservableProperty]
    private ObservableCollection<TagSuggestionDto> _tagSuggestions = new();

    [ObservableProperty]
    private int _similarEventsCount;

    [ObservableProperty]
    private int _crossReferencesCount;

    [ObservableProperty]
    private int _tagSuggestionsCount;

    public ConnectionsViewModel(
        IRagService ragService,
        IEventService eventService,
        IEmbeddingService embeddingService,
        ITagRepository tagRepository,
        INavigationService navigationService,
        ILogger<ConnectionsViewModel> logger)
    {
        _ragService = ragService;
        _eventService = eventService;
        _embeddingService = embeddingService;
        _tagRepository = tagRepository;
        _navigationService = navigationService;
        _logger = logger;
    }

    /// <summary>
    /// Initializes page-level state: checks whether the embedding service is
    /// configured and shows the Settings call-to-action when it is not.
    /// Called from ConnectionsPage.OnNavigatedTo on every navigation.
    /// </summary>
    public async Task InitializeAsync()
    {
        IsEmbeddingServiceAvailable = await CheckEmbeddingAvailabilityAsync();
        ShowEmbeddingCta = !IsEmbeddingServiceAvailable;
    }

    /// <summary>
    /// Loads connections for a specific event.
    /// </summary>
    public async Task LoadConnectionsForEventAsync(string eventId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            SelectedEventId = eventId;

            _logger.LogInformation("Loading connections for event {EventId}", eventId);

            // Load event details
            var eventDetails = await _eventService.GetEventByIdAsync(eventId);
            if (eventDetails == null)
            {
                ErrorMessage = "Event not found";
                HasSelectedEvent = false;
                return;
            }

            SelectedEventTitle = eventDetails.Title;
            _selectedEventStartDate = eventDetails.StartDate;
            HasSelectedEvent = true;

            // Embedding availability drives the CTA / "generate first" hint
            IsEmbeddingServiceAvailable = await CheckEmbeddingAvailabilityAsync();
            ShowEmbeddingCta = !IsEmbeddingServiceAvailable;
            SelectedEventNeedsEmbedding = IsEmbeddingServiceAvailable
                && !await _eventService.HasEmbeddingAsync(eventId);

            // Load similar events
            await LoadSimilarEventsAsync(eventId);

            // Load cross-references
            await LoadCrossReferencesAsync(eventId);

            // Load tag suggestions
            await LoadTagSuggestionsAsync(eventId);

            _logger.LogInformation("Loaded connections: {Similar} similar, {CrossRefs} cross-refs, {Tags} tag suggestions",
                SimilarEventsCount, CrossReferencesCount, TagSuggestionsCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading connections for event {EventId}", eventId);
            ErrorMessage = $"Error loading connections: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Loads similar events.
    /// </summary>
    private async Task LoadSimilarEventsAsync(string eventId)
    {
        try
        {
            // No explicit threshold: the service reads the rag_similarity_threshold setting.
            var similar = await _ragService.FindSimilarEventsAsync(eventId, topK: 10);

            SimilarEvents.Clear();
            foreach (var item in similar)
            {
                SimilarEvents.Add(new SimilarEventDto
                {
                    EventId = item.EventId,
                    Title = item.Title,
                    Description = item.Description,
                    StartDate = item.StartDate,
                    Similarity = item.Similarity
                });
            }

            SimilarEventsCount = SimilarEvents.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading similar events");
            AppendError($"Error loading similar events: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads cross-references.
    /// </summary>
    private async Task LoadCrossReferencesAsync(string eventId)
    {
        try
        {
            var crossRefs = await _ragService.DetectCrossReferencesAsync(eventId);

            CrossReferences.Clear();
            foreach (var item in crossRefs)
            {
                // Load target event details
                var targetEvent = await _eventService.GetEventByIdAsync(item.TargetEventId);
                if (targetEvent != null)
                {
                    CrossReferences.Add(new CrossReferenceDto
                    {
                        SourceEventId = item.SourceEventId,
                        TargetEventId = item.TargetEventId,
                        TargetEventTitle = targetEvent.Title,
                        TargetEventDate = targetEvent.StartDate,
                        RelationshipType = item.RelationshipType,
                        Confidence = item.Confidence,
                        Reasoning = item.Reasoning
                    });
                }
            }

            CrossReferencesCount = CrossReferences.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cross-references");
            AppendError($"Error loading cross-references: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads tag suggestions.
    /// </summary>
    private async Task LoadTagSuggestionsAsync(string eventId)
    {
        try
        {
            var suggestions = await _ragService.SuggestTagsAsync(eventId, maxSuggestions: 5);

            TagSuggestions.Clear();
            foreach (var item in suggestions)
            {
                TagSuggestions.Add(new TagSuggestionDto
                {
                    TagName = item.TagName,
                    Confidence = item.Confidence,
                    Reason = item.Reason
                });
            }

            TagSuggestionsCount = TagSuggestions.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading tag suggestions");
            AppendError($"Error loading tag suggestions: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends a message to <see cref="ErrorMessage"/> so multiple sub-load
    /// failures are all surfaced in the error InfoBar instead of being swallowed.
    /// </summary>
    private void AppendError(string message)
    {
        ErrorMessage = string.IsNullOrEmpty(ErrorMessage)
            ? message
            : $"{ErrorMessage}\n{message}";
    }

    /// <summary>
    /// Safely checks embedding-service availability (a broken settings read
    /// should degrade to the CTA state, not crash the page).
    /// </summary>
    private async Task<bool> CheckEmbeddingAvailabilityAsync()
    {
        try
        {
            return await _embeddingService.IsAvailableAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking embedding service availability");
            return false;
        }
    }

    /// <summary>
    /// Navigates to the Settings page so the user can configure an embedding API key.
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        _navigationService.NavigateTo("Settings");
    }

    /// <summary>
    /// Refreshes all connections data.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!string.IsNullOrEmpty(SelectedEventId))
        {
            await LoadConnectionsForEventAsync(SelectedEventId);
        }
    }

    /// <summary>
    /// "Add" on a tag suggestion: links the suggested tag to the selected
    /// event (creating the tag row if it does not exist yet) through
    /// IEventService.AddTagToEventAsync, drops the suggestion from the list,
    /// and lets the timeline/search know the event changed. Failures surface
    /// in the page's error InfoBar.
    /// </summary>
    [RelayCommand]
    private async Task AddSuggestedTagAsync(TagSuggestionDto? suggestion)
    {
        var eventId = SelectedEventId;
        if (suggestion == null || string.IsNullOrEmpty(eventId))
        {
            return;
        }

        var tagName = suggestion.TagName.Trim();
        if (tagName.Length == 0)
        {
            return;
        }

        try
        {
            ErrorMessage = null;

            var tag = await _tagRepository.GetByNameAsync(tagName)
                      ?? await _tagRepository.AddAsync(new Tag { TagName = tagName });

            await _eventService.AddTagToEventAsync(eventId, tag.TagId, isManual: true, confidence: suggestion.Confidence);

            TagSuggestions.Remove(suggestion);
            TagSuggestionsCount = TagSuggestions.Count;
            StatusMessage = $"Added tag \"{tagName}\" to {SelectedEventTitle}";

            // Tags are part of the event as the timeline and search show it.
            WeakReferenceMessenger.Default.Send(new EventUpdatedMessage(eventId, _selectedEventStartDate));

            _logger.LogInformation("Added suggested tag {Tag} to event {EventId}", tagName, eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding suggested tag {Tag} to event {EventId}", tagName, eventId);
            ErrorMessage = $"Could not add tag \"{tagName}\": {ex.Message}";
        }
    }

    /// <summary>
    /// Navigates to a specific event on the timeline.
    /// </summary>
    [RelayCommand]
    private void NavigateToEvent(string eventId)
    {
        _logger.LogInformation("Navigating to event {EventId}", eventId);
        // "Timeline" is the key registered in MainWindow.RegisterPages
        // ("TimelinePage" was never registered, so this used to no-op).
        _navigationService.NavigateTo("Timeline", eventId);
    }

    /// <summary>
    /// Generates embeddings for all events that don't have them.
    /// </summary>
    [RelayCommand]
    private async Task GenerateEmbeddingsAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;

            // Pre-flight: without a configured embedding API key every call would fail.
            IsEmbeddingServiceAvailable = await CheckEmbeddingAvailabilityAsync();
            ShowEmbeddingCta = !IsEmbeddingServiceAvailable;
            if (!IsEmbeddingServiceAvailable)
            {
                ErrorMessage = "Add an OpenAI embedding API key in Settings to enable connections.";
                return;
            }

            _logger.LogInformation("Starting embedding generation workflow");
            StatusMessage = "Finding events without embeddings...";

            // Get all events without embeddings
            var allEvents = await _eventService.GetAllEventsAsync();
            var eventsNeedingEmbeddings = new List<Data.Models.Event>();

            foreach (var evt in allEvents)
            {
                var hasEmbedding = await _eventService.HasEmbeddingAsync(evt.EventId);
                if (!hasEmbedding)
                {
                    eventsNeedingEmbeddings.Add(evt);
                }
            }

            if (eventsNeedingEmbeddings.Count == 0)
            {
                _logger.LogInformation("All events already have embeddings");
                StatusMessage = "All events already have embeddings.";
                return;
            }

            var total = eventsNeedingEmbeddings.Count;
            _logger.LogInformation("Generating embeddings for {Count} events", total);

            // Generate embeddings one by one; the service throws on failure,
            // so tally successes/failures instead of silently reporting success.
            int succeeded = 0;
            int failed = 0;
            int processed = 0;
            string? firstError = null;

            foreach (var evt in eventsNeedingEmbeddings)
            {
                processed++;
                StatusMessage = $"Generating embeddings ({processed}/{total})...";

                try
                {
                    await _eventService.GenerateEmbeddingAsync(evt.EventId);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    firstError ??= ex.Message;
                    _logger.LogError(ex, "Error generating embedding for event {EventId}", evt.EventId);
                }
            }

            _logger.LogInformation("Embedding generation complete: {Succeeded} succeeded, {Failed} failed of {Total}",
                succeeded, failed, total);

            // Refresh current view first (it resets ErrorMessage), then report the outcome.
            await RefreshAsync();

            if (failed == 0)
            {
                StatusMessage = $"{succeeded} embedded.";
            }
            else
            {
                StatusMessage = $"{succeeded} embedded, {failed} failed.";
                ErrorMessage = $"{succeeded} embedded, {failed} failed: {firstError}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in embedding generation workflow");
            ErrorMessage = $"Error generating embeddings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>
/// DTO for displaying similar events.
/// </summary>
public class SimilarEventDto : ObservableObject
{
    private string _eventId = string.Empty;
    private string _title = string.Empty;
    private string? _description;
    private DateTime _startDate;
    private double _similarity;

    public string EventId
    {
        get => _eventId;
        set => SetProperty(ref _eventId, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public DateTime StartDate
    {
        get => _startDate;
        set => SetProperty(ref _startDate, value);
    }

    public double Similarity
    {
        get => _similarity;
        set => SetProperty(ref _similarity, value);
    }

    public string SimilarityDisplay => $"{Similarity:P0}";

    public string StartDateDisplay => StartDate.ToString("MMM d, yyyy");
}

/// <summary>
/// DTO for displaying cross-references.
/// </summary>
public class CrossReferenceDto : ObservableObject
{
    private string _sourceEventId = string.Empty;
    private string _targetEventId = string.Empty;
    private string _targetEventTitle = string.Empty;
    private DateTime _targetEventDate;
    private CrossReferenceType _relationshipType;
    private double _confidence;
    private string? _reasoning;

    public string SourceEventId
    {
        get => _sourceEventId;
        set => SetProperty(ref _sourceEventId, value);
    }

    public string TargetEventId
    {
        get => _targetEventId;
        set => SetProperty(ref _targetEventId, value);
    }

    public string TargetEventTitle
    {
        get => _targetEventTitle;
        set => SetProperty(ref _targetEventTitle, value);
    }

    public DateTime TargetEventDate
    {
        get => _targetEventDate;
        set => SetProperty(ref _targetEventDate, value);
    }

    public CrossReferenceType RelationshipType
    {
        get => _relationshipType;
        set => SetProperty(ref _relationshipType, value);
    }

    public double Confidence
    {
        get => _confidence;
        set => SetProperty(ref _confidence, value);
    }

    public string? Reasoning
    {
        get => _reasoning;
        set => SetProperty(ref _reasoning, value);
    }

    public string ConfidenceDisplay => $"{Confidence:P0}";

    public string TargetDateDisplay => TargetEventDate.ToString("MMM d, yyyy");

    public string RelationshipIcon => RelationshipType switch
    {
        CrossReferenceType.Causal => "🔗",
        CrossReferenceType.Thematic => "📌",
        CrossReferenceType.Temporal => "⏰",
        CrossReferenceType.Participatory => "👥",
        CrossReferenceType.Locational => "📍",
        CrossReferenceType.Consequential => "➡️",
        _ => "●"
    };

    public string RelationshipTypeDisplay => RelationshipType.ToString();
}

/// <summary>
/// DTO for displaying tag suggestions.
/// </summary>
public class TagSuggestionDto : ObservableObject
{
    private string _tagName = string.Empty;
    private double _confidence;
    private string? _reason;

    public string TagName
    {
        get => _tagName;
        set => SetProperty(ref _tagName, value);
    }

    public double Confidence
    {
        get => _confidence;
        set => SetProperty(ref _confidence, value);
    }

    public string? Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    public string ConfidenceDisplay => $"{Confidence:P0}";
}
