using Microsoft.Extensions.Logging;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// RAG (Retrieval-Augmented Generation) service implementation.
/// Creates a short-lived DbContext per operation via IDbContextFactory.
/// Cross-reference detection is heuristic — temporal proximity, shared people,
/// shared places, then shared category, with confidences derived from
/// embedding similarity — and results are persisted via
/// <see cref="ICrossReferenceRepository"/>. Causal and Consequential have no
/// heuristic: they need an LLM judgement, which this service does not make.
/// </summary>
public class RagService : IRagService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IEventService _eventService;
    private readonly IDbContextFactory<Data.AppDbContext> _contextFactory;
    private readonly ICrossReferenceRepository _crossReferenceRepository;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger<RagService> _logger;

    /// <summary>
    /// The similarity floor when <c>rag_similarity_threshold</c> is absent or
    /// unusable, or when no settings service is injected (the value the
    /// service hard-coded before the setting was read).
    /// </summary>
    internal const double DefaultSimilarityThreshold = 0.7;

    /// <summary>
    /// Creates the service. <paramref name="settingsService"/> is optional and
    /// trailing so existing constructions keep compiling; without it the
    /// similarity threshold is <see cref="DefaultSimilarityThreshold"/>.
    /// </summary>
    public RagService(
        IEmbeddingService embeddingService,
        IEventService eventService,
        IDbContextFactory<Data.AppDbContext> contextFactory,
        ICrossReferenceRepository crossReferenceRepository,
        ILogger<RagService> logger,
        ISettingsService? settingsService = null)
    {
        _embeddingService = embeddingService;
        _eventService = eventService;
        _contextFactory = contextFactory;
        _crossReferenceRepository = crossReferenceRepository;
        _logger = logger;
        _settingsService = settingsService;
    }

    /// <summary>
    /// The configured similarity floor: <c>rag_similarity_threshold</c>, read
    /// per call so a Settings change applies live, validated to 0..1.
    /// </summary>
    internal async Task<double> GetSimilarityThresholdAsync()
    {
        if (_settingsService == null)
        {
            return DefaultSimilarityThreshold;
        }

        var text = await _settingsService.GetSettingAsync<string>(SettingKeys.RagSimilarityThreshold, string.Empty);
        return double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var value)
               && !double.IsNaN(value) && value >= 0.0 && value <= 1.0
            ? value
            : DefaultSimilarityThreshold;
    }

    /// <summary>
    /// Finds similar events using embedding-based similarity search.
    /// </summary>
    public async Task<List<SimilarEvent>> FindSimilarEventsAsync(string eventId, int topK = 10, double? threshold = null)
    {
        try
        {
            _logger.LogInformation("Finding similar events for {EventId}", eventId);

            var effectiveThreshold = threshold ?? await GetSimilarityThresholdAsync();

            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            // Get the source event and its embedding.
            // NOTE: queries must use the mapped EmbeddingVector column, never the
            // [NotMapped] Embedding alias (untranslatable -> runtime throw).
            var sourceEventEmbedding = await dbContext.EventEmbeddings
                .AsNoTracking()
                .FirstOrDefaultAsync(ee => ee.EventId == eventId);

            if (sourceEventEmbedding == null || string.IsNullOrWhiteSpace(sourceEventEmbedding.EmbeddingVector))
            {
                _logger.LogWarning("No embedding found for event {EventId}", eventId);
                return new List<SimilarEvent>();
            }

            var queryEmbedding = TryDeserializeEmbedding(sourceEventEmbedding.EmbeddingVector);
            if (queryEmbedding == null)
            {
                // Malformed source embedding: return empty results instead of
                // throwing so one corrupt row can't break the Connections page.
                _logger.LogWarning(
                    "Stored embedding for event {EventId} is malformed; returning no similar events. " +
                    "Regenerate embeddings to fix.", eventId);
                return new List<SimilarEvent>();
            }

            // Get all other event embeddings
            var allEmbeddings = await dbContext.EventEmbeddings
                .AsNoTracking()
                .Where(ee => ee.EventId != eventId && ee.EmbeddingVector != null && ee.EmbeddingVector != "")
                .ToListAsync();

            // Dimension guard (F11): cosine similarity across different
            // embedding dimensions is garbage math, so only candidate rows
            // whose stored dimension matches the SOURCE row's are compared.
            // Mismatches happen after an embedding-provider switch until the
            // user re-embeds; they are skipped with one summary log.
            var sourceDimension = sourceEventEmbedding.EmbeddingDimension;
            var mismatchedCount = allEmbeddings.Count(ee => ee.EmbeddingDimension != sourceDimension);
            if (mismatchedCount > 0)
            {
                _logger.LogWarning(
                    "Skipping {Count} candidate embedding(s) whose dimension differs from the source row's " +
                    "({Dimension}) for event {EventId}; re-embed all events to include them.",
                    mismatchedCount, sourceDimension, eventId);
            }

            var candidateEmbeddings = allEmbeddings
                .Where(ee => ee.EmbeddingDimension == sourceDimension)
                .Select(ee => (ee.EventId, Embedding: TryDeserializeEmbedding(ee.EmbeddingVector)))
                .Where(x => x.Embedding != null)
                .Select(x => (x.EventId, x.Embedding!))
                .ToList();

            // Find K nearest neighbors
            var similarities = _embeddingService.FindKNearestNeighbors(
                queryEmbedding,
                candidateEmbeddings,
                topK,
                effectiveThreshold);

            // Get event details
            var similarEvents = new List<SimilarEvent>();
            foreach (var similarity in similarities)
            {
                var eventEntity = await _eventService.GetEventByIdAsync(similarity.Id);
                if (eventEntity != null)
                {
                    similarEvents.Add(new SimilarEvent
                    {
                        EventId = eventEntity.EventId,
                        Title = eventEntity.Title,
                        Description = eventEntity.Description,
                        StartDate = eventEntity.StartDate,
                        Similarity = similarity.Similarity
                    });
                }
            }

            _logger.LogInformation("Found {Count} similar events for {EventId}", similarEvents.Count, eventId);
            return similarEvents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding similar events");
            throw;
        }
    }

    /// <summary>
    /// Detects cross-references between events.
    /// Detection is HEURISTIC (temporal proximity and shared category, with
    /// confidences derived from embedding similarity where available) — no LLM
    /// call is made. New detections are persisted via the cross-reference
    /// repository (deduplicated per event pair), and previously stored
    /// references for the event are merged into the returned results.
    /// </summary>
    public async Task<List<CrossReferenceResult>> DetectCrossReferencesAsync(string eventId, IEnumerable<string>? candidateEventIds = null)
    {
        try
        {
            _logger.LogInformation("Detecting cross-references for event {EventId}", eventId);

            var sourceEvent = await _eventService.GetEventWithDetailsAsync(eventId);
            if (sourceEvent == null)
            {
                throw new Exception($"Event {eventId} not found");
            }

            // Get candidate events (either specified, or embedding-similar events).
            // similarityByEventId feeds the heuristic confidence scores.
            var candidates = new List<Event>();
            var similarityByEventId = new Dictionary<string, double>();

            if (candidateEventIds != null && candidateEventIds.Any())
            {
                foreach (var id in candidateEventIds)
                {
                    var evt = await _eventService.GetEventWithDetailsAsync(id);
                    if (evt != null) candidates.Add(evt);
                }
            }
            else
            {
                // Candidates are gathered below the display floor so a
                // relationship can still be found for a borderline pair; the
                // configured threshold only ever lowers this, never raises it.
                var candidateFloor = Math.Min(0.6, await GetSimilarityThresholdAsync());
                var similarEvents = await FindSimilarEventsAsync(eventId, topK: 20, threshold: candidateFloor);
                foreach (var similar in similarEvents)
                {
                    var evt = await _eventService.GetEventWithDetailsAsync(similar.EventId);
                    if (evt != null)
                    {
                        candidates.Add(evt);
                        similarityByEventId[similar.EventId] = similar.Similarity;
                    }
                }
            }

            // Start from what is already stored so previous detections are not lost
            var results = new List<CrossReferenceResult>();
            var knownPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var storedReferences = await _crossReferenceRepository.GetReferencesForEventAsync(eventId);
            foreach (var stored in storedReferences)
            {
                results.Add(ToResult(stored, eventId));
                knownPairs.Add(PairKey(stored.EventId1, stored.EventId2));
            }

            // Heuristic detection over the candidates, persisting new pairs
            foreach (var candidate in candidates)
            {
                if (candidate.EventId == eventId)
                    continue;

                if (knownPairs.Contains(PairKey(eventId, candidate.EventId)))
                    continue;

                double? similarity = similarityByEventId.TryGetValue(candidate.EventId, out var s) ? s : null;
                var relationship = DetermineRelationshipHeuristically(sourceEvent, candidate, similarity);
                if (relationship == null)
                    continue;

                knownPairs.Add(PairKey(eventId, candidate.EventId));
                results.Add(relationship);

                // Persist (dedupe against concurrent writers via GetReferenceAsync).
                // Persistence failure must not break detection results.
                try
                {
                    var existing = await _crossReferenceRepository.GetReferenceAsync(eventId, candidate.EventId);
                    if (existing == null)
                    {
                        await _crossReferenceRepository.AddAsync(new CrossReference
                        {
                            ReferenceId = Guid.NewGuid().ToString(),
                            EventId1 = relationship.SourceEventId,
                            EventId2 = relationship.TargetEventId,
                            RelationshipType = ToStorageType(relationship.RelationshipType),
                            ConfidenceScore = relationship.Confidence,
                            AnalysisDetails = relationship.Reasoning,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(persistEx,
                        "Failed to persist cross-reference {Source} -> {Target}",
                        relationship.SourceEventId, relationship.TargetEventId);
                }
            }

            _logger.LogInformation("Detected {Count} cross-references for event {EventId} ({Stored} previously stored)",
                results.Count, eventId, storedReferences.Count());

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting cross-references");
            throw;
        }
    }

    /// <summary>
    /// Detects patterns in events.
    /// </summary>
    public async Task<PatternAnalysisResult> DetectPatternsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            _logger.LogInformation("Detecting patterns in events");

            var result = new PatternAnalysisResult();

            // Get events in date range
            var events = startDate.HasValue && endDate.HasValue
                ? await _eventService.GetEventsByDateRangeAsync(startDate.Value, endDate.Value)
                : await _eventService.GetAllEventsAsync();

            var eventList = events.ToList();

            if (eventList.Count == 0)
            {
                return result;
            }

            // Detect category patterns
            result.CategoryPatterns = DetectCategoryPatterns(eventList);

            // Detect temporal clusters
            result.TemporalClusters = DetectTemporalClusters(eventList);

            // Detect era transitions
            result.EraTransitions = DetectEraTransitions(eventList);

            _logger.LogInformation("Detected {CategoryPatterns} category patterns, {TemporalClusters} temporal clusters, {EraTransitions} era transitions",
                result.CategoryPatterns.Count, result.TemporalClusters.Count, result.EraTransitions.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting patterns");
            throw;
        }
    }

    /// <summary>
    /// Suggests tags for an event based on similar events.
    /// </summary>
    public async Task<List<TagSuggestion>> SuggestTagsAsync(string eventId, int maxSuggestions = 5)
    {
        try
        {
            _logger.LogInformation("Suggesting tags for event {EventId}", eventId);

            // Find similar events at the configured floor
            var similarEvents = await FindSimilarEventsAsync(eventId, topK: 10);

            if (similarEvents.Count == 0)
            {
                return new List<TagSuggestion>();
            }

            // Get tags from similar events with frequency counting
            var tagFrequency = new Dictionary<string, (int count, double totalSimilarity)>();

            foreach (var similar in similarEvents)
            {
                var eventTags = await _eventService.GetEventTagsAsync(similar.EventId);

                foreach (var tag in eventTags)
                {
                    if (tagFrequency.ContainsKey(tag.Name))
                    {
                        var current = tagFrequency[tag.Name];
                        tagFrequency[tag.Name] = (current.count + 1, current.totalSimilarity + similar.Similarity);
                    }
                    else
                    {
                        tagFrequency[tag.Name] = (1, similar.Similarity);
                    }
                }
            }

            // Calculate confidence scores and create suggestions
            var suggestions = tagFrequency
                .Select(kvp => new TagSuggestion
                {
                    TagName = kvp.Key,
                    Confidence = (kvp.Value.totalSimilarity / similarEvents.Count) * (kvp.Value.count / (double)similarEvents.Count),
                    Reason = $"Found in {kvp.Value.count} similar events"
                })
                .OrderByDescending(s => s.Confidence)
                .Take(maxSuggestions)
                .ToList();

            _logger.LogInformation("Generated {Count} tag suggestions for event {EventId}", suggestions.Count, eventId);
            return suggestions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting tags");
            throw;
        }
    }

    /// <summary>
    /// Suggests tags for text before event creation.
    /// </summary>
    /// <exception cref="EmbeddingDimensionMismatchException">
    /// The current embedding provider's dimension matches none of the stored
    /// rows (e.g. after a provider switch without re-embedding) — comparing
    /// across dimensions would produce garbage, so the call is refused.
    /// </exception>
    public async Task<List<TagSuggestion>> SuggestTagsForTextAsync(string title, string? description, int maxSuggestions = 5)
    {
        try
        {
            _logger.LogInformation("Suggesting tags for text: {Title}", title);

            // Load stored embeddings (mapped EmbeddingVector column — the
            // [NotMapped] Embedding alias is untranslatable in EF queries).
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var allEmbeddings = await dbContext.EventEmbeddings
                .AsNoTracking()
                .Where(ee => ee.EmbeddingVector != null && ee.EmbeddingVector != "")
                .ToListAsync();

            if (allEmbeddings.Count == 0)
            {
                // Nothing to compare against — no point paying for a query
                // embedding.
                _logger.LogInformation("No stored embeddings; returning no tag suggestions for text");
                return new List<TagSuggestion>();
            }

            // Generate the query embedding FIRST: per-call routing resolves the
            // CURRENT provider, so the returned vector's length is the
            // authoritative dimension for the guard below. (The routing
            // facade's EmbeddingDimension display property lags behind an
            // in-session provider switch until an embedding call refreshes it —
            // a pre-check keyed on it can pass while the query comes back a
            // different length, silently yielding zero candidates instead of
            // the typed guard.)
            var text = string.IsNullOrWhiteSpace(description) ? title : $"{title}. {description}";
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(text);

            // Dimension guard (F11): stored rows from another dimension cannot
            // be compared with the query embedding.
            var currentDimension = queryEmbedding.Length;
            var matchingEmbeddings = allEmbeddings
                .Where(ee => ee.EmbeddingDimension == currentDimension)
                .ToList();

            if (matchingEmbeddings.Count == 0)
            {
                var storedDimension = allEmbeddings
                    .GroupBy(ee => ee.EmbeddingDimension)
                    .OrderByDescending(g => g.Count())
                    .First().Key;
                throw new EmbeddingDimensionMismatchException(currentDimension, storedDimension);
            }

            if (matchingEmbeddings.Count < allEmbeddings.Count)
            {
                _logger.LogWarning(
                    "Skipping {Count} stored embedding(s) whose dimension differs from the current provider's " +
                    "({Dimension}); re-embed all events to include them.",
                    allEmbeddings.Count - matchingEmbeddings.Count, currentDimension);
            }

            var candidateEmbeddings = matchingEmbeddings
                .Select(ee => (ee.EventId, Embedding: TryDeserializeEmbedding(ee.EmbeddingVector)))
                .Where(x => x.Embedding != null)
                .Select(x => (x.EventId, x.Embedding!))
                .ToList();

            // Find similar events at the configured floor
            var similarities = _embeddingService.FindKNearestNeighbors(
                queryEmbedding,
                candidateEmbeddings,
                10,
                await GetSimilarityThresholdAsync());

            // Get tags from similar events
            var tagFrequency = new Dictionary<string, (int count, double totalSimilarity)>();

            foreach (var similarity in similarities)
            {
                var eventTags = await _eventService.GetEventTagsAsync(similarity.Id);

                foreach (var tag in eventTags)
                {
                    if (tagFrequency.ContainsKey(tag.Name))
                    {
                        var current = tagFrequency[tag.Name];
                        tagFrequency[tag.Name] = (current.count + 1, current.totalSimilarity + similarity.Similarity);
                    }
                    else
                    {
                        tagFrequency[tag.Name] = (1, similarity.Similarity);
                    }
                }
            }

            // Calculate confidence and create suggestions
            var suggestions = tagFrequency
                .Select(kvp => new TagSuggestion
                {
                    TagName = kvp.Key,
                    Confidence = (kvp.Value.totalSimilarity / similarities.Count) * (kvp.Value.count / (double)similarities.Count),
                    Reason = $"Found in {kvp.Value.count} similar events"
                })
                .OrderByDescending(s => s.Confidence)
                .Take(maxSuggestions)
                .ToList();

            _logger.LogInformation("Generated {Count} tag suggestions for text", suggestions.Count);
            return suggestions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting tags for text");
            throw;
        }
    }

    #region Private Methods

    /// <summary>
    /// Safely deserializes a stored embedding, returning null (and logging) on malformed data
    /// instead of throwing so a single corrupt row cannot abort an entire similarity search.
    /// </summary>
    private float[]? TryDeserializeEmbedding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

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

    /// <summary>
    /// Heuristic relationship detection with honest labelling: confidences are
    /// derived from embedding similarity, temporal proximity and link overlap
    /// — never hard-coded — and the reasoning states that the result is a
    /// heuristic. Precedence: Temporal (within a week), Participatory (a person
    /// in common), Locational (a place in common), Thematic (a category in
    /// common); the first that applies is the relationship.
    /// </summary>
    private CrossReferenceResult? DetermineRelationshipHeuristically(Event source, Event target, double? similarity)
    {
        var daysDifference = Math.Abs((target.StartDate - source.StartDate).TotalDays);

        if (daysDifference <= 7)
        {
            // Proximity score: 1.0 (same day) down to ~0.0 (7 days apart)
            var proximity = 1.0 - (daysDifference / 7.0);

            // Blend with embedding similarity when we have one; otherwise the
            // proximity alone (discounted — we know less about the pair).
            var confidence = similarity.HasValue
                ? Math.Clamp(0.5 * similarity.Value + 0.5 * proximity, 0.0, 1.0)
                : Math.Clamp(0.6 * proximity, 0.0, 1.0);

            return new CrossReferenceResult
            {
                SourceEventId = source.EventId,
                TargetEventId = target.EventId,
                RelationshipType = CrossReferenceType.Temporal,
                Confidence = Math.Round(confidence, 3),
                Reasoning = similarity.HasValue
                    ? $"Heuristic: events occurred within {daysDifference:0.#} days of each other (embedding similarity {similarity.Value:0.00})"
                    : $"Heuristic: events occurred within {daysDifference:0.#} days of each other"
            };
        }

        // Shared people and shared places are stronger evidence of a real
        // relationship than a shared category (which every "work" event has
        // with every other), so they are tested before the thematic fallback.
        // Both read the navigations GetEventWithDetailsAsync loads; an event
        // fetched without them simply has nothing in common here.
        var sharedPeople = SharedNames(
            source.EventPeople.Select(ep => (ep.PersonId, ep.Person?.Name)),
            target.EventPeople.Select(ep => (ep.PersonId, ep.Person?.Name)));
        if (sharedPeople.Count > 0)
        {
            var overlap = OverlapRatio(sharedPeople.Count, source.EventPeople.Count, target.EventPeople.Count);
            var confidence = similarity.HasValue
                ? Math.Clamp(0.5 * similarity.Value + 0.5 * (0.5 + 0.5 * overlap), 0.0, 1.0)
                : Math.Clamp(0.45 + 0.3 * overlap, 0.0, 1.0);

            return new CrossReferenceResult
            {
                SourceEventId = source.EventId,
                TargetEventId = target.EventId,
                RelationshipType = CrossReferenceType.Participatory,
                Confidence = Math.Round(confidence, 3),
                Reasoning = similarity.HasValue
                    ? $"Heuristic: both events involve {FormatList(sharedPeople)} (embedding similarity {similarity.Value:0.00})"
                    : $"Heuristic: both events involve {FormatList(sharedPeople)}"
            };
        }

        var sharedPlaces = SharedNames(
            source.EventLocations.Select(el => (el.LocationId, el.Location?.Name)),
            target.EventLocations.Select(el => (el.LocationId, el.Location?.Name)));
        if (sharedPlaces.Count > 0)
        {
            var overlap = OverlapRatio(sharedPlaces.Count, source.EventLocations.Count, target.EventLocations.Count);
            var confidence = similarity.HasValue
                ? Math.Clamp(0.5 * similarity.Value + 0.5 * (0.45 + 0.5 * overlap), 0.0, 1.0)
                : Math.Clamp(0.4 + 0.3 * overlap, 0.0, 1.0);

            return new CrossReferenceResult
            {
                SourceEventId = source.EventId,
                TargetEventId = target.EventId,
                RelationshipType = CrossReferenceType.Locational,
                Confidence = Math.Round(confidence, 3),
                Reasoning = similarity.HasValue
                    ? $"Heuristic: both events took place at {FormatList(sharedPlaces)} (embedding similarity {similarity.Value:0.00})"
                    : $"Heuristic: both events took place at {FormatList(sharedPlaces)}"
            };
        }

        if (!string.IsNullOrWhiteSpace(source.Category) &&
            string.Equals(source.Category, target.Category, StringComparison.OrdinalIgnoreCase))
        {
            // Thematic closeness is best measured by embedding similarity; the
            // shared category alone is a weak signal, reflected in the score.
            var confidence = similarity.HasValue
                ? Math.Clamp(similarity.Value, 0.0, 1.0)
                : 0.4;

            return new CrossReferenceResult
            {
                SourceEventId = source.EventId,
                TargetEventId = target.EventId,
                RelationshipType = CrossReferenceType.Thematic,
                Confidence = Math.Round(confidence, 3),
                Reasoning = similarity.HasValue
                    ? $"Heuristic: both events share the '{source.Category}' category (embedding similarity {similarity.Value:0.00})"
                    : $"Heuristic: both events share the '{source.Category}' category (no embedding similarity available)"
            };
        }

        return null;
    }

    /// <summary>
    /// Display names of the junction rows two events have in common, matched
    /// by id (a merged-away person still shares its id with itself). The name
    /// falls back to the id when the navigation was not loaded.
    /// </summary>
    private static List<string> SharedNames(
        IEnumerable<(string Id, string? Name)> source,
        IEnumerable<(string Id, string? Name)> target)
    {
        var targetIds = target.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return source
            .Where(s => targetIds.Contains(s.Id))
            .Select(s => string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Share of the smaller event's links that are shared: 1.0 when one is a subset of the other.</summary>
    private static double OverlapRatio(int shared, int sourceCount, int targetCount)
    {
        var smaller = Math.Max(1, Math.Min(sourceCount, targetCount));
        return Math.Clamp((double)shared / smaller, 0.0, 1.0);
    }

    private static string FormatList(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>Canonical unordered key for an event pair.</summary>
    private static string PairKey(string eventId1, string eventId2)
    {
        return string.CompareOrdinal(eventId1, eventId2) <= 0
            ? $"{eventId1}|{eventId2}"
            : $"{eventId2}|{eventId1}";
    }

    /// <summary>Converts a stored cross-reference row into a result, oriented from the requested event.</summary>
    private static CrossReferenceResult ToResult(CrossReference stored, string sourceEventId)
    {
        var isForward = string.Equals(stored.EventId1, sourceEventId, StringComparison.OrdinalIgnoreCase);

        return new CrossReferenceResult
        {
            SourceEventId = isForward ? stored.EventId1 : stored.EventId2,
            TargetEventId = isForward ? stored.EventId2 : stored.EventId1,
            RelationshipType = FromStorageType(stored.RelationshipType),
            Confidence = stored.ConfidenceScore ?? 0.0,
            Reasoning = stored.AnalysisDetails
        };
    }

    /// <summary>Maps the service-level relationship enum to the stored string value.</summary>
    private static string ToStorageType(CrossReferenceType type)
    {
        return type switch
        {
            CrossReferenceType.Causal => RelationshipType.Causal.ToStringValue(),
            CrossReferenceType.Thematic => RelationshipType.Thematic.ToStringValue(),
            CrossReferenceType.Temporal => RelationshipType.Temporal.ToStringValue(),
            CrossReferenceType.Participatory => RelationshipType.Person.ToStringValue(),
            CrossReferenceType.Locational => RelationshipType.Location.ToStringValue(),
            CrossReferenceType.Consequential => RelationshipType.Causal.ToStringValue(),
            _ => RelationshipType.Other.ToStringValue()
        };
    }

    /// <summary>Maps a stored relationship string back to the service-level enum.</summary>
    private static CrossReferenceType FromStorageType(string? storedType)
    {
        return RelationshipTypeExtensions.FromString(storedType ?? string.Empty) switch
        {
            RelationshipType.Causal => CrossReferenceType.Causal,
            RelationshipType.Temporal => CrossReferenceType.Temporal,
            RelationshipType.Person => CrossReferenceType.Participatory,
            RelationshipType.Location => CrossReferenceType.Locational,
            _ => CrossReferenceType.Thematic
        };
    }

    private List<CategoryPattern> DetectCategoryPatterns(List<Event> events)
    {
        var patterns = new List<CategoryPattern>();

        var categoryGroups = events.GroupBy(e => e.Category);

        foreach (var group in categoryGroups)
        {
            var pattern = new CategoryPattern
            {
                Category = group.Key ?? "Unknown",
                EventCount = group.Count()
            };

            // Calculate frequency (events per month)
            if (events.Count > 0)
            {
                var dateRange = events.Max(e => e.StartDate) - events.Min(e => e.StartDate);
                var months = Math.Max(1, dateRange.TotalDays / 30);
                pattern.Frequency = group.Count() / months;
            }

            patterns.Add(pattern);
        }

        return patterns.OrderByDescending(p => p.EventCount).ToList();
    }

    private List<TemporalCluster> DetectTemporalClusters(List<Event> events)
    {
        var clusters = new List<TemporalCluster>();
        var sortedEvents = events.OrderBy(e => e.StartDate).ToList();

        // Simple clustering: group events within 30 days
        for (int i = 0; i < sortedEvents.Count; i++)
        {
            var clusterEvents = new List<Event> { sortedEvents[i] };
            var startDate = sortedEvents[i].StartDate;

            for (int j = i + 1; j < sortedEvents.Count; j++)
            {
                if ((sortedEvents[j].StartDate - startDate).TotalDays <= 30)
                {
                    clusterEvents.Add(sortedEvents[j]);
                    i = j;
                }
                else
                {
                    break;
                }
            }

            if (clusterEvents.Count >= 3)  // Only clusters with 3+ events
            {
                clusters.Add(new TemporalCluster
                {
                    StartDate = clusterEvents.First().StartDate,
                    EndDate = clusterEvents.Last().StartDate,
                    EventCount = clusterEvents.Count,
                    EventIds = clusterEvents.Select(e => e.EventId).ToList(),
                    Theme = DetermineClusterTheme(clusterEvents)
                });
            }
        }

        return clusters;
    }

    private List<EraTransition> DetectEraTransitions(List<Event> events)
    {
        var transitions = new List<EraTransition>();

        // Simple transition detection based on category changes
        var sortedEvents = events.OrderBy(e => e.StartDate).ToList();

        for (int i = 1; i < sortedEvents.Count - 1; i++)
        {
            var prevCategory = sortedEvents[i - 1].Category;
            var currentCategory = sortedEvents[i].Category;

            if (prevCategory != currentCategory)
            {
                // Check if this is a sustained change (next few events are also different)
                var nextEvents = sortedEvents.Skip(i).Take(3).ToList();
                if (nextEvents.Count(e => e.Category == currentCategory) >= 2)
                {
                    transitions.Add(new EraTransition
                    {
                        TransitionDate = sortedEvents[i].StartDate,
                        FromTheme = prevCategory ?? "Unknown",
                        ToTheme = currentCategory ?? "Unknown",
                        Description = $"Transition from {prevCategory ?? "Unknown"} to {currentCategory ?? "Unknown"} focus"
                    });
                }
            }
        }

        return transitions;
    }

    private string? DetermineClusterTheme(List<Event> events)
    {
        // Find most common category in cluster
        var commonCategory = events
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;

        return commonCategory;
    }

    #endregion
}
