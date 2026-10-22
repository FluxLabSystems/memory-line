using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service for extracting events from transcripts using LLM.
/// Creates a short-lived DbContext per operation via IDbContextFactory,
/// so it is safe to consume from singletons and background processing.
/// </summary>
public class EventExtractionService : IEventExtractionService
{
    private readonly ILlmService _llmService;
    private readonly ISpeechToTextService _sttService;
    private readonly IEventService _eventService;
    private readonly ISettingsService _settingsService;
    private readonly IRecordingQueueRepository _queueRepository;
    private readonly IPersonService _personService;
    private readonly IDbContextFactory<Data.AppDbContext> _contextFactory;
    private readonly ILogger<EventExtractionService> _logger;
    private readonly EventRevisionWriter? _revisionWriter;
    private readonly ICaptureStatusPublisher? _statusPublisher;
    private readonly ITimelineProjectionPublisher? _projectionPublisher;

    private const string MissingApiKeyMessage = "Anthropic API key not configured — add it in Settings";
    private const string MissingBaseUrlMessage = "LLM base URL not configured — add it in Settings (e.g. http://localhost:11434/v1 for Ollama)";

    public EventExtractionService(
        ILlmService llmService,
        ISpeechToTextService sttService,
        IEventService eventService,
        ISettingsService settingsService,
        IRecordingQueueRepository queueRepository,
        IPersonService personService,
        IDbContextFactory<Data.AppDbContext> contextFactory,
        ILogger<EventExtractionService> logger,
        EventRevisionWriter? revisionWriter = null,
        ICaptureStatusPublisher? statusPublisher = null,
        ITimelineProjectionPublisher? projectionPublisher = null)
    {
        _llmService = llmService;
        _sttService = sttService;
        _eventService = eventService;
        _settingsService = settingsService;
        _queueRepository = queueRepository;
        _personService = personService;
        _contextFactory = contextFactory;
        _logger = logger;
        _revisionWriter = revisionWriter;
        _statusPublisher = statusPublisher;
        _projectionPublisher = projectionPublisher;
    }

    /// <summary>
    /// Processes a queue source: resolve its transcript (inline text for
    /// Text/Imported sources, speech-to-text for Audio sources) and extract events.
    /// </summary>
    public async Task<int> ProcessRecordingAsync(
        string queueId,
        IProgress<(int, string)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Processing queue source {QueueId}", queueId);

            // API-key pre-flight BEFORE any work (transcription is wasted effort
            // if extraction can never run). ConfigurationException is non-retryable.
            await EnsureLlmConfiguredAsync();

            progress?.Report((10, "Loading source..."));

            var queueItem = await _queueRepository.GetByIdAsync(queueId);
            if (queueItem == null)
            {
                throw new Exception($"Queue item {queueId} not found");
            }

            // Step 1: resolve the transcript. Text/Imported sources carry their
            // content inline and never run speech-to-text; Audio sources are
            // transcribed ONCE and the transcript is persisted on the queue row
            // so retries (e.g. after a flaky LLM call) skip Whisper entirely.
            string transcript;
            if (queueItem.SourceType != QueueSourceType.Audio)
            {
                if (string.IsNullOrWhiteSpace(queueItem.Transcript))
                {
                    throw new Exception($"Queue item {queueId} is a text source but has no stored text");
                }

                progress?.Report((20, "Using saved text..."));
                transcript = queueItem.Transcript;
                _logger.LogInformation("Using stored text for {QueueId}: {Length} characters",
                    queueId, transcript.Length);
            }
            else if (!string.IsNullOrWhiteSpace(queueItem.Transcript))
            {
                progress?.Report((20, "Using saved transcript..."));
                transcript = queueItem.Transcript;
                _logger.LogInformation("Reusing persisted transcript for {QueueId}: {Length} characters",
                    queueId, transcript.Length);
            }
            else
            {
                progress?.Report((20, "Transcribing audio..."));
                _logger.LogInformation("Transcribing audio file: {FilePath}", queueItem.AudioFilePath);

                var transcriptionResult = await _sttService.TranscribeAsync(queueItem.AudioFilePath, null, cancellationToken);

                if (!transcriptionResult.Success || string.IsNullOrWhiteSpace(transcriptionResult.Text))
                {
                    throw new Exception($"Transcription failed: {transcriptionResult.ErrorMessage}");
                }

                transcript = transcriptionResult.Text;
                _logger.LogInformation("Transcription completed: {Length} characters", transcript.Length);

                // Persist the transcript on the queue row BEFORE extraction so a
                // failed LLM call (and the queue's retry loop, which re-enters
                // this method) reuses it instead of re-running Whisper.
                try
                {
                    queueItem.Transcript = transcript;
                    await _queueRepository.UpdateAsync(queueItem);
                }
                catch (Exception persistEx)
                {
                    // Non-fatal by design: extraction proceeds with the in-memory
                    // transcript; only the retry optimization is lost.
                    _logger.LogWarning(persistEx,
                        "Failed to persist transcript for {QueueId}; a retry may re-transcribe", queueId);
                }
            }

            // Step 2: Extract events using LLM (transcript + audio path are persisted
            // on every pending event so nothing lives only in memory)
            progress?.Report((50, "Extracting events..."));
            var pendingEvents = await ExtractAndCreatePendingEventsAsync(queueId, transcript, cancellationToken);

            progress?.Report((100, $"Extracted {pendingEvents.Count} events"));
            _logger.LogInformation("Successfully extracted {Count} events from recording {QueueId}",
                pendingEvents.Count, queueId);

            return pendingEvents.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing recording {QueueId}", queueId);
            throw;
        }
    }

    /// <summary>
    /// Extracts events from transcript and creates pending events.
    /// The transcript and source audio path are persisted on each pending event.
    /// </summary>
    public async Task<List<PendingEvent>> ExtractAndCreatePendingEventsAsync(
        string queueId, string transcript, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Extracting events from transcript for queue {QueueId}", queueId);

            // Resolve the source audio file path so it can be persisted with each
            // pending event. Text sources store string.Empty in the NOT NULL
            // audio_file_path column (see RecordingQueue.AudioFilePath); map that
            // to null so pending/approved events never claim a phantom audio file.
            var queueItem = await _queueRepository.GetByIdAsync(queueId);
            var audioFilePath = string.IsNullOrEmpty(queueItem?.AudioFilePath)
                ? null
                : queueItem!.AudioFilePath;

            // Build extraction context
            var context = await BuildExtractionContextAsync();

            // Extract events using LLM
            var extraction = await _llmService.ExtractEventsAsync(transcript, context, cancellationToken);

            if (!extraction.Success)
            {
                throw new Exception($"Event extraction failed: {extraction.ErrorMessage}");
            }

            _logger.LogInformation("LLM extracted {Count} events with confidence {Confidence}",
                extraction.Events.Count, extraction.OverallConfidence);

            // Convert to pending events
            var pendingEvents = new List<PendingEvent>();

            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            foreach (var extracted in extraction.Events)
            {
                var pendingEvent = new PendingEvent
                {
                    PendingEventId = Guid.NewGuid().ToString(),
                    QueueId = queueId,
                    Title = extracted.Title,
                    Description = extracted.Description,
                    StartDate = extracted.StartDate,
                    EndDate = extracted.EndDate,
                    // Defensive parse: null/garbage precision strings fall back to Day.
                    DatePrecision = DatePrecisionParser.Parse(extracted.DatePrecision),
                    Category = ParseCategory(extracted.Category),
                    ConfidenceScore = extracted.Confidence,
                    ExtractedData = System.Text.Json.JsonSerializer.Serialize(extracted),
                    Transcript = transcript,
                    AudioFilePath = audioFilePath,
                    Status = PendingStatus.PendingReview.ToStringValue(),
                    IsApproved = false,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.PendingEvents.Add(pendingEvent);
                pendingEvents.Add(pendingEvent);

                _logger.LogDebug("Created pending event: {Title} (confidence: {Confidence})",
                    pendingEvent.Title, pendingEvent.ConfidenceScore);
            }

            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Created {Count} pending events for review", pendingEvents.Count);

            // The review queue only exists on a companion because it is
            // projected there: capture_status says how many events are waiting,
            // and nothing but this says what they are. Published after the
            // commit and one row at a time, because each pending event is its
            // own entity on the wire — there is no batched form.
            foreach (var pendingEvent in pendingEvents)
            {
                await PublishProjectionAsync(
                    publisher => publisher.PublishPendingEventAsync(pendingEvent.PendingId),
                    "pending event", pendingEvent.PendingId);
            }

            return pendingEvents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting and creating pending events");
            throw;
        }
    }

    /// <summary>
    /// Approves a pending event and creates it as a real event.
    /// Atomic: the event row, its tag/person/location metadata and the pending-event
    /// status flip are all written in a single transaction on one context, so a
    /// failure part-way can never leave a half-approved state or duplicate events
    /// on re-approve.
    /// Once committed, the owning capture's status is republished with fresh
    /// review counts unless <paramref name="publishCaptureStatus"/> says the
    /// caller will do it itself after a batch.
    /// </summary>
    public async Task<Event> ApprovePendingEventAsync(string pendingEventId, bool publishCaptureStatus = true)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var pendingEvent = await dbContext.PendingEvents
                .FirstOrDefaultAsync(pe => pe.PendingId == pendingEventId);

            if (pendingEvent == null)
            {
                throw new Exception($"Pending event {pendingEventId} not found");
            }

            if (pendingEvent.IsApproved)
            {
                throw new InvalidOperationException(
                    $"Pending event {pendingEventId} has already been approved");
            }

            // Minimal validation before writing anything
            if (string.IsNullOrWhiteSpace(pendingEvent.Title))
            {
                throw new InvalidOperationException("Cannot approve a pending event without a title");
            }

            if (pendingEvent.StartDate == default)
            {
                throw new InvalidOperationException("Cannot approve a pending event without a start date");
            }

            _logger.LogInformation("Approving pending event: {Title}", pendingEvent.Title);

            // Recover the full extraction payload (tags/people/locations/sourceText)
            var extracted = TryDeserializeExtractedData(pendingEvent.ExtractedData);

            await using var transaction = await dbContext.Database.BeginTransactionAsync();

            var realEvent = new Event
            {
                EventId = Guid.NewGuid().ToString(),
                Title = pendingEvent.Title,
                Description = pendingEvent.Description,
                StartDate = pendingEvent.StartDate,
                EndDate = pendingEvent.EndDate,
                DatePrecision = pendingEvent.DatePrecision,
                EarliestPossible = pendingEvent.EarliestPossible,
                LatestPossible = pendingEvent.LatestPossible,
                Category = NormalizeCategory(pendingEvent.Category),
                Confidence = pendingEvent.ConfidenceScore,
                AudioFilePath = pendingEvent.AudioFilePath,
                RawTranscript = pendingEvent.Transcript,
                // Provenance: the full extraction JSON (source text, reasoning, ...)
                ExtractionMetadata = string.IsNullOrWhiteSpace(pendingEvent.ExtractedData)
                    ? null
                    : pendingEvent.ExtractedData,
                Location = extracted?.Locations?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            dbContext.Events.Add(realEvent);

            IReadOnlyCollection<string> linkedPersonIds = Array.Empty<string>();
            if (extracted != null)
            {
                linkedPersonIds = await MapExtractedMetadataAsync(dbContext, realEvent, extracted);
            }

            // Flip pending-event status inside the same transaction
            pendingEvent.IsApproved = true;
            pendingEvent.Status = PendingStatus.Approved.ToStringValue();
            pendingEvent.ReviewedAt = DateTime.UtcNow;

            // Revision history (F12): record the Approved snapshot INSIDE the
            // approve transaction (same context, same commit) with the junction
            // names MapExtractedMetadataAsync just added. Gated on the
            // revision_history_enabled setting; failures are logged and never
            // affect the approve itself.
            if (_revisionWriter != null)
            {
                await _revisionWriter.TryAddApprovedToContextAsync(dbContext, realEvent);
            }

            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation("Pending event approved and created: {EventId}", realEvent.EventId);

            // Notify the UI (singleton TimelineViewModel refreshes without renavigation)
            try
            {
                WeakReferenceMessenger.Default.Send(
                    new EventCreatedMessage(realEvent.EventId, realEvent.StartDate));
            }
            catch (Exception msgEx)
            {
                _logger.LogWarning(msgEx, "Failed to publish EventCreatedMessage for {EventId}", realEvent.EventId);
            }

            // An approval moves a memory between two projected collections and
            // touches the contact book on the way, so it publishes the event,
            // then its people, then the tombstone — in that order.
            //
            // The event upsert goes first: if only one of the two reaches the
            // companion, an item that lingers in the review queue while also
            // appearing on the timeline is a stale duplicate the next decision
            // clears, whereas a queue entry that vanishes with no event to
            // replace it is indistinguishable from losing the memory.
            //
            // The people go between the two, not after, because outbox delivery
            // is ordered and an event payload carries person IDS only — names
            // come from the person projection. A batch truncated after the
            // tombstone would leave a companion with a settled-looking timeline
            // entry naming people it cannot resolve, and nothing pending to
            // suggest more is coming; truncated after the people it shows a
            // duplicate queue item, which is the cost already accepted above.
            // Approval is also the moment these contacts first become
            // publishable: a person minted here has never been projected, and
            // an existing one's EventCount just moved with the junction row.
            //
            // The tombstone is what removes it from the queue. The row itself
            // survives carrying IsApproved/Status, but the pending-event
            // projection has no status field, so republishing it as an upsert
            // would be byte-identical to the one already published and dropped
            // as unchanged — the companion would show a resolved item forever.
            // "No longer awaiting review" is a deletion as far as the queue is
            // concerned, and that is the collection being projected.
            //
            // Neither publish is gated on publishCaptureStatus: that flag
            // exists because a batch's many approvals collapse into one capture
            // status, while these two describe individual entities and have
            // nothing to coalesce.
            await PublishProjectionAsync(
                publisher => publisher.PublishEventAsync(realEvent.EventId), "event", realEvent.EventId);
            foreach (var personId in linkedPersonIds)
            {
                // Always an upsert. A merged-away contact keeps its row and its
                // MergedIntoId pointer, and the resolution above deliberately
                // follows a broken chain back to a tombstone rather than mint a
                // colliding name — so a tombstone id can legitimately land here,
                // and publishing it as anything but an upsert would strand the
                // events that reference it.
                await PublishProjectionAsync(
                    publisher => publisher.PublishPersonAsync(personId), "person", personId);
            }

            await PublishProjectionAsync(
                publisher => publisher.PublishDeletedAsync(
                    TimelineProjectionEntity.PendingEvent, pendingEventId),
                "pending event deletion", pendingEventId);

            // The phone is watching this capture's review counts (design §19
            // Phase 3); nothing else republishes them once extraction is done.
            if (publishCaptureStatus)
            {
                await PublishCaptureStatusAsync(pendingEvent.QueueId ?? string.Empty);
            }

            // Kick off embedding generation in the background, unless the user
            // turned automatic embedding off (auto_generate_embeddings); it must
            // never affect the approve flow and logs its own errors.
            var approvedEventId = realEvent.EventId;
            if (await _eventService.IsAutoEmbeddingEnabledAsync())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _eventService.GenerateEmbeddingAsync(approvedEventId);
                    }
                    catch (Exception embedEx)
                    {
                        _logger.LogError(embedEx,
                            "Background embedding generation failed for approved event {EventId}", approvedEventId);
                    }
                });
            }
            else
            {
                _logger.LogDebug("Automatic embedding is disabled; approved event {EventId} is not embedded", approvedEventId);
            }

            return realEvent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving pending event");
            throw;
        }
    }

    /// <summary>
    /// Updates a pending event. Loads the tracked row by id in a fresh context and
    /// copies the editable fields (no Update() on a detached clone).
    /// </summary>
    public async Task<PendingEvent> UpdatePendingEventAsync(PendingEvent pendingEvent)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var tracked = await dbContext.PendingEvents
                .FirstOrDefaultAsync(pe => pe.PendingId == pendingEvent.PendingId);

            if (tracked == null)
            {
                throw new Exception($"Pending event {pendingEvent.PendingEventId} not found");
            }

            tracked.Title = pendingEvent.Title;
            tracked.Description = pendingEvent.Description;
            tracked.StartDate = pendingEvent.StartDate;
            tracked.EndDate = pendingEvent.EndDate;
            tracked.DatePrecision = pendingEvent.DatePrecision;
            tracked.EarliestPossible = pendingEvent.EarliestPossible;
            tracked.LatestPossible = pendingEvent.LatestPossible;
            tracked.Category = pendingEvent.Category;
            tracked.ConfidenceScore = pendingEvent.ConfidenceScore;

            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Updated pending event: {PendingEventId}", tracked.PendingEventId);

            // A reviewer corrected the extraction before deciding on it; the
            // companion's copy of the queue must show what will actually be
            // approved, not the title the model first guessed at.
            await PublishProjectionAsync(
                publisher => publisher.PublishPendingEventAsync(tracked.PendingId),
                "pending event", tracked.PendingId);

            return tracked;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating pending event");
            throw;
        }
    }

    /// <summary>
    /// Rejects and deletes a pending event. Once committed, the owning capture's
    /// status is republished with fresh review counts unless
    /// <paramref name="publishCaptureStatus"/> says the caller will do it itself
    /// after a batch.
    /// </summary>
    public async Task RejectPendingEventAsync(string pendingEventId, bool publishCaptureStatus = true)
    {
        string? queueId = null;
        var deleted = false;

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var pendingEvent = await dbContext.PendingEvents
                .FirstOrDefaultAsync(pe => pe.PendingId == pendingEventId);

            if (pendingEvent != null)
            {
                queueId = pendingEvent.QueueId;
                dbContext.PendingEvents.Remove(pendingEvent);
                await dbContext.SaveChangesAsync();
                deleted = true;

                _logger.LogInformation("Rejected and deleted pending event: {PendingEventId}", pendingEventId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting pending event");
            throw;
        }

        // The row is gone, so only its id survives to tell a companion to drop
        // its copy — a rejection leaves nothing to render, and no event follows.
        // Tracked separately from queueId because a pending event may carry no
        // queue (imported text), and that is still a real deletion to project.
        if (deleted)
        {
            await PublishProjectionAsync(
                publisher => publisher.PublishDeletedAsync(
                    TimelineProjectionEntity.PendingEvent, pendingEventId),
                "pending event deletion", pendingEventId);
        }

        // Rejecting the last pending event finishes the review just as approving
        // it does, so the capture reaches `completed` either way.
        if (publishCaptureStatus && queueId != null)
        {
            await PublishCaptureStatusAsync(queueId);
        }
    }

    /// <summary>
    /// Republishes one capture's processing status after its review moved:
    /// refreshed pending/approved counts, and — once nothing is left to review —
    /// the queue item advanced to <see cref="QueueProcessingStage.Completed"/>
    /// so the phone stops showing "Ready for review" for a review that is over
    /// (design §19 Phase 3).
    ///
    /// Bulk review paths call this once per affected capture after the batch
    /// instead of once per event. The publisher is an optional dependency: with
    /// none registered this is a no-op, and a publish that fails is logged and
    /// swallowed — approving an event must never fail because a status could not
    /// be projected, and the next transition republishes anyway.
    /// </summary>
    public async Task PublishCaptureStatusAsync(string queueId)
    {
        if (_statusPublisher == null || string.IsNullOrWhiteSpace(queueId))
        {
            return;
        }

        try
        {
            var queueItem = await _queueRepository.GetByIdAsync(queueId);
            if (queueItem == null)
            {
                _logger.LogDebug("Queue item {QueueId} no longer exists; no status is published", queueId);
                return;
            }

            // Only review_ready is ours to finish. A failed or still-processing
            // item is the queue pipeline's business, and forcing it to completed
            // here would tell the phone the capture is done when it is not.
            if (queueItem.ProcessingStage == QueueProcessingStage.ReviewReady &&
                await CountPendingReviewAsync(queueId) == 0)
            {
                queueItem.Status = QueueStatus.Completed;
                queueItem.ProcessingStage = QueueProcessingStage.Completed;
                queueItem.ProcessedAt ??= DateTime.UtcNow;
                await _queueRepository.UpdateAsync(queueItem);

                _logger.LogInformation(
                    "Queue item {QueueId} has no events left to review; marked completed", queueId);
            }

            await _statusPublisher.PublishAsync(queueItem);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish capture status for queue item {QueueId}", queueId);
        }
    }

    /// <summary>
    /// Runs one timeline projection publish, on the same terms as
    /// <see cref="PublishCaptureStatusAsync"/>: a no-op when no publisher is
    /// registered, and a failure is logged and swallowed.
    ///
    /// Swallowing is the point. Every caller invokes this AFTER its archive
    /// write has committed, so rethrowing would report a failure for work that
    /// already happened — the user would be told their approval failed while
    /// the event sits in the timeline. A lost projection costs a companion one
    /// stale row until the next publish for that entity; a lost approval costs
    /// a memory.
    ///
    /// Callers never have to judge whether a change is worth publishing: the
    /// publisher drops a projection identical to the last one for the same
    /// entity.
    /// </summary>
    /// <param name="publish">The publish to attempt.</param>
    /// <param name="entity">What is being projected, for the failure log.</param>
    /// <param name="entityId">Id of the projected entity, for the failure log.</param>
    private async Task PublishProjectionAsync(
        Func<ITimelineProjectionPublisher, Task> publish, string entity, string entityId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await publish(_projectionPublisher);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish the {Entity} projection for {EntityId}", entity, entityId);
        }
    }

    /// <summary>
    /// Gets all pending events for a queue item.
    /// </summary>
    public async Task<List<PendingEvent>> GetPendingEventsForQueueAsync(string queueId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        return await dbContext.PendingEvents
            .AsNoTracking()
            .Where(pe => pe.QueueId == queueId)
            .OrderByDescending(pe => pe.ConfidenceScore)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all pending events awaiting review.
    /// </summary>
    public async Task<List<PendingEvent>> GetAllPendingEventsAsync()
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        return await dbContext.PendingEvents
            .AsNoTracking()
            .Where(pe => !pe.IsApproved)
            .OrderByDescending(pe => pe.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Gets count of pending events by approval status.
    /// </summary>
    public async Task<int> GetPendingEventCountAsync(bool? isApproved = null)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var query = dbContext.PendingEvents.AsQueryable();

        if (isApproved.HasValue)
        {
            query = query.Where(pe => pe.IsApproved == isApproved.Value);
        }

        return await query.CountAsync();
    }

    /// <summary>
    /// Computes create/update suggestions for the people mentioned in a pending
    /// event. Names are sourced from the extraction payload's PeopleDetails
    /// (falling back to the flat People list) and matched against existing
    /// contacts via <see cref="IPersonService.FindBestMatchAsync"/>.
    /// </summary>
    public async Task<List<PersonSuggestionDto>> GetPersonSuggestionsAsync(string pendingEventId)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var pendingEvent = await dbContext.PendingEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(pe => pe.PendingId == pendingEventId);

            if (pendingEvent == null)
            {
                throw new Exception($"Pending event {pendingEventId} not found");
            }

            var extracted = TryDeserializeExtractedData(pendingEvent.ExtractedData);
            if (extracted == null)
            {
                return new List<PersonSuggestionDto>();
            }

            // Source names from PeopleDetails, falling back to the flat People list
            var detailNames = DistinctNames(extracted.PeopleDetails?.Select(d => d.Name)).ToList();
            var names = detailNames.Count > 0
                ? detailNames
                : DistinctNames(extracted.People).ToList();

            var suggestions = new List<PersonSuggestionDto>();

            foreach (var name in names)
            {
                var detail = FindPersonDetail(extracted.PeopleDetails, name);

                var suggestion = new PersonSuggestionDto
                {
                    PendingEventId = pendingEventId,
                    Name = name,
                    SuggestedRelationship = NullIfWhiteSpace(detail?.Relationship),
                    SuggestedDetails = NullIfWhiteSpace(detail?.Details)
                };

                var match = await _personService.FindBestMatchAsync(name);

                if (match == null)
                {
                    suggestion.Kind = PersonSuggestionKind.NewPerson;
                }
                else
                {
                    suggestion.MatchedPersonId = match.Person.PersonId;
                    suggestion.MatchedPersonName = match.Person.Name;

                    var addsRelationship = !string.IsNullOrWhiteSpace(suggestion.SuggestedRelationship)
                        && string.IsNullOrWhiteSpace(match.Person.Relationship);
                    var addsDetails = !string.IsNullOrWhiteSpace(suggestion.SuggestedDetails)
                        && string.IsNullOrWhiteSpace(match.Person.Notes);

                    suggestion.Kind = addsRelationship || addsDetails
                        ? PersonSuggestionKind.UpdateDetails
                        : PersonSuggestionKind.KnownPerson;
                }

                suggestions.Add(suggestion);
            }

            _logger.LogInformation("Computed {Count} person suggestions for pending event {PendingEventId}",
                suggestions.Count, pendingEventId);

            return suggestions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing person suggestions for pending event {PendingEventId}", pendingEventId);
            throw;
        }
    }

    /// <summary>
    /// Applies one person suggestion: creates the person (NewPerson), fills only
    /// the missing fields on the matched person (UpdateDetails), or does nothing
    /// (KnownPerson). Duplicate-create races resolve to the existing person.
    /// Always returns the suggestion with IsApplied set to true.
    /// </summary>
    public async Task<PersonSuggestionDto> ApplyPersonSuggestionAsync(PersonSuggestionDto suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        try
        {
            switch (suggestion.Kind)
            {
                case PersonSuggestionKind.NewPerson:
                    try
                    {
                        var created = await _personService.CreatePersonAsync(new PersonDto
                        {
                            Name = suggestion.Name,
                            Relationship = suggestion.SuggestedRelationship,
                            Notes = suggestion.SuggestedDetails
                        });

                        suggestion.MatchedPersonId = created.PersonId;
                        suggestion.MatchedPersonName = created.Name;

                        _logger.LogInformation("Created person '{Name}' from suggestion", created.Name);
                    }
                    catch (InvalidOperationException)
                    {
                        // Duplicate-create race: another caller created the person
                        // first — resolve to the existing contact.
                        _logger.LogInformation("Person '{Name}' already exists; resolving suggestion to existing contact",
                            suggestion.Name);

                        var existing = await _personService.FindBestMatchAsync(suggestion.Name);
                        if (existing != null)
                        {
                            suggestion.MatchedPersonId = existing.Person.PersonId;
                            suggestion.MatchedPersonName = existing.Person.Name;
                        }
                    }
                    break;

                case PersonSuggestionKind.UpdateDetails:
                    if (!string.IsNullOrWhiteSpace(suggestion.MatchedPersonId))
                    {
                        var person = await _personService.GetPersonAsync(suggestion.MatchedPersonId);
                        if (person != null)
                        {
                            var changed = false;

                            // Fill ONLY missing fields; never overwrite existing data
                            if (string.IsNullOrWhiteSpace(person.Relationship)
                                && !string.IsNullOrWhiteSpace(suggestion.SuggestedRelationship))
                            {
                                person.Relationship = suggestion.SuggestedRelationship;
                                changed = true;
                            }

                            if (string.IsNullOrWhiteSpace(person.Notes)
                                && !string.IsNullOrWhiteSpace(suggestion.SuggestedDetails))
                            {
                                person.Notes = suggestion.SuggestedDetails;
                                changed = true;
                            }

                            if (changed)
                            {
                                await _personService.UpdatePersonAsync(person);
                                _logger.LogInformation("Updated person '{Name}' with suggested details", person.Name);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Matched person {PersonId} no longer exists; suggestion not applied",
                                suggestion.MatchedPersonId);
                        }
                    }
                    break;

                case PersonSuggestionKind.KnownPerson:
                    // Nothing to apply — the contact already exists with no new details
                    break;
            }

            suggestion.IsApplied = true;
            return suggestion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying person suggestion for '{Name}'", suggestion.Name);
            throw;
        }
    }

    #region Private Methods

    /// <summary>
    /// Events still awaiting review for one queue item. Counts by
    /// <see cref="PendingEvent.Status"/> rather than
    /// <see cref="PendingEvent.IsApproved"/> so it agrees with the publisher's
    /// own pending/approved split (rejected rows are deleted outright).
    /// </summary>
    private async Task<int> CountPendingReviewAsync(string queueId)
    {
        var pendingReview = PendingStatus.PendingReview.ToStringValue();

        await using var dbContext = await _contextFactory.CreateDbContextAsync();
        return await dbContext.PendingEvents
            .CountAsync(pe => pe.QueueId == queueId && pe.Status == pendingReview);
    }

    /// <summary>
    /// Verifies the ACTIVE LLM provider is configured; throws a non-retryable
    /// ConfigurationException when it is not, so the queue fails the item
    /// immediately instead of burning retries. Provider-aware (F11): the
    /// OpenAI-compatible provider needs a base URL (no API key), Anthropic
    /// needs its API key.
    /// </summary>
    private async Task EnsureLlmConfiguredAsync()
    {
        var provider = LlmProviderKeys.Normalize(await _settingsService.GetLlmProviderAsync());

        if (provider == LlmProviderKeys.OpenAiCompatible)
        {
            var baseUrl = await _settingsService.GetSettingAsync<string>(SettingKeys.LlmBaseUrl, string.Empty);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ConfigurationException(MissingBaseUrlMessage);
            }
            return;
        }

        var apiKey = await _settingsService.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ConfigurationException(MissingApiKeyMessage);
        }
    }

    /// <summary>
    /// Deserializes the stored extraction payload; returns null (with a logged
    /// warning) on malformed data instead of failing the approval.
    /// </summary>
    private ExtractedEvent? TryDeserializeExtractedData(string? extractedData)
    {
        if (string.IsNullOrWhiteSpace(extractedData))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ExtractedEvent>(
                extractedData,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed ExtractedData on pending event; approving without metadata");
            return null;
        }
    }

    /// <summary>
    /// Maps extracted tags/people/locations onto the real entity + junction tables.
    /// Upserts Tag/Person/Location by name (checking both the database and rows
    /// already added to this context), then adds junction rows. All work happens
    /// on the caller's context so it participates in the approve transaction.
    ///
    /// Returns the person ids this linked to the event, resolved and
    /// de-duplicated. The caller needs them to project those people once the
    /// transaction commits: a person created here exists on no companion at
    /// all, and an existing one's linked-event count just moved.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> MapExtractedMetadataAsync(
        Data.AppDbContext dbContext, Event realEvent, ExtractedEvent extracted)
    {
        // Tags -> tags + event_tags
        foreach (var rawTag in DistinctNames(extracted.Tags))
        {
            var tag = dbContext.Tags.Local
                    .FirstOrDefault(t => string.Equals(t.TagName, rawTag, StringComparison.OrdinalIgnoreCase))
                ?? await dbContext.Tags
                    .FirstOrDefaultAsync(t => t.TagName.ToLower() == rawTag.ToLower());

            if (tag == null)
            {
                tag = new Tag
                {
                    TagId = Guid.NewGuid().ToString(),
                    TagName = rawTag,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Tags.Add(tag);
            }

            dbContext.EventTags.Add(new EventTag
            {
                EventId = realEvent.EventId,
                TagId = tag.TagId,
                Event = realEvent,
                Tag = tag,
                ConfidenceScore = extracted.Confidence,
                IsManual = false,
                CreatedAt = DateTime.UtcNow
            });
        }

        // People -> people + event_people. The lookup must be alias-aware and
        // tombstone-aware BEFORE creating: under the NOCASE unique name index
        // a case-variant create ("sarah" next to "Sarah") throws, and a name
        // matching an alias ("Bob" for "Robert") must link the existing
        // contact instead of minting a duplicate.
        var linkedPersonIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPerson in DistinctNames(extracted.People))
        {
            var person = await ResolvePersonForApprovalAsync(dbContext, rawPerson);

            if (person == null)
            {
                // Enrich brand-new person rows from the per-person extraction details
                var detail = FindPersonDetail(extracted.PeopleDetails, rawPerson);

                person = new Person
                {
                    PersonId = Guid.NewGuid().ToString(),
                    Name = rawPerson,
                    Relationship = NullIfWhiteSpace(detail?.Relationship),
                    Notes = NullIfWhiteSpace(detail?.Details),
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.People.Add(person);

                try
                {
                    // Flush the insert now (still inside the caller's approve
                    // transaction) so a NOCASE unique-index collision surfaces
                    // here instead of failing the whole approval at the final
                    // SaveChanges.
                    await dbContext.SaveChangesAsync();
                }
                catch (DbUpdateException ex)
                {
                    // SQLite's NOCASE folding is ASCII-only while the C#
                    // lookup above pre-folds the parameter with full-Unicode
                    // ToLowerInvariant, so for some non-ASCII case pairs
                    // (e.g. 'İ' U+0130) the lookup misses a row the unique
                    // index still treats as equal. Drop the rejected insert
                    // and link the existing row instead, re-running the
                    // lookup with the index's own folding (COLLATE NOCASE).
                    dbContext.Entry(person).State = EntityState.Detached;

                    var existing = await dbContext.People
                        .FirstOrDefaultAsync(p => EF.Functions.Collate(p.Name, "NOCASE") == rawPerson);
                    if (existing == null)
                    {
                        // Not a name collision after all - surface the
                        // original failure.
                        throw;
                    }

                    _logger.LogWarning(ex,
                        "Person insert for '{RawName}' hit the NOCASE unique index; linking existing person '{ExistingName}' ({PersonId}) instead",
                        rawPerson, existing.Name, existing.PersonId);
                    person = await FollowMergeChainAsync(dbContext, existing);
                }
            }

            // Two extracted spellings ("Bob" and "Robert") can resolve to the
            // same contact; guard the composite (event, person) primary key.
            if (!linkedPersonIds.Add(person.PersonId))
            {
                continue;
            }

            dbContext.EventPeople.Add(new EventPerson
            {
                EventId = realEvent.EventId,
                PersonId = person.PersonId,
                Event = realEvent,
                Person = person,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Locations -> locations + event_locations
        foreach (var rawLocation in DistinctNames(extracted.Locations))
        {
            var location = dbContext.Locations.Local
                    .FirstOrDefault(l => string.Equals(l.Name, rawLocation, StringComparison.OrdinalIgnoreCase))
                ?? await dbContext.Locations
                    .FirstOrDefaultAsync(l => l.Name.ToLower() == rawLocation.ToLower());

            if (location == null)
            {
                location = new Location
                {
                    LocationId = Guid.NewGuid().ToString(),
                    Name = rawLocation,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Locations.Add(location);
            }

            dbContext.EventLocations.Add(new EventLocation
            {
                EventId = realEvent.EventId,
                LocationId = location.LocationId,
                Event = realEvent,
                Location = location,
                CreatedAt = DateTime.UtcNow
            });
        }

        return linkedPersonIds;
    }

    /// <summary>
    /// Resolves an extracted person name to an existing contact for the
    /// approval path: rows already added to this context (case-insensitive),
    /// then a case-insensitive name lookup, then a case-insensitive alias
    /// lookup — finally following any merge-tombstone chain to the living
    /// person (visited set guards a malformed cycle; a broken chain returns
    /// the last person reached rather than creating a name that would
    /// collide with the NOCASE unique index). Null means "genuinely new".
    /// </summary>
    private async Task<Person?> ResolvePersonForApprovalAsync(Data.AppDbContext dbContext, string rawName)
    {
        var lowered = rawName.ToLowerInvariant();

        var person = dbContext.People.Local
                .FirstOrDefault(p => string.Equals(p.Name, rawName, StringComparison.OrdinalIgnoreCase))
            ?? await dbContext.People
                .FirstOrDefaultAsync(p => p.Name.ToLower() == lowered);

        if (person == null)
        {
            var aliasOwnerId = await dbContext.PersonAliases.AsNoTracking()
                .Where(a => a.Alias.ToLower() == lowered)
                .Select(a => a.PersonId)
                .FirstOrDefaultAsync();
            if (aliasOwnerId != null)
            {
                person = await dbContext.People
                    .FirstOrDefaultAsync(p => p.PersonId == aliasOwnerId);
            }
        }

        return person == null ? null : await FollowMergeChainAsync(dbContext, person);
    }

    /// <summary>
    /// Follows a person's merge-tombstone chain to the living person. The
    /// visited set guards a malformed cycle; a broken chain returns the last
    /// person reached (linking the tombstone beats creating a row whose name
    /// the NOCASE unique index already holds).
    /// </summary>
    private static async Task<Person> FollowMergeChainAsync(Data.AppDbContext dbContext, Person person)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (person.MergedIntoId != null && visited.Add(person.PersonId))
        {
            var nextId = person.MergedIntoId;
            var next = await dbContext.People
                .FirstOrDefaultAsync(p => p.PersonId == nextId);
            if (next == null)
            {
                // Broken chain: link the tombstone rather than create a row
                // whose name the unique index already holds.
                break;
            }

            person = next;
        }

        return person;
    }

    private static IEnumerable<string> DistinctNames(IEnumerable<string>? names)
    {
        return (names ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the extracted per-person detail matching a name (OrdinalIgnoreCase, trimmed).
    /// </summary>
    private static ExtractedPersonDetail? FindPersonDetail(
        IEnumerable<ExtractedPersonDetail>? details, string name)
    {
        return details?.FirstOrDefault(d =>
            !string.IsNullOrWhiteSpace(d.Name) &&
            string.Equals(d.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Builds extraction context from existing data.
    /// </summary>
    private async Task<ExtractionContext> BuildExtractionContextAsync()
    {
        var context = new ExtractionContext
        {
            ReferenceDate = DateTime.Now
        };

        try
        {
            // Get recent event titles for context
            var recentEvents = await _eventService.GetRecentEventsAsync(20);
            context.RecentEvents = recentEvents.Select(e => e.Title).ToList();

            // Known people so extraction reuses canonical spellings
            // (best-effort). People with aliases are rendered as
            // "CanonicalName (also: nickname, alias1, ...)" so Claude
            // resolves "Bob" to "Robert" at extraction time; everyone else
            // keeps the plain DisplayName ("Name (Nickname)").
            var persons = (await _personService.GetAllPersonsAsync(PersonSortOption.MostEvents))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Take(100)
                .ToList();

            var aliasesByPerson = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var personIds = persons.Select(p => p.PersonId).ToList();
            await using (var dbContext = await _contextFactory.CreateDbContextAsync())
            {
                var aliasRows = await dbContext.PersonAliases.AsNoTracking()
                    .Where(a => personIds.Contains(a.PersonId))
                    .Select(a => new { a.PersonId, a.Alias })
                    .ToListAsync();
                foreach (var row in aliasRows)
                {
                    if (!aliasesByPerson.TryGetValue(row.PersonId, out var list))
                    {
                        list = new List<string>();
                        aliasesByPerson[row.PersonId] = list;
                    }
                    list.Add(row.Alias);
                }
            }

            context.KnownPeople = persons
                .Select(p => FormatKnownPerson(p, aliasesByPerson))
                .ToList();

            return context;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error building extraction context, using minimal context");
            return context;
        }
    }

    /// <summary>
    /// Formats one known-people prompt entry: "Name (also: nickname, alias1)"
    /// when the person has aliases (the nickname joins the also-list so it is
    /// not lost), otherwise the plain DisplayName ("Name (Nickname)").
    /// Also-entries that only differ from the canonical name by case are
    /// dropped as noise.
    /// </summary>
    private static string FormatKnownPerson(
        PersonDto person,
        Dictionary<string, List<string>> aliasesByPerson)
    {
        aliasesByPerson.TryGetValue(person.PersonId, out var aliases);
        if (aliases == null || aliases.Count == 0)
        {
            return person.DisplayName;
        }

        var also = new List<string>();
        if (!string.IsNullOrWhiteSpace(person.Nickname))
        {
            also.Add(person.Nickname.Trim());
        }

        foreach (var alias in aliases)
        {
            var trimmed = alias.Trim();
            if (trimmed.Length == 0 ||
                string.Equals(trimmed, person.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                also.Any(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            also.Add(trimmed);
        }

        return also.Count == 0
            ? person.DisplayName
            : $"{person.Name} (also: {string.Join(", ", also)})";
    }

    /// <summary>
    /// Normalizes an already-stored category to a valid lowercase EventCategory value.
    /// </summary>
    private string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return EventCategory.Other;
        }

        var normalized = category.Trim().ToLowerInvariant();

        return EventCategory.AllCategories.Contains(normalized)
            ? normalized
            : ParseCategory(category);
    }

    /// <summary>
    /// Parses category string to EventCategory constant.
    /// </summary>
    private string ParseCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return EventCategory.Other;
        }

        return category.ToLowerInvariant() switch
        {
            "milestone" => EventCategory.Milestone,
            "work" => EventCategory.Work,
            "education" => EventCategory.Education,
            "health" => EventCategory.Challenge,
            "travel" => EventCategory.Travel,
            "social" => EventCategory.Relationship,
            "personal" => EventCategory.Other,
            "family" => EventCategory.Relationship,
            _ => EventCategory.Other
        };
    }

    #endregion
}
