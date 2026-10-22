using MemoryTimeline.Core.Models;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Narrative generation implementation.
///
/// Pipeline: (a) resolve the requested scope to events (chronological);
/// (b) partition into sections of at most ~40 events, preferring calendar-year
/// boundaries (era/person scopes spanning years get strictly per-year
/// sections); (c) one <see cref="ILlmService.CompleteAsync"/> call per section,
/// feeding each event as a single line whose date is the honest
/// <see cref="DateDisplay.FormatPrecise"/> text (a Year-precision memory reads
/// "1998", never a fabricated day); (d) when there is more than one section, a
/// single connective pass writes 1-2 sentence bridges and an overall title
/// (JSON, parsed defensively); (e) citation integrity is enforced in code:
/// any [event:ID] marker whose id is not in the section's source set is
/// stripped, and IncludeCitations=false strips them all.
///
/// Zero-event scopes return an empty narrative without any LLM call.
/// Cancellation is checked between sections and propagates as
/// <see cref="OperationCanceledException"/> — a cancelled generation returns
/// nothing, and since export happens strictly after generation no partial
/// file can result.
/// </summary>
public class NarrativeService : INarrativeService
{
    /// <summary>Maximum number of events fed to a single section call.</summary>
    private const int MaxEventsPerSection = 40;

    private const int MinSectionWords = 150;
    private const int MaxSectionWords = 800;
    private const int ConnectiveMaxTokens = 1500;
    private const int MaxDescriptionChars = 600;

    private readonly ILlmService _llmService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<NarrativeService> _logger;

    // Reused serializer options (allocating per call is expensive).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Same marker shape as the Ask feature; integrity is enforced in code.
    private static readonly Regex CitationMarkerRegex = new(@"\[event:([^\]\s]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// The grounding rules sent verbatim with every section call. These are
    /// the correctness requirement of the feature — do not soften them.
    /// </summary>
    private const string GroundingRules = """
        Grounding rules (each one is a hard correctness requirement):
        1. Use ONLY the supplied memories. Invent nothing: no weather, no dialogue, no unstated emotions, and no outside knowledge.
        2. Each memory's date text is already rounded to what is actually known. Never state a date more precisely than its date text: a memory dated only "1998" happened "sometime that year" — never give it a month or a day.
        3. Mark gaps honestly: when time passes between memories with nothing recorded, say so plainly instead of filling the gap.
        4. Cite the supporting memory inline after each statement with the marker [event:ID], using the exact ID from the supplied lines.
        """;

    public NarrativeService(
        ILlmService llmService,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<NarrativeService> logger)
    {
        _llmService = llmService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Narrative> GenerateAsync(
        NarrativeRequest request,
        IProgress<(int pct, string stage)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        progress?.Report((5, "Gathering memories…"));
        var (events, scopeLabel) = await ResolveScopeAsync(request, ct);
        ct.ThrowIfCancellationRequested();

        if (events.Count == 0)
        {
            // Nothing to tell: return an honest empty narrative, no LLM call.
            _logger.LogInformation(
                "Narrative scope {Scope} ({Label}) resolved to zero events; skipping generation",
                request.Scope, scopeLabel);
            progress?.Report((100, "No memories found"));

            return new Narrative
            {
                Title = BuildFallbackTitle(scopeLabel),
                Scope = request.Scope,
                ScopeLabel = scopeLabel,
                GeneratedAt = DateTime.UtcNow,
                Voice = request.Voice,
                Sections = new List<NarrativeSection>(),
                SourceEventIds = new List<string>(),
                IncludeMedia = request.IncludeMedia
            };
        }

        _logger.LogInformation(
            "Generating narrative for scope {Scope} ({Label}): {Count} events, voice {Voice}",
            request.Scope, scopeLabel, events.Count, request.Voice);

        var chunks = PartitionIntoSections(events, request.Scope);
        var headings = BuildSectionHeadings(chunks, scopeLabel);
        var targetPerSection = Math.Clamp(
            Math.Max(1, request.TargetWords) / chunks.Count, MinSectionWords, MaxSectionWords);

        var sections = new List<NarrativeSection>();
        for (var i = 0; i < chunks.Count; i++)
        {
            // Cancellation is checked between sections so a cancelled run
            // never produces a half-written narrative.
            ct.ThrowIfCancellationRequested();
            progress?.Report((10 + (int)(78.0 * i / chunks.Count), $"Writing section {i + 1} of {chunks.Count}…"));

            var prose = await GenerateSectionProseAsync(request.Voice, headings[i], chunks[i], targetPerSection, ct);
            sections.Add(new NarrativeSection
            {
                Heading = headings[i],
                Prose = prose,
                SourceEventIds = chunks[i].Select(e => e.EventId).ToList()
            });
        }

        var title = BuildFallbackTitle(scopeLabel);
        if (sections.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((90, "Weaving the sections together…"));
            title = await ApplyConnectivePassAsync(sections, scopeLabel, ct);
        }

        // Citation integrity runs LAST (after bridges are prepended) so no
        // path can smuggle a marker past the source-set check.
        foreach (var section in sections)
        {
            section.Prose = ApplyCitationRules(section.Prose, section.SourceEventIds, request.IncludeCitations);
        }

        progress?.Report((100, "Story ready"));

        return new Narrative
        {
            Title = title,
            Scope = request.Scope,
            ScopeLabel = scopeLabel,
            GeneratedAt = DateTime.UtcNow,
            Voice = request.Voice,
            Sections = sections,
            SourceEventIds = events.Select(e => e.EventId).ToList(),
            IncludeMedia = request.IncludeMedia
        };
    }

    #region Scope resolution

    private async Task<(List<Event> Events, string? ScopeLabel)> ResolveScopeAsync(
        NarrativeRequest request, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        IQueryable<Event> query = context.Events
            .Include(e => e.EventPeople).ThenInclude(ep => ep.Person)
            .Include(e => e.EventLocations).ThenInclude(el => el.Location)
            .AsNoTracking();

        string? scopeLabel;

        switch (request.Scope)
        {
            case NarrativeScope.Era:
            {
                RequireScopeId(request);
                var era = await context.Eras.AsNoTracking()
                        .FirstOrDefaultAsync(er => er.EraId == request.ScopeId, ct)
                    ?? throw new InvalidOperationException($"Era not found: {request.ScopeId}");

                scopeLabel = era.Name;

                // An event belongs to the era's story when it is explicitly
                // assigned (EraId) OR when it overlaps the era's date range.
                var eraId = era.EraId;
                var eraStart = era.StartDate;
                var eraEnd = era.EndDate ?? DateTime.MaxValue;
                query = query.Where(e => e.EraId == eraId
                    || (e.StartDate <= eraEnd && (e.EndDate ?? e.StartDate) >= eraStart));
                break;
            }

            case NarrativeScope.Person:
            {
                RequireScopeId(request);
                var person = await context.People.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.PersonId == request.ScopeId, ct)
                    ?? throw new InvalidOperationException($"Person not found: {request.ScopeId}");

                scopeLabel = person.Name;
                var personId = person.PersonId;
                query = query.Where(e => e.EventPeople.Any(ep => ep.PersonId == personId));
                break;
            }

            case NarrativeScope.Tag:
            {
                RequireScopeId(request);
                var tag = await context.Tags.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.TagId == request.ScopeId, ct)
                    ?? throw new InvalidOperationException($"Tag not found: {request.ScopeId}");

                scopeLabel = tag.TagName;
                var tagId = tag.TagId;
                query = query.Where(e => e.EventTags.Any(et => et.TagId == tagId));
                break;
            }

            case NarrativeScope.Selection:
            {
                if (request.SelectedEventIds == null)
                {
                    throw new ArgumentException("Selection scope requires SelectedEventIds.", nameof(request));
                }

                var ids = request.SelectedEventIds;
                query = query.Where(e => ids.Contains(e.EventId));
                scopeLabel = null; // finalized below from the resolved count
                break;
            }

            case NarrativeScope.DateRange:
            default:
            {
                if (!request.From.HasValue || !request.To.HasValue)
                {
                    throw new ArgumentException("Date-range scope requires both From and To.", nameof(request));
                }

                var from = request.From.Value;
                var to = request.To.Value;
                if (to < from)
                {
                    (from, to) = (to, from);
                }

                scopeLabel = FormatRangeLabel(from, to);

                // Overlap semantics matching EventRepository.GetByDateRangeAsync:
                // a duration event counts when any part of it touches the range.
                query = query.Where(e => e.StartDate <= to && (e.EndDate ?? e.StartDate) >= from);
                break;
            }
        }

        var events = await query.ToListAsync(ct);

        // Chronological order with a deterministic tie-break.
        events = events
            .OrderBy(e => e.StartDate)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ToList();

        if (request.Scope == NarrativeScope.Selection)
        {
            scopeLabel = events.Count == 1 ? "1 selected memory" : $"{events.Count} selected memories";
        }

        return (events, scopeLabel);
    }

    private static void RequireScopeId(NarrativeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScopeId))
        {
            throw new ArgumentException($"{request.Scope} scope requires ScopeId.", nameof(request));
        }
    }

    private static string FormatRangeLabel(DateTime from, DateTime to)
    {
        var culture = CultureInfo.InvariantCulture;
        return $"{from.ToString("d MMM yyyy", culture)} – {to.ToString("d MMM yyyy", culture)}";
    }

    #endregion

    #region Chunking

    /// <summary>
    /// Partitions chronologically ordered events into sections of at most
    /// <see cref="MaxEventsPerSection"/> events. Small scopes stay a single
    /// section. Larger scopes split on calendar-year boundaries (a year with
    /// too many events is subdivided into near-equal consecutive runs). For
    /// Era and Person scopes the per-year sections are kept as-is; other
    /// scopes merge adjacent small sections back together while staying under
    /// the cap, so year boundaries are preferred but not fragmenting.
    /// </summary>
    private static List<List<Event>> PartitionIntoSections(List<Event> events, NarrativeScope scope)
    {
        if (events.Count <= MaxEventsPerSection)
        {
            return new List<List<Event>> { events };
        }

        var yearGroups = events
            .GroupBy(e => e.StartDate.Year)
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();

        // Subdivide any year that alone exceeds the cap.
        var chunks = new List<List<Event>>();
        foreach (var group in yearGroups)
        {
            if (group.Count <= MaxEventsPerSection)
            {
                chunks.Add(group);
                continue;
            }

            var parts = (group.Count + MaxEventsPerSection - 1) / MaxEventsPerSection;
            var baseSize = group.Count / parts;
            var remainder = group.Count % parts;
            var index = 0;
            for (var p = 0; p < parts; p++)
            {
                var size = baseSize + (p < remainder ? 1 : 0);
                chunks.Add(group.GetRange(index, size));
                index += size;
            }
        }

        if (scope is NarrativeScope.Era or NarrativeScope.Person)
        {
            // A life phase or a relationship reads naturally year by year.
            return chunks;
        }

        // Merge adjacent chunks while staying under the cap (may cross year
        // boundaries — "preferably" by year, not fanatically).
        var merged = new List<List<Event>>();
        foreach (var chunk in chunks)
        {
            if (merged.Count > 0 && merged[^1].Count + chunk.Count <= MaxEventsPerSection)
            {
                merged[^1].AddRange(chunk);
            }
            else
            {
                merged.Add(new List<Event>(chunk));
            }
        }

        return merged;
    }

    /// <summary>
    /// Deterministic headings: the scope label for a single section, else the
    /// chunk's year (or year span), with duplicate headings numbered
    /// ("2019 — part 1 of 3"). Chunks whose events are ALL coarser than Year
    /// precision never use raw anchor years (see <see cref="BuildChunkHeading"/>) —
    /// the heading is fed into the prompt as "# Section: {heading}" and would
    /// otherwise leak precision that <see cref="BuildEventLine"/> deliberately
    /// hides via <see cref="DateDisplay.FormatPrecise"/>.
    /// </summary>
    private static List<string> BuildSectionHeadings(List<List<Event>> chunks, string? scopeLabel)
    {
        if (chunks.Count == 1)
        {
            return new List<string> { string.IsNullOrWhiteSpace(scopeLabel) ? "The story" : scopeLabel };
        }

        var culture = CultureInfo.InvariantCulture;
        var fallbackLabel = string.IsNullOrWhiteSpace(scopeLabel) ? "The story" : scopeLabel;
        var raw = chunks.Select(chunk => BuildChunkHeading(chunk, fallbackLabel, culture)).ToList();

        var totals = raw.GroupBy(h => h, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            if (totals.TryGetValue(raw[i], out var total))
            {
                seen[raw[i]] = seen.GetValueOrDefault(raw[i]) + 1;
                raw[i] = $"{raw[i]} — part {seen[raw[i]]} of {total}";
            }
        }

        return raw;
    }

    /// <summary>
    /// Heading for one chunk. A chunk with at least one Year-or-finer event
    /// keeps the anchor-year heading (single year, or "minYear – maxYear").
    /// A chunk containing ONLY coarser-than-Year events must not leak the raw
    /// anchor years its own event lines hide: Decade-precision chunks read as
    /// the honest decade label ("1990s", or "1990s – 2000s" for a span), and
    /// a chunk of only Unknown-precision events (whose anchors are
    /// placeholders) falls back to the scope label.
    /// </summary>
    private static string BuildChunkHeading(List<Event> chunk, string fallbackLabel, CultureInfo culture)
    {
        if (chunk.Any(e => e.DatePrecision <= DatePrecision.Year))
        {
            var minYear = chunk[0].StartDate.Year;
            var maxYear = chunk[^1].StartDate.Year;
            return minYear == maxYear
                ? minYear.ToString(culture)
                : $"{minYear.ToString(culture)} – {maxYear.ToString(culture)}";
        }

        // Only Decade/Unknown precision left. Unknown anchors carry no usable
        // date, so the heading comes from the coarsest DATED precision present.
        var dated = chunk.Where(e => e.DatePrecision != DatePrecision.Unknown).ToList();
        if (dated.Count == 0)
        {
            return fallbackLabel;
        }

        var coarsest = dated.Max(e => e.DatePrecision);
        var first = DateDisplay.FormatPrecise(dated[0].StartDate, coarsest);
        var last = DateDisplay.FormatPrecise(dated[^1].StartDate, coarsest);
        return first == last ? first : $"{first} – {last}";
    }

    #endregion

    #region Section generation

    private async Task<string> GenerateSectionProseAsync(
        NarrativeVoice voice,
        string heading,
        List<Event> events,
        int targetWords,
        CancellationToken ct)
    {
        var systemPrompt = $"""
            You are writing one section of a personal life story from a person's own recorded memories.
            {GetVoiceInstruction(voice)}
            {GroundingRules}
            Write approximately {targetWords} words of connected prose in short paragraphs separated by blank lines. Do not use headings, bullet points, or lists. Return ONLY the prose text.
            """;

        var lines = new StringBuilder();
        foreach (var evt in events)
        {
            lines.AppendLine(BuildEventLine(evt));
        }

        var userPrompt = $"""
            # Memories (the only allowed sources), in chronological order:
            {lines.ToString().TrimEnd()}

            # Section: {heading}
            Write this section of the story now.
            """;

        // ~2 tokens per English word is a comfortable ceiling for the prose.
        var maxTokens = Math.Clamp(targetWords * 3, 600, 3000);

        var response = await _llmService.CompleteAsync(systemPrompt, userPrompt, maxTokens, ct);
        return StripCodeFences(response);
    }

    private static string GetVoiceInstruction(NarrativeVoice voice)
    {
        return voice switch
        {
            NarrativeVoice.Factual => "Write in a plain, factual, documentary voice: concrete statements, no sentimentality, no speculation.",
            NarrativeVoice.Warm => "Write in a warm, affectionate voice, as if lovingly retelling these memories to family.",
            _ => "Write in a thoughtful, reflective voice, as a person quietly looking back on this chapter of their life."
        };
    }

    /// <summary>
    /// One prompt line per event. The date is the honest
    /// <see cref="DateDisplay.FormatPrecise"/> text — never the raw anchor
    /// date — so the model cannot see (and therefore cannot state) more
    /// precision than the event actually has.
    /// </summary>
    private static string BuildEventLine(Event evt)
    {
        var dateText = DateDisplay.FormatPrecise(evt.StartDate, evt.DatePrecision, evt.EndDate);

        var line = new StringBuilder();
        line.Append("[event:").Append(evt.EventId).Append("] ");
        line.Append(dateText).Append(" — ").Append(evt.Title);

        if (!string.IsNullOrWhiteSpace(evt.Description))
        {
            line.Append(" — ").Append(Condense(evt.Description));
        }

        var people = evt.EventPeople
            .Select(ep => ep.Person?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var location = !string.IsNullOrWhiteSpace(evt.Location)
            ? evt.Location
            : evt.EventLocations
                .Select(el => el.Location?.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        var extras = new List<string>();
        if (people.Count > 0)
        {
            extras.Add("People: " + string.Join(", ", people));
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            extras.Add("Location: " + location);
        }

        if (extras.Count > 0)
        {
            line.Append(" (").Append(string.Join("; ", extras)).Append(')');
        }

        return line.ToString();
    }

    /// <summary>Flattens newlines and truncates long descriptions for the prompt.</summary>
    private static string Condense(string text)
    {
        var flattened = Regex.Replace(text, @"\s+", " ").Trim();
        return flattened.Length <= MaxDescriptionChars
            ? flattened
            : flattened[..MaxDescriptionChars].TrimEnd() + "…";
    }

    #endregion

    #region Connective pass

    /// <summary>
    /// One extra LLM call over the finished sections asking ONLY for an
    /// overall title and a 1-2 sentence bridge leading into each section
    /// after the first. Parsed defensively: any failure keeps the section
    /// headings as-is and falls back to a deterministic scope-based title.
    /// Returns the title to use.
    /// </summary>
    private async Task<string> ApplyConnectivePassAsync(
        List<NarrativeSection> sections, string? scopeLabel, CancellationToken ct)
    {
        var fallbackTitle = BuildFallbackTitle(scopeLabel);
        var expectedBridges = sections.Count - 1;

        var systemPrompt = $$"""
            You are given the numbered sections of a personal life story. Write ONLY:
            (a) an overall title for the whole story, and
            (b) for each section AFTER the first, a bridge of one or two sentences that leads from the previous section into it.
            Do not rewrite or summarize the sections and do not invent facts that are not already in them.
            Return ONLY a JSON object with this exact structure (no markdown fences, no commentary):
            {
              "title": "the overall title",
              "bridges": ["bridge into section 2", "bridge into section 3", ...]
            }
            The bridges array must have exactly {{expectedBridges}} entries.
            """;

        var body = new StringBuilder();
        for (var i = 0; i < sections.Count; i++)
        {
            body.AppendLine($"## Section {i + 1}: {sections[i].Heading}");
            body.AppendLine(sections[i].Prose);
            body.AppendLine();
        }

        var userPrompt = $"# Sections:\n{body.ToString().TrimEnd()}";

        try
        {
            var response = await _llmService.CompleteAsync(systemPrompt, userPrompt, ConnectiveMaxTokens, ct);
            var parsed = JsonSerializer.Deserialize<ConnectiveResponse>(StripCodeFences(response), JsonOptions);

            if (parsed == null)
            {
                _logger.LogWarning("Connective pass returned null JSON; keeping section headings and fallback title");
                return fallbackTitle;
            }

            var bridges = parsed.Bridges ?? new List<string>();
            for (var i = 0; i < Math.Min(bridges.Count, expectedBridges); i++)
            {
                var bridge = bridges[i]?.Trim();
                if (string.IsNullOrWhiteSpace(bridge))
                {
                    continue;
                }

                sections[i + 1].Prose = bridge + "\n\n" + sections[i + 1].Prose;
            }

            return string.IsNullOrWhiteSpace(parsed.Title) ? fallbackTitle : parsed.Title.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed connective-pass JSON; keeping section headings and fallback title");
            return fallbackTitle;
        }
        catch (Exception ex)
        {
            // The sections themselves are already generated; a failed
            // connective pass degrades to the deterministic title rather
            // than losing the whole narrative.
            _logger.LogWarning(ex, "Connective pass failed; keeping section headings and fallback title");
            return fallbackTitle;
        }
    }

    private static string BuildFallbackTitle(string? scopeLabel)
    {
        return string.IsNullOrWhiteSpace(scopeLabel) ? "Your story" : $"The story of {scopeLabel}";
    }

    #endregion

    #region Citation integrity

    /// <summary>
    /// Enforces the same integrity rule as Ask-your-timeline: a marker only
    /// survives when its id is in the section's source set (the model cannot
    /// cite an event it was not given), and IncludeCitations=false strips all
    /// markers. Whitespace/punctuation is tidied after removal.
    /// </summary>
    private static string ApplyCitationRules(string prose, List<string> sourceEventIds, bool includeCitations)
    {
        var allowed = new HashSet<string>(sourceEventIds, StringComparer.Ordinal);

        var cleaned = CitationMarkerRegex.Replace(prose, match =>
            includeCitations && allowed.Contains(match.Groups[1].Value)
                ? match.Value
                : string.Empty);

        cleaned = Regex.Replace(cleaned, " {2,}", " ");
        cleaned = Regex.Replace(cleaned, " +([.,!?;:])", "$1");
        return cleaned.Trim();
    }

    #endregion

    #region Helpers

    private static string StripCodeFences(string text)
    {
        var t = text.Trim();

        if (t.StartsWith("```json", StringComparison.Ordinal))
        {
            t = t.Substring(7);
        }
        else if (t.StartsWith("```", StringComparison.Ordinal))
        {
            t = t.Substring(3);
        }

        if (t.EndsWith("```", StringComparison.Ordinal))
        {
            t = t.Substring(0, t.Length - 3);
        }

        return t.Trim();
    }

    #endregion

    #region Response models

    /// <summary>Structured output of the connective pass.</summary>
    private sealed class ConnectiveResponse
    {
        public string? Title { get; set; }
        public List<string>? Bridges { get; set; }
    }

    #endregion
}
