using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for Retrieval-Augmented Generation operations.
/// </summary>
public interface IRagService
{
    /// <summary>
    /// Finds similar events based on embedding similarity.
    /// </summary>
    /// <param name="eventId">Event ID to find similar events for</param>
    /// <param name="topK">Number of similar events to return</param>
    /// <param name="threshold">Minimum cosine similarity; null uses the
    /// <c>rag_similarity_threshold</c> setting (seeded 0.75), which is what
    /// callers should pass unless they have a reason of their own.</param>
    /// <returns>List of similar events with similarity scores</returns>
    Task<List<SimilarEvent>> FindSimilarEventsAsync(string eventId, int topK = 10, double? threshold = null);

    /// <summary>
    /// Detects cross-references between events using LLM analysis.
    /// </summary>
    /// <param name="eventId">Event ID to analyze</param>
    /// <param name="candidateEventIds">Candidate event IDs to check for relationships</param>
    /// <returns>List of detected cross-references</returns>
    Task<List<CrossReferenceResult>> DetectCrossReferencesAsync(string eventId, IEnumerable<string>? candidateEventIds = null);

    /// <summary>
    /// Detects patterns in events (recurring categories, temporal clusters, etc.).
    /// </summary>
    /// <param name="startDate">Start date for pattern analysis</param>
    /// <param name="endDate">End date for pattern analysis</param>
    /// <returns>Detected patterns</returns>
    Task<PatternAnalysisResult> DetectPatternsAsync(DateTime? startDate = null, DateTime? endDate = null);

    /// <summary>
    /// Suggests tags for an event based on similar events.
    /// </summary>
    /// <param name="eventId">Event ID</param>
    /// <param name="maxSuggestions">Maximum number of suggestions</param>
    /// <returns>List of suggested tags with confidence scores</returns>
    Task<List<TagSuggestion>> SuggestTagsAsync(string eventId, int maxSuggestions = 5);

    /// <summary>
    /// Suggests tags for an event based on its text content (before creation).
    /// </summary>
    /// <param name="title">Event title</param>
    /// <param name="description">Event description</param>
    /// <param name="maxSuggestions">Maximum number of suggestions</param>
    /// <returns>List of suggested tags with confidence scores</returns>
    Task<List<TagSuggestion>> SuggestTagsForTextAsync(string title, string? description, int maxSuggestions = 5);
}

/// <summary>
/// Similar event with similarity score.
/// </summary>
public class SimilarEvent
{
    public string EventId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime StartDate { get; set; }
    public double Similarity { get; set; }
}

/// <summary>
/// Cross-reference relationship between events.
/// </summary>
public class CrossReferenceResult
{
    public string SourceEventId { get; set; } = string.Empty;
    public string TargetEventId { get; set; } = string.Empty;
    public CrossReferenceType RelationshipType { get; set; }
    public double Confidence { get; set; }
    public string? Reasoning { get; set; }
}

/// <summary>
/// Types of cross-reference relationships.
/// </summary>
public enum CrossReferenceType
{
    Causal,         // One event caused another
    Thematic,       // Events share a common theme
    Temporal,       // Events are related by time proximity
    Participatory,  // Same people involved
    Locational,     // Same location
    Consequential   // One event is a consequence of another
}

/// <summary>
/// Pattern analysis result.
/// </summary>
public class PatternAnalysisResult
{
    /// <summary>
    /// Recurring category patterns.
    /// </summary>
    public List<CategoryPattern> CategoryPatterns { get; set; } = new();

    /// <summary>
    /// Temporal clusters (groups of events in time).
    /// </summary>
    public List<TemporalCluster> TemporalClusters { get; set; } = new();

    /// <summary>
    /// Detected era transitions.
    /// </summary>
    public List<EraTransition> EraTransitions { get; set; } = new();
}

/// <summary>
/// Category pattern (recurring category over time).
/// </summary>
public class CategoryPattern
{
    public string Category { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public double Frequency { get; set; } // Events per month
    public List<string> CommonTags { get; set; } = new();
}

/// <summary>
/// Temporal cluster of events.
/// </summary>
public class TemporalCluster
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int EventCount { get; set; }
    public string? Theme { get; set; }
    public List<string> EventIds { get; set; } = new();
}

/// <summary>
/// Era transition point.
/// </summary>
public class EraTransition
{
    public DateTime TransitionDate { get; set; }
    public string? FromTheme { get; set; }
    public string? ToTheme { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Tag suggestion with confidence.
/// </summary>
public class TagSuggestion
{
    public string TagName { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string? Reason { get; set; }
}
