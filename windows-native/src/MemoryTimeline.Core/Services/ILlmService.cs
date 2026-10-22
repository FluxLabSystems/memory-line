namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for Large Language Model operations.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Extracts events from a transcript text.
    /// </summary>
    /// <param name="transcript">The transcript text to analyze</param>
    /// <returns>Extraction result with detected events</returns>
    Task<EventExtractionResult> ExtractEventsAsync(string transcript);

    /// <summary>
    /// Extracts events from a transcript with additional context.
    /// </summary>
    /// <param name="transcript">The transcript text to analyze</param>
    /// <param name="context">Additional context (e.g., existing events, tags)</param>
    /// <param name="cancellationToken">
    /// Aborts the in-flight model call; propagates as
    /// <see cref="OperationCanceledException"/>, never as a failed result.
    /// </param>
    /// <returns>Extraction result with detected events</returns>
    Task<EventExtractionResult> ExtractEventsAsync(string transcript, ExtractionContext? context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a single free-form completion against the configured model.
    /// Used by conversational features (e.g. "Ask your timeline") that need raw
    /// text back rather than the structured extraction result.
    /// </summary>
    /// <param name="systemPrompt">System-level instructions for the model</param>
    /// <param name="userPrompt">The user-turn content</param>
    /// <param name="maxTokens">Maximum tokens for the response</param>
    /// <param name="ct">Cancellation token honored at the call boundaries</param>
    /// <returns>The model's text response (raw, untrimmed of code fences)</returns>
    /// <exception cref="ConfigurationException">No API key is configured</exception>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2000, CancellationToken ct = default);

    /// <summary>
    /// Streams a single free-form completion as the model writes it. Each
    /// item is the next piece of response text (a token or a few), in order;
    /// concatenating every item gives the untrimmed text that
    /// <see cref="CompleteAsync"/> would have returned for the same prompts.
    /// Used by "Ask your timeline" so an answer appears while it is being
    /// generated instead of arriving whole.
    /// </summary>
    /// <param name="systemPrompt">System-level instructions for the model</param>
    /// <param name="userPrompt">The user-turn content</param>
    /// <param name="maxTokens">Maximum tokens for the response</param>
    /// <param name="ct">Cancellation token; cancelling aborts the in-flight stream</param>
    /// <exception cref="ConfigurationException">No API key / endpoint is configured</exception>
    /// <exception cref="InvalidOperationException">The stream ended without any text</exception>
    IAsyncEnumerable<string> StreamCompletionAsync(string systemPrompt, string userPrompt, int maxTokens = 2000, CancellationToken ct = default);

    /// <summary>
    /// Gets the name of the LLM provider.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets the model name being used.
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// Gets whether the service requires internet connectivity.
    /// </summary>
    bool RequiresInternet { get; }
}

/// <summary>
/// Context for event extraction to improve accuracy.
/// </summary>
public class ExtractionContext
{
    /// <summary>
    /// Recent events for temporal context.
    /// </summary>
    public List<string>? RecentEvents { get; set; }

    /// <summary>
    /// Available tags for consistency.
    /// </summary>
    public List<string>? AvailableTags { get; set; }

    /// <summary>
    /// People mentioned in recent events.
    /// </summary>
    public List<string>? KnownPeople { get; set; }

    /// <summary>
    /// Locations from recent events.
    /// </summary>
    public List<string>? KnownLocations { get; set; }

    /// <summary>
    /// Current date for relative date parsing.
    /// </summary>
    public DateTime? ReferenceDate { get; set; }
}

/// <summary>
/// Result of event extraction from transcript.
/// </summary>
public class EventExtractionResult
{
    /// <summary>
    /// List of extracted events.
    /// </summary>
    public List<ExtractedEvent> Events { get; set; } = new();

    /// <summary>
    /// Overall confidence score (0.0 to 1.0).
    /// </summary>
    public double OverallConfidence { get; set; }

    /// <summary>
    /// Whether the extraction was successful.
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error message if extraction failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Processing duration in seconds.
    /// </summary>
    public double ProcessingDurationSeconds { get; set; }

    /// <summary>
    /// Token usage for cost tracking.
    /// </summary>
    public TokenUsage? TokenUsage { get; set; }
}

/// <summary>
/// An extracted event from the transcript.
/// </summary>
public class ExtractedEvent
{
    /// <summary>
    /// Event title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Event description/details.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Start date of the event.
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End date for duration events.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Raw date-precision string from the model
    /// (exact|day|month|season|year|decade|unknown). Null on payloads from
    /// older prompts; parse defensively with
    /// <c>MemoryTimeline.Data.Models.DatePrecisionParser.Parse</c>.
    /// </summary>
    public string? DatePrecision { get; set; }

    /// <summary>
    /// Event category.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Suggested tags.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// People involved.
    /// </summary>
    public List<string> People { get; set; } = new();

    /// <summary>
    /// Per-person details (relationship, extra context) for the people involved.
    /// Optional richer companion to the flat <see cref="People"/> list, which is
    /// kept for compatibility.
    /// </summary>
    public List<ExtractedPersonDetail>? PeopleDetails { get; set; }

    /// <summary>
    /// Locations mentioned.
    /// </summary>
    public List<string> Locations { get; set; } = new();

    /// <summary>
    /// Confidence score for this event (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Portion of transcript that mentions this event.
    /// </summary>
    public string? SourceText { get; set; }

    /// <summary>
    /// Reasoning for extraction (for debugging/review).
    /// </summary>
    public string? Reasoning { get; set; }
}

/// <summary>
/// Per-person detail extracted from a transcript alongside the flat people list.
/// </summary>
public class ExtractedPersonDetail
{
    /// <summary>
    /// Person name as mentioned in the transcript.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Relationship to the speaker (e.g. "sister", "coworker"), when stated.
    /// </summary>
    public string? Relationship { get; set; }

    /// <summary>
    /// Other noteworthy details mentioned about the person.
    /// </summary>
    public string? Details { get; set; }
}

/// <summary>
/// Token usage information for API calls.
/// </summary>
public class TokenUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal? EstimatedCost { get; set; }
}
