namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for narrative generation: turning a scope of timeline
/// events (an era, a person, a tag, a date range, or an explicit selection)
/// into connected prose a person would actually read, with every paragraph
/// traceable back to its source events.
/// </summary>
public interface INarrativeService
{
    /// <summary>
    /// Generates a narrative for the requested scope.
    ///
    /// Grounding is the correctness requirement: the model is given ONLY the
    /// resolved events (each rendered at its honest date precision) and the
    /// output is post-processed so citation markers can never reference an
    /// event outside the section's source set.
    ///
    /// A scope that resolves to zero events returns an empty
    /// <see cref="Narrative"/> (clear <see cref="Narrative.ScopeLabel"/>, no
    /// sections) WITHOUT any LLM call. Cancellation throws
    /// <see cref="OperationCanceledException"/> and returns nothing — callers
    /// export only after a completed generation, so no partial file is ever
    /// written.
    /// </summary>
    /// <param name="request">Scope, voice, and output options</param>
    /// <param name="progress">Optional (percent, stage-description) progress sink</param>
    /// <param name="ct">Cancellation token, checked between LLM calls</param>
    /// <exception cref="ConfigurationException">No LLM API key is configured</exception>
    /// <exception cref="OperationCanceledException">The generation was cancelled</exception>
    Task<Narrative> GenerateAsync(
        NarrativeRequest request,
        IProgress<(int pct, string stage)>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// What slice of the timeline a narrative is generated from.
/// </summary>
public enum NarrativeScope
{
    /// <summary>Every event overlapping an explicit From..To date range.</summary>
    DateRange = 0,

    /// <summary>Events belonging to an era (by era id) or falling inside its date range.</summary>
    Era = 1,

    /// <summary>Events linked to a person via the event_people junction.</summary>
    Person = 2,

    /// <summary>Events linked to a tag via the event_tags junction.</summary>
    Tag = 3,

    /// <summary>An explicit list of event ids.</summary>
    Selection = 4
}

/// <summary>
/// The writing voice for a generated narrative.
/// </summary>
public enum NarrativeVoice
{
    /// <summary>Thoughtful, looking-back-on-life tone (default).</summary>
    Reflective = 0,

    /// <summary>Plain documentary tone; no emotional language.</summary>
    Factual = 1,

    /// <summary>Affectionate, telling-stories-to-family tone.</summary>
    Warm = 2
}

/// <summary>
/// Parameters for a narrative generation run.
/// </summary>
public class NarrativeRequest
{
    /// <summary>The kind of scope to gather events from.</summary>
    public NarrativeScope Scope { get; set; }

    /// <summary>
    /// Era/person/tag id for the corresponding scopes; unused for
    /// DateRange and Selection.
    /// </summary>
    public string? ScopeId { get; set; }

    /// <summary>Lower date bound (required for DateRange scope).</summary>
    public DateTime? From { get; set; }

    /// <summary>Upper date bound (required for DateRange scope).</summary>
    public DateTime? To { get; set; }

    /// <summary>The writing voice.</summary>
    public NarrativeVoice Voice { get; set; } = NarrativeVoice.Reflective;

    /// <summary>Approximate total length of the narrative, in words.</summary>
    public int TargetWords { get; set; } = 1200;

    /// <summary>
    /// When true, valid inline [event:ID] markers are kept in the prose;
    /// when false all markers are stripped. Markers whose id is not in the
    /// section's source set are always stripped regardless.
    /// </summary>
    public bool IncludeCitations { get; set; } = true;

    /// <summary>
    /// When true, the media attached to the narrative's source events
    /// (<c>event_media</c>) is referenced in the exported document: images as
    /// inline image references with their caption, other attachments as links.
    /// The flag is copied onto <see cref="Narrative.IncludeMedia"/> so the
    /// export knows the choice without a second request object. Generation
    /// itself never sees the media — the model is given text only.
    /// </summary>
    public bool IncludeMedia { get; set; } = true;

    /// <summary>Explicit event ids (required for Selection scope).</summary>
    public List<string>? SelectedEventIds { get; set; }
}

/// <summary>
/// A generated narrative: an ordered list of prose sections plus the full
/// provenance of which events fed it.
/// </summary>
public class Narrative
{
    /// <summary>Overall title of the story.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The scope kind this narrative was generated from.</summary>
    public NarrativeScope Scope { get; set; }

    /// <summary>
    /// Human-readable label of the scope (era name, person name, tag name,
    /// formatted date range, or selection count).
    /// </summary>
    public string? ScopeLabel { get; set; }

    /// <summary>UTC timestamp of the generation.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>The voice the narrative was written in.</summary>
    public NarrativeVoice Voice { get; set; }

    /// <summary>The prose sections in chronological order.</summary>
    public List<NarrativeSection> Sections { get; set; } = new();

    /// <summary>Every event id that fed the narrative, in chronological order.</summary>
    public List<string> SourceEventIds { get; set; } = new();

    /// <summary>
    /// Whether the export should reference the media attached to the source
    /// events (copied from <see cref="NarrativeRequest.IncludeMedia"/>).
    /// </summary>
    public bool IncludeMedia { get; set; }
}

/// <summary>
/// One section of a narrative, traceable to the exact events it was
/// generated from.
/// </summary>
public class NarrativeSection
{
    /// <summary>Section heading (year label or the scope label).</summary>
    public string Heading { get; set; } = string.Empty;

    /// <summary>The section's prose (with inline [event:ID] markers when citations are enabled).</summary>
    public string Prose { get; set; } = string.Empty;

    /// <summary>The event ids supplied to the model for this section.</summary>
    public List<string> SourceEventIds { get; set; } = new();
}
