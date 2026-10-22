using MemoryTimeline.Core.Models;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// "Ask your timeline" implementation.
///
/// Pipeline: (a) one LLM call turns the question into a small retrieval plan;
/// (b) hybrid retrieval — keyword/structured search via
/// <see cref="IAdvancedSearchService"/> plus semantic search over the stored
/// event_embeddings — merged with reciprocal rank fusion; (c) one LLM call
/// produces a grounded answer restricted to the retrieved events, with inline
/// [event:ID] citation markers; (d) citation integrity is enforced in code:
/// only ids that were actually retrieved become citations, and the raw markers
/// are replaced with [n] references matching the citation list order.
///
/// The semantic leg degrades gracefully: any embedding failure (typically a
/// missing embedding API key) falls back to keyword-only retrieval.
/// </summary>
public class MemoryQueryService : IMemoryQueryService
{
    private const int DefaultTopK = 12;
    private const int PlanMaxTokens = 1000;
    private const int AnswerMaxTokens = 2000;
    private const int RrfConstant = 60;

    private readonly ILlmService _llmService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IAdvancedSearchService _searchService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IPersonRepository _personRepository;
    private readonly ITagRepository _tagRepository;
    private readonly ILocationRepository _locationRepository;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MemoryQueryService> _logger;

    // Reused serializer options (allocating per call is expensive).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex CitationMarkerRegex = new(@"\[event:([^\]\s]+)\]", RegexOptions.Compiled);

    public MemoryQueryService(
        ILlmService llmService,
        IEmbeddingService embeddingService,
        IAdvancedSearchService searchService,
        IDbContextFactory<AppDbContext> contextFactory,
        IPersonRepository personRepository,
        ITagRepository tagRepository,
        ILocationRepository locationRepository,
        ISettingsService settingsService,
        ILogger<MemoryQueryService> logger)
    {
        _llmService = llmService;
        _embeddingService = embeddingService;
        _searchService = searchService;
        _contextFactory = contextFactory;
        _personRepository = personRepository;
        _tagRepository = tagRepository;
        _locationRepository = locationRepository;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<MemoryAnswer> AskAsync(string question, MemoryQueryOptions? options = null, CancellationToken ct = default)
        => AskCoreAsync(question, options, partialAnswer: null, ct);

    /// <inheritdoc />
    public Task<MemoryAnswer> AskAsync(string question, MemoryQueryOptions? options, IProgress<string> partialAnswer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partialAnswer);
        return AskCoreAsync(question, options, partialAnswer, ct);
    }

    /// <summary>
    /// The Ask pipeline. With a <paramref name="partialAnswer"/> sink the
    /// grounded answer is streamed from the provider and reported as display
    /// text while it arrives; without one it is requested whole. Both end in
    /// the same post-processing, so the returned <see cref="MemoryAnswer"/> is
    /// identical in shape either way.
    /// </summary>
    private async Task<MemoryAnswer> AskCoreAsync(
        string question,
        MemoryQueryOptions? options,
        IProgress<string>? partialAnswer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("Question must not be empty.", nameof(question));
        }

        ct.ThrowIfCancellationRequested();

        var topK = options?.TopK ?? await _settingsService.GetSettingAsync<int>(SettingKeys.AskTopK, DefaultTopK);
        if (topK <= 0)
        {
            topK = DefaultTopK;
        }

        var includeTranscripts = options?.IncludeTranscripts
            ?? await _settingsService.GetSettingAsync<bool>(SettingKeys.AskIncludeTranscripts, false);

        // (a) Query analysis: one LLM call producing a small retrieval plan.
        var plan = await BuildRetrievalPlanAsync(question, ct);

        var from = options?.From ?? TryParseDate(plan.From);
        var to = options?.To ?? TryParseDate(plan.To);

        // (b) Hybrid retrieval.
        var keywordRanking = await RunKeywordSearchAsync(plan, question, from, to, topK, ct);

        var semanticRanking = new List<string>();
        var usedSemantic = false;
        try
        {
            semanticRanking = await RunSemanticSearchAsync(plan.SemanticQuery ?? question, topK, ct);
            usedSemantic = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ConfigurationException (no embedding key), an
            // EmbeddingDimensionMismatchException (stored rows are from a
            // different provider - re-embed to restore semantic retrieval), or
            // any embedding failure: degrade to keyword-only retrieval instead
            // of failing the question. usedSemantic stays false, so the answer
            // metadata honestly reports that semantics contributed nothing.
            _logger.LogWarning(ex, "Semantic retrieval unavailable; degrading to keyword-only retrieval");
        }

        var fused = FuseRankings(new IReadOnlyList<string>[] { keywordRanking, semanticRanking });

        // Load a little more than TopK so the date filter (which the semantic
        // leg cannot apply up front) still leaves enough candidates.
        var candidateIds = fused.Take(Math.Max(topK * 2, topK)).ToList();
        var candidates = await LoadEventsAsync(candidateIds, ct);

        if (from.HasValue)
        {
            candidates = candidates.Where(e => (e.EndDate ?? e.StartDate) >= from.Value).ToList();
        }

        if (to.HasValue)
        {
            candidates = candidates.Where(e => e.StartDate <= to.Value).ToList();
        }

        var retrieved = candidates.Take(topK).ToList();
        var retrievedIds = retrieved.Select(e => e.EventId).ToList();

        if (retrieved.Count == 0)
        {
            // Nothing to ground an answer in: refuse honestly without an LLM call.
            _logger.LogInformation("Ask retrieval found no events for question; returning refusal");
            const string refusal = "I couldn't find anything about that in your timeline archive.";
            partialAnswer?.Report(refusal);
            return new MemoryAnswer
            {
                Question = question,
                Answer = refusal,
                AnsweredFromArchive = false,
                Confidence = 0,
                RetrievedEventIds = retrievedIds,
                UsedSemanticRetrieval = usedSemantic
            };
        }

        // (c) Grounded answer: one LLM call restricted to the retrieved events,
        // streamed when the caller wants to watch it arrive.
        var (systemPrompt, userPrompt) = BuildGroundedAnswerPrompts(question, retrieved, includeTranscripts, plan.AnswerMode);
        var rawAnswer = partialAnswer == null
            ? await _llmService.CompleteAsync(systemPrompt, userPrompt, AnswerMaxTokens, ct)
            : await StreamGroundedAnswerAsync(systemPrompt, userPrompt, retrievedIds, partialAnswer, ct);
        var parsed = ParseGroundedAnswer(rawAnswer);

        // (d) Post-process: enforce citation integrity in code, never trusting
        // the model — only retrieved ids survive as citations.
        var displayAnswer = BuildDisplayAnswer(parsed.Answer, retrievedIds, out var citationOrder);

        var eventById = retrieved.ToDictionary(e => e.EventId, StringComparer.Ordinal);
        var citations = citationOrder
            .Select(id => eventById[id])
            .Select(e => new CitedEvent(e.EventId, e.Title, e.StartDate, BuildSnippet(e), e.DatePrecision))
            .ToList();

        _logger.LogInformation(
            "Ask answered from {Retrieved} retrieved events with {Citations} citations (fromArchive: {FromArchive}, semantic: {Semantic})",
            retrieved.Count, citations.Count, parsed.AnsweredFromArchive, usedSemantic);

        return new MemoryAnswer
        {
            Question = question,
            Answer = displayAnswer,
            Citations = citations,
            AnsweredFromArchive = parsed.AnsweredFromArchive,
            Confidence = Math.Clamp(parsed.Confidence, 0.0, 1.0),
            RetrievedEventIds = retrievedIds,
            UsedSemanticRetrieval = usedSemantic
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string question,
        MemoryQueryOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The progress-reporting pipeline writes display chunks into a
        // channel as the provider streams them; this enumerator drains it.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var askTask = RunAsync();

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
        {
            yield return chunk;
        }

        // Surfaces failures (no provider configured, cancellation, ...) to the consumer.
        await askTask;

        async Task RunAsync()
        {
            try
            {
                await AskCoreAsync(question, options, new ChannelProgress(channel.Writer), ct);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// The display text for a grounded answer and the order in which its
    /// valid citations appear: markers whose id was retrieved become [n]
    /// references (numbered by first appearance), every other marker is
    /// stripped, and the whitespace left behind is tidied. Internal and pure
    /// so the streaming rewriter can be checked against it.
    /// </summary>
    internal static string BuildDisplayAnswer(string? answer, IReadOnlyCollection<string> retrievedIds, out List<string> citationOrder)
    {
        var retrievedSet = new HashSet<string>(retrievedIds, StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Match match in CitationMarkerRegex.Matches(answer ?? string.Empty))
        {
            var id = match.Groups[1].Value;
            if (retrievedSet.Contains(id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }

        var displayAnswer = CitationMarkerRegex.Replace(answer ?? string.Empty, match =>
        {
            var id = match.Groups[1].Value;
            var index = order.IndexOf(id);
            // Valid markers become [n] references matching the citation list
            // order; fabricated/unretrieved ids are stripped entirely.
            return index >= 0 ? $" [{index + 1}]" : string.Empty;
        });
        displayAnswer = Regex.Replace(displayAnswer, " {2,}", " ");
        displayAnswer = Regex.Replace(displayAnswer, " +([.,!?;:])", "$1").Trim();

        citationOrder = order;
        return displayAnswer;
    }

    /// <summary>Relays streamed chunks into the channel behind <see cref="AskStreamingAsync"/>.</summary>
    private sealed class ChannelProgress : IProgress<string>
    {
        private readonly ChannelWriter<string> _writer;

        public ChannelProgress(ChannelWriter<string> writer)
        {
            _writer = writer;
        }

        public void Report(string value) => _writer.TryWrite(value);
    }

    /// <summary>
    /// Merges ranked id lists with reciprocal rank fusion:
    /// score(id) = sum over lists of 1 / (k + rank), rank 1-based.
    /// Ties break on first appearance for a deterministic order.
    /// Public and pure so tests can exercise it directly.
    /// </summary>
    public static List<string> FuseRankings(IReadOnlyList<IReadOnlyList<string>> rankedLists, int k = RrfConstant)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = 0;

        foreach (var list in rankedLists)
        {
            if (list == null)
            {
                continue;
            }

            for (var rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank];
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (k + rank + 1);
                if (!firstSeen.ContainsKey(id))
                {
                    firstSeen[id] = order++;
                }
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => firstSeen[kv.Key])
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Splits text into word-sized chunks (each keeping its trailing space).
    /// A display helper for callers that replay a finished answer; the Ask
    /// page no longer needs it now that the answer streams from the provider.
    /// </summary>
    public static IEnumerable<string> SplitIntoWordChunks(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            yield return i < words.Length - 1 ? words[i] + " " : words[i];
        }
    }

    #region Retrieval

    private async Task<RetrievalPlan> BuildRetrievalPlanAsync(string question, CancellationToken ct)
    {
        const string systemPrompt = """
            You are a retrieval planner for a personal memory archive. Analyze the user's question and produce a plan for finding the relevant events. Return ONLY a JSON object with this exact structure (no markdown, no commentary):
            {
              "semanticQuery": "short phrase of the key search terms (2-6 words)",
              "from": "yyyy-MM-dd lower date bound, or null when the question has none",
              "to": "yyyy-MM-dd upper date bound, or null when the question has none",
              "people": ["person names mentioned in the question"],
              "tags": ["topic keywords implied by the question"],
              "locations": ["place names mentioned in the question"],
              "answerMode": "Factual|Summary|Count|Timeline"
            }
            Use empty arrays when nothing applies. Resolve relative dates against today's date given in the message.
            """;

        var userPrompt = $"Today's date: {DateTime.Now:yyyy-MM-dd}\n\nQuestion: {question}";

        // LLM failures (including a missing Anthropic key) propagate: without a
        // working LLM the answer stage cannot run either.
        var response = await _llmService.CompleteAsync(systemPrompt, userPrompt, PlanMaxTokens, ct);
        var json = StripCodeFences(response);

        try
        {
            var plan = JsonSerializer.Deserialize<RetrievalPlan>(json, JsonOptions);
            if (plan == null || string.IsNullOrWhiteSpace(plan.SemanticQuery))
            {
                _logger.LogWarning("Retrieval plan missing or empty; falling back to raw-question retrieval");
                return FallbackPlan(question);
            }

            return plan;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed retrieval plan JSON; falling back to raw-question retrieval");
            return FallbackPlan(question);
        }
    }

    private static RetrievalPlan FallbackPlan(string question) => new()
    {
        // The raw question serves as both the keyword and the semantic query.
        SemanticQuery = question
    };

    private async Task<List<string>> RunKeywordSearchAsync(
        RetrievalPlan plan,
        string question,
        DateTime? from,
        DateTime? to,
        int topK,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var filter = new SearchFilter
        {
            SearchTerm = plan.SemanticQuery ?? question,
            StartDate = from,
            EndDate = to,
            // Date-ascending puts the earliest matches first, which is the
            // natural rank for "when did I first..." questions; the answering
            // model still sees every retrieved event with its date.
            SortBy = SearchSortOptions.DateAsc,
            PageNumber = 1,
            PageSize = Math.Max(topK * 3, 25)
        };

        await ResolveNameFiltersAsync(filter, plan);

        var results = await _searchService.SearchAsync(filter);
        var ids = results.Events.Select(e => e.EventId).ToList();

        if (ids.Count == 0 &&
            (filter.PersonIds.Any() || filter.TagIds.Any() || filter.LocationIds.Any() || from.HasValue || to.HasValue))
        {
            // The free-text term is AND-ed with the structured filters and can
            // be too restrictive; retry on the structured filters alone so
            // date/person/tag questions still retrieve.
            ct.ThrowIfCancellationRequested();
            filter.SearchTerm = null;
            results = await _searchService.SearchAsync(filter);
            ids = results.Events.Select(e => e.EventId).ToList();
        }

        return ids;
    }

    private async Task ResolveNameFiltersAsync(SearchFilter filter, RetrievalPlan plan)
    {
        // Unresolvable names are skipped rather than added: a non-existent id
        // in the filter would zero out the results.
        foreach (var name in NonBlank(plan.People))
        {
            // Living people only (the repository filters tombstones): exact
            // case-insensitive name match first, then the alias table - so
            // "Bob" filters as "Robert", and a retired pre-merge name (kept
            // as an alias by MergeAsync) resolves to the survivor instead of
            // silently matching nothing - then any partial name match.
            var matches = (await _personRepository.SearchByNameAsync(name)).ToList();
            var exact = matches.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                filter.PersonIds.Add(exact.PersonId);
                continue;
            }

            var aliasPersonId = await ResolveAliasToLivingPersonIdAsync(name);
            if (aliasPersonId != null)
            {
                filter.PersonIds.Add(aliasPersonId);
                continue;
            }

            var partial = matches.FirstOrDefault();
            if (partial != null)
            {
                filter.PersonIds.Add(partial.PersonId);
            }
        }

        foreach (var name in NonBlank(plan.Tags))
        {
            var matches = (await _tagRepository.SearchByNameAsync(name)).ToList();
            var match = matches.FirstOrDefault(t => string.Equals(t.TagName, name, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            if (match != null)
            {
                filter.TagIds.Add(match.TagId);
            }
        }

        foreach (var name in NonBlank(plan.Locations))
        {
            var matches = (await _locationRepository.SearchByNameAsync(name)).ToList();
            var match = matches.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            if (match != null)
            {
                filter.LocationIds.Add(match.LocationId);
            }
        }
    }

    private static IEnumerable<string> NonBlank(List<string>? names) =>
        (names ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n));

    /// <summary>
    /// Resolves a name through the person_aliases table (case-insensitive)
    /// to the LIVING person, following the merge-tombstone chain like
    /// PersonService.FindBestMatchAsync does (the visited set terminates a
    /// malformed cycle). Null when the name is no one's alias or the chain
    /// leads nowhere living.
    /// </summary>
    private async Task<string?> ResolveAliasToLivingPersonIdAsync(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        if (lowered.Length == 0)
        {
            return null;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        var aliasOwnerId = await context.PersonAliases.AsNoTracking()
            .Where(a => a.Alias.ToLower() == lowered)
            .Select(a => a.PersonId)
            .FirstOrDefaultAsync();
        if (aliasOwnerId == null)
        {
            return null;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = await context.People.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PersonId == aliasOwnerId);
        while (current != null && current.MergedIntoId != null && visited.Add(current.PersonId))
        {
            var nextId = current.MergedIntoId;
            current = await context.People.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PersonId == nextId);
        }

        return current != null && current.MergedIntoId == null ? current.PersonId : null;
    }

    private async Task<List<string>> RunSemanticSearchAsync(string semanticQuery, int topK, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Throws ConfigurationException when no embedding key is configured;
        // the caller degrades to keyword-only retrieval.
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(semanticQuery);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        // Query the mapped EmbeddingVector column, never the [NotMapped]
        // Embedding alias (untranslatable -> runtime throw).
        var storedEmbeddings = await dbContext.EventEmbeddings
            .AsNoTracking()
            .Where(ee => ee.EmbeddingVector != null && ee.EmbeddingVector != "")
            .ToListAsync(ct);

        var deserialized = storedEmbeddings
            .Select(ee => (ee.EventId, Embedding: TryDeserializeEmbedding(ee.EmbeddingVector)))
            .Where(x => x.Embedding != null)
            .ToList();

        var candidates = deserialized
            .Where(x => x.Embedding!.Length == queryEmbedding.Length)
            .Select(x => (x.EventId, x.Embedding!))
            .ToList();

        // Stored embeddings exist but NONE match the query dimension: the
        // archive was embedded by a different provider (e.g. a provider switch
        // without re-embedding). Throw the typed mismatch instead of silently
        // contributing nothing - the caller's catch degrades to keyword-only
        // retrieval, logs the warning, and keeps usedSemantic=false so
        // MemoryAnswer.UsedSemanticRetrieval reports honestly.
        if (deserialized.Count > 0 && candidates.Count == 0)
        {
            var storedDimension = deserialized
                .GroupBy(x => x.Embedding!.Length)
                .OrderByDescending(g => g.Count())
                .First().Key;
            throw new EmbeddingDimensionMismatchException(queryEmbedding.Length, storedDimension);
        }

        var neighbors = _embeddingService.FindKNearestNeighbors(
            queryEmbedding,
            candidates,
            Math.Max(topK * 2, topK));

        return neighbors.Select(n => n.Id).ToList();
    }

    /// <summary>
    /// Deserializes a stored embedding, returning null (and logging) on
    /// malformed data so one corrupt row cannot abort retrieval.
    /// Mirrors RagService.TryDeserializeEmbedding.
    /// </summary>
    private float[]? TryDeserializeEmbedding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<float[]>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize stored embedding; skipping");
            return null;
        }
    }

    private async Task<List<Event>> LoadEventsAsync(List<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new List<Event>();
        }

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var events = await dbContext.Events
            .AsNoTracking()
            .Include(e => e.EventPeople).ThenInclude(ep => ep.Person)
            .Include(e => e.EventLocations).ThenInclude(el => el.Location)
            .Where(e => ids.Contains(e.EventId))
            .ToListAsync(ct);

        // Preserve the fused ranking order.
        var byId = events.ToDictionary(e => e.EventId, StringComparer.Ordinal);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    #endregion

    #region Answering

    /// <summary>
    /// Streams the grounded-answer call and reports display text as it
    /// arrives: the raw model text is run through
    /// <see cref="JsonAnswerFieldStreamer"/> (so only the "answer" field's
    /// value is shown, decoded) and <see cref="CitationMarkerStreamRewriter"/>
    /// (so markers become [n] references the moment they complete). Returns
    /// the complete raw response for the usual parse. When the model did not
    /// produce the JSON shape, the whole response is reported once at the end,
    /// mirroring <see cref="ParseGroundedAnswer"/>'s fallback.
    /// </summary>
    private async Task<string> StreamGroundedAnswerAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyCollection<string> retrievedIds,
        IProgress<string> partialAnswer,
        CancellationToken ct)
    {
        var raw = new StringBuilder();
        var field = new JsonAnswerFieldStreamer();
        var rewriter = new CitationMarkerStreamRewriter(retrievedIds);

        await foreach (var piece in _llmService.StreamCompletionAsync(systemPrompt, userPrompt, AnswerMaxTokens, ct).WithCancellation(ct))
        {
            raw.Append(piece);

            var answerText = field.Append(piece);
            if (answerText.Length == 0)
            {
                continue;
            }

            var display = rewriter.Append(answerText);
            if (display.Length > 0)
            {
                partialAnswer.Report(display);
            }
        }

        var tail = new StringBuilder();
        if (!field.FoundAnswerField)
        {
            tail.Append(rewriter.Append(raw.ToString().Trim()));
        }

        tail.Append(rewriter.Flush());
        if (tail.Length > 0)
        {
            partialAnswer.Report(tail.ToString());
        }

        return raw.ToString();
    }

    private static (string SystemPrompt, string UserPrompt) BuildGroundedAnswerPrompts(
        string question,
        List<Event> retrieved,
        bool includeTranscripts,
        string? answerMode)
    {
        const string systemPrompt = """
            You are answering a question about the user's personal memory archive. You are given the ONLY events you may use.
            Rules:
            1. Answer using ONLY the supplied events. Never use outside knowledge and never invent details.
            2. Cite every supporting event inline with the marker [event:EVENTID] immediately after the statement it supports, using the exact "id" value from the supplied events.
            3. If the supplied events do not contain the answer, say so plainly (for example: "Your archive doesn't mention that.") and set answeredFromArchive to false.
            4. Return ONLY a JSON object with this exact structure (no markdown fences, no commentary):
            {
              "answer": "the answer text with [event:EVENTID] markers",
              "answeredFromArchive": true or false,
              "confidence": 0.0 to 1.0
            }
            """;

        var contextBlock = SerializeEventsForPrompt(retrieved, includeTranscripts);
        var modeHint = string.IsNullOrWhiteSpace(answerMode)
            ? string.Empty
            : $"\n\nAnswer mode: {answerMode} (Count = state the number explicitly; Timeline = a chronological account; Summary = a concise overview; Factual = a direct answer).";

        var userPrompt = $"# Archive events (the only allowed sources):\n{contextBlock}\n\n# Question:\n{question}{modeHint}";

        return (systemPrompt, userPrompt);
    }

    private static string SerializeEventsForPrompt(List<Event> events, bool includeTranscripts)
    {
        var compact = events.Select(e => new
        {
            id = e.EventId,
            title = e.Title,
            date = e.EndDate.HasValue
                ? e.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " to "
                    + e.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : e.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            category = e.Category,
            description = e.Description,
            people = e.EventPeople
                .Select(ep => ep.Person?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList(),
            location = !string.IsNullOrWhiteSpace(e.Location)
                ? e.Location
                : e.EventLocations
                    .Select(el => el.Location?.Name)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            transcript = includeTranscripts && !string.IsNullOrWhiteSpace(e.RawTranscript)
                ? e.RawTranscript
                : null
        }).ToList();

        return JsonSerializer.Serialize(compact, PromptJsonOptions);
    }

    private GroundedAnswerResponse ParseGroundedAnswer(string raw)
    {
        var json = StripCodeFences(raw);

        try
        {
            var parsed = JsonSerializer.Deserialize<GroundedAnswerResponse>(json, JsonOptions);
            if (parsed?.Answer != null)
            {
                return parsed;
            }

            _logger.LogWarning("Grounded-answer JSON missing the answer field; treating the whole response as the answer text");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed grounded-answer JSON; treating the whole response as the answer text");
        }

        // Malformed output: surface the text but never claim it is grounded.
        return new GroundedAnswerResponse
        {
            Answer = raw.Trim(),
            AnsweredFromArchive = false,
            Confidence = 0
        };
    }

    private static string BuildSnippet(Event e)
    {
        var source = string.IsNullOrWhiteSpace(e.Description) ? e.Title : e.Description;
        source = source.Trim();
        return source.Length <= 140 ? source : source[..140].TrimEnd() + "…";
    }

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

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    #endregion

    #region Response Models

    /// <summary>Retrieval plan produced by the query-analysis LLM call.</summary>
    private sealed class RetrievalPlan
    {
        public string? SemanticQuery { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public List<string>? People { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Locations { get; set; }
        public string? AnswerMode { get; set; }
    }

    /// <summary>Structured grounded answer returned by the answering LLM call.</summary>
    private sealed class GroundedAnswerResponse
    {
        public string? Answer { get; set; }
        public bool AnsweredFromArchive { get; set; }
        public double Confidence { get; set; }
    }

    #endregion
}
