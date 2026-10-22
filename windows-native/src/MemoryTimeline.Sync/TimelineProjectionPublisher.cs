using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="ITimelineProjectionPublisher"/> (design §19 Phase 3): projects
/// events, eras, people and the review queue into <c>event</c> / <c>era</c> /
/// <c>person</c> / <c>pending_event</c> outbox rows, which
/// <see cref="LocalOutboxPublisher"/> pushes to the sync service and a companion
/// pulls. Same shape and same discipline as
/// <see cref="CaptureStatusPublisher"/>, one layer up: that one projects how a
/// capture is being processed, this one projects what the processing produced.
///
/// <list type="bullet">
/// <item><b>Denormalised on purpose.</b> An event payload carries its tag names,
/// person IDs and location names inline, resolved from the loaded navigations.
/// A consumer of a read-only projection wants to draw a bubble, not rebuild a
/// relational graph, and syncing the junction tables would triple the change
/// volume to reproduce a join the consumer performs immediately anyway.</item>
///
/// <item><b>Dates are formatted here, once.</b>
/// <see cref="EventProjectionPayload.DisplayDate"/> comes from
/// <see cref="DateDisplay.FormatPrecise"/> — the same call the Windows UI makes
/// — so "Summer 2003" reads identically on every device. Re-deriving the rule
/// per client is precisely how a companion ends up inventing a precise day for a
/// memory the user only dated to a summer.</item>
///
/// <item><b>Unchanged projections are dropped.</b> Callers may publish after
/// every mutation; a payload that differs from the last published one only in
/// its <c>updatedAtUtc</c> stamp shows a consumer nothing new and is
/// suppressed.</item>
///
/// <item><b>Privacy (§14.5).</b> Nothing here carries a transcript body, an
/// audio or photo path, extraction metadata, or a person's contact details.
/// <see cref="PendingEventProjectionPayload.TranscriptPreview"/> and the
/// description fields are bounded to
/// <see cref="CaptureStatusChangePayload.TranscriptPreviewMaxChars"/>, the same
/// limit the capture-status preview already uses. Nothing here logs event text,
/// people, or coordinates.</item>
/// </list>
/// </summary>
public class TimelineProjectionPublisher : ITimelineProjectionPublisher
{
    /// <summary>
    /// Wire name of the timestamp field on every projection payload. Dropped
    /// before the unchanged-projection comparison: it moves whenever the row was
    /// touched, including by edits to fields no projection carries, and on its
    /// own it gives a consumer nothing to redraw.
    /// </summary>
    private const string UpdatedAtProperty = "updatedAtUtc";

    /// <summary>
    /// Bound for free text that crosses the wire. Deliberately the capture
    /// status preview's limit rather than a second number: both exist for the
    /// same §14.5 reason, and one constant cannot drift from the other.
    /// </summary>
    private const int TextPreviewMaxChars = CaptureStatusChangePayload.TranscriptPreviewMaxChars;

    private readonly ISyncOutboxRepository _outboxRepository;
    private readonly IEventRepository _eventRepository;
    private readonly IEraRepository _eraRepository;
    private readonly IPersonRepository _personRepository;
    private readonly IPendingEventRepository _pendingEventRepository;
    private readonly IEventMediaRepository _mediaRepository;
    private readonly ILogger<TimelineProjectionPublisher> _logger;

    public TimelineProjectionPublisher(
        ISyncOutboxRepository outboxRepository,
        IEventRepository eventRepository,
        IEraRepository eraRepository,
        IPersonRepository personRepository,
        IPendingEventRepository pendingEventRepository,
        IEventMediaRepository mediaRepository,
        ILogger<TimelineProjectionPublisher> logger)
    {
        _outboxRepository = outboxRepository;
        _eventRepository = eventRepository;
        _eraRepository = eraRepository;
        _personRepository = personRepository;
        _pendingEventRepository = pendingEventRepository;
        _mediaRepository = mediaRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PublishEventAsync(string eventId, CancellationToken cancellationToken = default) =>
        PublishEventCoreAsync(eventId, force: false, cancellationToken);

    /// <inheritdoc />
    public Task PublishEraAsync(string eraId, CancellationToken cancellationToken = default) =>
        PublishEraCoreAsync(eraId, force: false, cancellationToken);

    /// <inheritdoc />
    public Task PublishPersonAsync(string personId, CancellationToken cancellationToken = default) =>
        PublishPersonCoreAsync(personId, force: false, cancellationToken);

    /// <inheritdoc />
    public Task PublishPendingEventAsync(string pendingEventId, CancellationToken cancellationToken = default) =>
        PublishPendingEventCoreAsync(pendingEventId, force: false, cancellationToken);

    /// <inheritdoc />
    public async Task<TimelineProjectionBackfillResult> PublishAllAsync(
        TimelineProjectionBackfillMode mode, CancellationToken cancellationToken = default)
    {
        var force = mode == TimelineProjectionBackfillMode.Everything;
        int eras = 0, events = 0, people = 0, pendingEvents = 0, unchanged = 0;

        // Ids first, entities later, one at a time through the same code the
        // single-entity publishes use. The lists are id snapshots, so an
        // archive edited mid-run simply publishes again on its own change.
        // Order is the interface's prefix rule: eras before the events that
        // name them, events before the people whose counts they explain, the
        // review queue last.
        foreach (var era in await _eraRepository.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count(await PublishEraCoreAsync(era.EraId, force, cancellationToken), ref eras, ref unchanged);
        }

        foreach (var evt in await _eventRepository.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count(await PublishEventCoreAsync(evt.EventId, force, cancellationToken), ref events, ref unchanged);
        }

        foreach (var person in await _personRepository.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count(await PublishPersonCoreAsync(person.PersonId, force, cancellationToken), ref people, ref unchanged);
        }

        // Only what is still awaiting review: an approved or rejected pending
        // event was tombstoned when it was decided, and that tombstone is the
        // last word on it.
        foreach (var pending in await _pendingEventRepository.GetByStatusAsync(PendingStatus.PendingReview))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count(await PublishPendingEventCoreAsync(pending.PendingId, force, cancellationToken),
                ref pendingEvents, ref unchanged);
        }

        var result = new TimelineProjectionBackfillResult(eras, events, people, pendingEvents, unchanged);
        _logger.LogInformation(
            "Timeline projection backfill ({Mode}) queued {Published} projections ({Eras} eras, {Events} events, {People} people, {PendingEvents} pending events); {Unchanged} unchanged",
            mode, result.Published, eras, events, people, pendingEvents, unchanged);
        return result;

        static void Count(bool published, ref int publishedCount, ref int unchangedCount)
        {
            if (published)
            {
                publishedCount++;
            }
            else
            {
                unchangedCount++;
            }
        }
    }

    private async Task<bool> PublishEventCoreAsync(string eventId, bool force, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        cancellationToken.ThrowIfCancellationRequested();

        // The includes ARE the denormalisation source: tags, people and
        // locations are read off the navigations rather than re-queried.
        var entity = await _eventRepository.GetByIdWithIncludesAsync(eventId);
        if (entity == null)
        {
            _logger.LogDebug("Event {EventId} no longer exists; nothing is projected", eventId);
            return false;
        }

        var payload = await BuildEventPayloadAsync(entity, cancellationToken);
        return await PublishUpsertAsync(SyncEntityType.Event, eventId, payload, payload.UpdatedAtUtc, force);
    }

    private async Task<bool> PublishEraCoreAsync(string eraId, bool force, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eraId);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await _eraRepository.GetByIdAsync(eraId);
        if (entity == null)
        {
            _logger.LogDebug("Era {EraId} no longer exists; nothing is projected", eraId);
            return false;
        }

        var payload = BuildEraPayload(entity);
        return await PublishUpsertAsync(SyncEntityType.Era, eraId, payload, payload.UpdatedAtUtc, force);
    }

    private async Task<bool> PublishPersonCoreAsync(string personId, bool force, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await _personRepository.GetByIdAsync(personId);
        if (entity == null)
        {
            _logger.LogDebug("Person {PersonId} no longer exists; nothing is projected", personId);
            return false;
        }

        var payload = await BuildPersonPayloadAsync(entity);
        return await PublishUpsertAsync(SyncEntityType.Person, personId, payload, payload.UpdatedAtUtc, force);
    }

    private async Task<bool> PublishPendingEventCoreAsync(
        string pendingEventId, bool force, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingEventId);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await _pendingEventRepository.GetByIdAsync(pendingEventId);
        if (entity == null)
        {
            _logger.LogDebug(
                "Pending event {PendingEventId} no longer exists; nothing is projected", pendingEventId);
            return false;
        }

        var payload = BuildPendingEventPayload(entity);
        return await PublishUpsertAsync(
            SyncEntityType.PendingEvent, pendingEventId, payload, payload.UpdatedAtUtc, force);
    }

    /// <inheritdoc />
    public async Task PublishDeletedAsync(
        TimelineProjectionEntity entity, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        cancellationToken.ThrowIfCancellationRequested();

        var entityType = EntityTypeFor(entity);

        // A tombstone on top of a tombstone tells a consumer nothing it has not
        // already been told, and delete is the one operation that stays true no
        // matter how often it is replayed.
        var latest = await _outboxRepository.GetLatestForEntityAsync(entityType, entityId);
        if (latest != null &&
            string.Equals(latest.Operation, SyncOutboxOperation.Delete, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "{EntityType} {EntityId} is already tombstoned; not republishing the deletion",
                entityType, entityId);
            return;
        }

        await _outboxRepository.AddAsync(new SyncOutbox
        {
            EntityType = entityType,
            EntityId = entityId,
            Operation = SyncOutboxOperation.Delete,
            // A tombstone carries identity only — there is no surviving row to
            // describe, and the consumer's job is to drop its copy.
            PayloadJson = null,
            CreatedAt = DateTime.UtcNow,
        });

        _logger.LogInformation("Published deletion of {EntityType} {EntityId}", entityType, entityId);
    }

    #region Payload construction

    private async Task<EventProjectionPayload> BuildEventPayloadAsync(
        Event entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new EventProjectionPayload
        {
            EventId = entity.EventId,
            Title = entity.Title,
            Description = Bounded(entity.Description),
            StartDate = Utc(entity.StartDate),
            EndDate = Utc(entity.EndDate),
            DatePrecision = DatePrecisionParser.ToStringValue(entity.DatePrecision),
            // The SAME formatter the Windows UI binds to, so a companion renders
            // what the owning app renders instead of re-deriving the rules.
            // Formatted from the raw value: Utc() only stamps a Kind, but taking
            // the pre-Utc value keeps it obvious that this string is the archive's
            // own reading of the date and owes nothing to the wire encoding.
            DisplayDate = DateDisplay.FormatPrecise(entity.StartDate, entity.DatePrecision, entity.EndDate),
            EarliestPossible = Utc(entity.EarliestPossible),
            LatestPossible = Utc(entity.LatestPossible),
            Category = entity.Category,
            EraId = entity.EraId,
            Tags = SortedDistinct(entity.EventTags.Select(et => et.Tag?.TagName)),
            // Person IDs, not names: the person projection resolves them, and it
            // is the only thing that can also express a merge.
            PersonIds = SortedDistinct(entity.EventPeople.Select(ep => ep.PersonId)),
            Locations = SortedDistinct(entity.EventLocations.Select(el => el.Location?.Name)),
            UpdatedAtUtc = Utc(entity.UpdatedAt),
        };

        // One pin per event: the first place that actually has coordinates. A
        // second set would have nowhere to go in the contract, and an event
        // whose places are all uncoordinated simply has no map presence.
        var located = entity.EventLocations
            .Select(el => el.Location)
            .FirstOrDefault(location => location is { Latitude: not null, Longitude: not null });
        if (located != null)
        {
            payload.Latitude = located.Latitude;
            payload.Longitude = located.Longitude;
        }

        payload.MediaCount = await CountMediaAsync(entity.EventId);

        return payload;
    }

    private static EraProjectionPayload BuildEraPayload(Era entity) => new()
    {
        EraId = entity.EraId,
        Name = entity.Name,
        Subtitle = entity.Subtitle,
        StartDate = Utc(entity.StartDate),
        EndDate = Utc(entity.EndDate),
        // EffectiveColor, not ColorCode: it is the colour Windows actually
        // paints (a user's override wins over the category default), and the
        // point of a projection is that both screens agree.
        ColorCode = string.IsNullOrWhiteSpace(entity.EffectiveColor) ? "#808080" : entity.EffectiveColor,
        CategoryId = entity.CategoryId,
        CategoryName = entity.Category?.Name,
        DisplayOrder = entity.DisplayOrder,
        UpdatedAtUtc = Utc(entity.UpdatedAt),
    };

    /// <summary>
    /// Projects the identity half of a person and nothing else. Email, phone,
    /// birthday, company, notes and the avatar's file path stay on Windows:
    /// they are contact data a timeline never shows, and a photo path would be a
    /// filesystem path leaving the machine (§14.5).
    /// </summary>
    private async Task<PersonProjectionPayload> BuildPersonPayloadAsync(Person entity)
    {
        return new PersonProjectionPayload
        {
            PersonId = entity.PersonId,
            Name = entity.Name,
            Nickname = entity.Nickname,
            Relationship = entity.Relationship,
            // A merged person keeps its row and carries the pointer, so events
            // that still reference the merged-away id resolve instead of
            // dangling. This is why a merge publishes an upsert, not a delete.
            MergedIntoId = entity.MergedIntoId,
            IsFavorite = entity.IsFavorite,
            EventCount = await _personRepository.GetEventCountForPersonAsync(entity.PersonId),
            UpdatedAtUtc = Utc(entity.UpdatedAt),
        };
    }

    private static PendingEventProjectionPayload BuildPendingEventPayload(PendingEvent entity)
    {
        return new PendingEventProjectionPayload
        {
            PendingEventId = entity.PendingId,
            // The capture this was extracted from, so a companion can link the
            // queue item back to the recording it made.
            CaptureId = entity.RecordingQueue?.SourceCaptureId,
            Title = entity.Title,
            Description = Bounded(entity.Description),
            StartDate = Utc(entity.StartDate),
            EndDate = Utc(entity.EndDate),
            DatePrecision = DatePrecisionParser.ToStringValue(entity.DatePrecision),
            DisplayDate = DateDisplay.FormatPrecise(entity.StartDate, entity.DatePrecision, entity.EndDate),
            Category = entity.Category,
            ConfidenceScore = entity.ConfidenceScore,
            Tags = ParseExtractedTagNames(entity.ExtractedData),
            // Reuses the Review page's own parse (PeopleDetails names, falling
            // back to the flat list) so the phone lists exactly the people
            // Windows lists for the same pending event.
            PeopleNames = PendingEventDto.FromPendingEvent(entity).PeopleNames,
            // A bounded excerpt, never the transcript itself (§14.5).
            TranscriptPreview = Bounded(entity.Transcript),
            // Pending events have no updated_at column; the review timestamp is
            // the only moment one can change after extraction wrote it.
            UpdatedAtUtc = Utc(entity.ReviewedAt ?? entity.CreatedAt),
        };
    }

    /// <summary>
    /// Stamps <see cref="DateTimeKind.Utc"/> on a value read back from the
    /// archive, so it serializes with a trailing <c>Z</c>.
    ///
    /// <para>This is not cosmetic. Microsoft.Data.Sqlite returns every
    /// <c>DateTime</c> as <see cref="DateTimeKind.Unspecified"/>, and
    /// <c>System.Text.Json</c> writes an Unspecified value with no time-zone
    /// designator at all — <c>"2003-07-14T00:00:00"</c>. That is not valid
    /// RFC 3339, the format the wire contract specifies, and the Swift clients
    /// decode dates with <c>ISO8601DateFormatter</c>, which requires a
    /// designator and returns nil without one. The failure is silent in the
    /// worst way: the client skips the change as undecodable, the cursor still
    /// advances, sync reports success, and the timeline stays empty. Nothing
    /// short of reading the client's notice-level log would show why.</para>
    ///
    /// <para><c>capture_status</c> never hit this because
    /// <c>CaptureStatusPublisher</c> stamps <c>DateTime.UtcNow</c>, which
    /// already carries Kind=Utc. Every payload built from stored rows has to do
    /// it explicitly.</para>
    ///
    /// <para><b>What this does and does not claim.</b> For <c>UpdatedAt</c> and
    /// <c>CreatedAt</c> it restores the truth: those columns are written from
    /// <c>DateTime.UtcNow</c> and only lost their Kind in transit through
    /// SQLite. For <c>StartDate</c> and friends it is a convention, not a fact —
    /// an event dated "Summer 2003" is a calendar date with no time zone, and
    /// pinning it to UTC midnight is simply the encoding chosen so the value can
    /// cross a wire that demands an offset. Clients must render
    /// <c>DisplayDate</c>, which Windows formats, rather than localizing these
    /// instants; doing the latter is how "July 14" becomes "July 13, 8pm" west
    /// of Greenwich.</para>
    /// </summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>
    /// Tag names from the stored extraction payload. Parsed defensively and
    /// case-insensitively: the JSON was written with default (PascalCase)
    /// options, and a malformed payload must cost the projection its tags, not
    /// the whole publish.
    /// </summary>
    private static List<string> ParseExtractedTagNames(string? extractedData)
    {
        if (string.IsNullOrWhiteSpace(extractedData))
        {
            return new List<string>();
        }

        try
        {
            var extracted = JsonSerializer.Deserialize<ExtractedEvent>(extractedData, ExtractedDataJson);
            return SortedDistinct(extracted?.Tags);
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private static readonly JsonSerializerOptions ExtractedDataJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private async Task<int> CountMediaAsync(string eventId)
    {
        try
        {
            var counts = await _mediaRepository.GetCountsForEventsAsync(new[] { eventId });
            return counts.TryGetValue(eventId, out var count) ? count : 0;
        }
        catch (Exception ex)
        {
            // The badge is decoration on the event, never the reason to lose it.
            _logger.LogWarning(ex, "Could not count media for event {EventId}", eventId);
            return 0;
        }
    }

    /// <summary>
    /// Trimmed, de-duplicated and ordered names. Ordering is what makes the
    /// unchanged-projection check trustworthy: without it, two reads that return
    /// the same junction rows in a different order would look like a change and
    /// republish forever.
    /// </summary>
    private static List<string> SortedDistinct(IEnumerable<string?>? values) =>
        (values ?? Enumerable.Empty<string?>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Leading excerpt of at most <see cref="TextPreviewMaxChars"/> characters.
    /// Cuts one character short rather than splitting a surrogate pair, so the
    /// excerpt is always well-formed text (same rule as the capture-status
    /// transcript preview).
    /// </summary>
    private static string? Bounded(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= TextPreviewMaxChars)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[TextPreviewMaxChars - 1])
            ? TextPreviewMaxChars - 1
            : TextPreviewMaxChars;
        return text[..length];
    }

    private static string EntityTypeFor(TimelineProjectionEntity entity) => entity switch
    {
        TimelineProjectionEntity.Event => SyncEntityType.Event,
        TimelineProjectionEntity.Era => SyncEntityType.Era,
        TimelineProjectionEntity.Person => SyncEntityType.Person,
        TimelineProjectionEntity.PendingEvent => SyncEntityType.PendingEvent,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, "Unknown projection entity"),
    };

    #endregion

    #region Outbox writing

    /// <summary>
    /// Writes one upsert row, unless the projection is indistinguishable from
    /// the last one published for the same entity. With <paramref name="force"/>
    /// (a backfill for a new registration or a restored archive) only a row
    /// that is still UNDELIVERED counts as "already published": a delivered
    /// row describes a server or an archive state that no longer applies, so
    /// it is sent again. Returns whether a row was written.
    /// </summary>
    private async Task<bool> PublishUpsertAsync<TPayload>(
        string entityType, string entityId, TPayload payload, DateTime updatedAtUtc, bool force = false)
    {
        var payloadJson = JsonSerializer.Serialize(payload, SyncJson.Options);

        var latest = await _outboxRepository.GetLatestForEntityAsync(entityType, entityId);
        if (latest != null &&
            string.Equals(latest.Operation, SyncOutboxOperation.Upsert, StringComparison.OrdinalIgnoreCase) &&
            (!force || latest.DeliveredAt == null) &&
            IsUnchanged(latest.PayloadJson, payloadJson))
        {
            _logger.LogDebug(
                "{EntityType} {EntityId} projection is unchanged; not republishing", entityType, entityId);
            return false;
        }

        await _outboxRepository.AddAsync(new SyncOutbox
        {
            EntityType = entityType,
            EntityId = entityId,
            Operation = SyncOutboxOperation.Upsert,
            PayloadJson = payloadJson,
            CreatedAt = updatedAtUtc,
        });

        _logger.LogInformation("Published {EntityType} projection for {EntityId}", entityType, entityId);
        return true;
    }

    /// <summary>
    /// Compares two serialized projections ignoring <c>updatedAtUtc</c> — the
    /// only field that can move while a consumer would draw exactly the same
    /// thing. Everything else is compared, so an added tag, a renamed person or
    /// a new media attachment still reaches the companion even when the
    /// entity's own timestamp did not move.
    ///
    /// An unreadable previous payload proves nothing is unchanged, so it counts
    /// as changed and the projection is published again.
    /// </summary>
    private bool IsUnchanged(string? previousJson, string nextJson)
    {
        if (string.IsNullOrWhiteSpace(previousJson))
        {
            return false;
        }

        try
        {
            return string.Equals(
                WithoutTimestamp(previousJson), WithoutTimestamp(nextJson), StringComparison.Ordinal);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "A previously published projection could not be read; publishing again to be safe");
            return false;
        }
    }

    private static string WithoutTimestamp(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return json;
        }

        root.Remove(UpdatedAtProperty);
        return root.ToJsonString();
    }

    #endregion
}
