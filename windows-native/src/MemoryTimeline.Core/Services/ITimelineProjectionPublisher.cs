namespace MemoryTimeline.Core.Services;

/// <summary>
/// The archive entities that are projected onto the sync feed. Used by
/// <see cref="ITimelineProjectionPublisher.PublishDeletedAsync"/>, where the row
/// is already gone and only its identity survives to be tombstoned.
/// </summary>
public enum TimelineProjectionEntity
{
    /// <summary>A timeline event.</summary>
    Event,

    /// <summary>A life period.</summary>
    Era,

    /// <summary>A contact-book person.</summary>
    Person,

    /// <summary>An extracted event awaiting review.</summary>
    PendingEvent,
}

/// <summary>
/// How <see cref="ITimelineProjectionPublisher.PublishAllAsync"/> treats a
/// projection the outbox has already delivered.
/// </summary>
public enum TimelineProjectionBackfillMode
{
    /// <summary>
    /// Queue only what the outbox never sent, or sent in a different shape:
    /// the ordinary unchanged-projection suppression applies to delivered rows
    /// too. The cheap catch-up for an archive that predates projection
    /// publishing, when the server it synced with is still the one it talks to.
    /// </summary>
    MissingOrChanged,

    /// <summary>
    /// Queue every projection again, whatever the outbox says was delivered,
    /// skipping only rows still waiting in the outbox with identical content.
    /// For a new registration or a restored archive: the outbox's memory of
    /// what was delivered describes a server, a device identity or an archive
    /// that no longer matches, so "already sent" is no longer true.
    /// </summary>
    Everything,
}

/// <summary>Outcome of one backfill run.</summary>
/// <param name="Eras">Era projections queued.</param>
/// <param name="Events">Event projections queued.</param>
/// <param name="People">Person projections queued.</param>
/// <param name="PendingEvents">Pending-event projections queued.</param>
/// <param name="Unchanged">Rows left alone because the outbox already carries an identical projection.</param>
public sealed record TimelineProjectionBackfillResult(
    int Eras, int Events, int People, int PendingEvents, int Unchanged)
{
    /// <summary>Everything queued, across entity types.</summary>
    public int Published => Eras + Events + People + PendingEvents;
}

/// <summary>
/// Projects the Windows archive — events, eras, people and the review queue —
/// onto the sync feed so a companion device can render a timeline it does not
/// own (design §19 Phase 3).
///
/// The port is declared here, next to <see cref="ICaptureStatusPublisher"/> and
/// for the same reason: the services that mutate the archive must be able to say
/// "this changed" without Core taking a dependency on the sync contracts. The
/// implementation lives in MemoryTimeline.Sync, writes rows into the local sync
/// outbox, and <c>LocalOutboxPublisher</c> pushes them.
///
/// <para><b>Direction.</b> Windows publishes; companions consume. These are
/// read-only projections, never instructions — Windows remains the only writer
/// and the only editing surface. The single write that travels the other way is
/// a pending-event decision, which the sync layer applies through the same
/// approval path the Windows UI uses.</para>
///
/// <para><b>Idempotence.</b> Every method is safe to call after every mutation:
/// a projection that is byte-identical to the last one published for the same
/// entity is dropped rather than re-published, exactly as
/// <see cref="ICaptureStatusPublisher"/> no-ops on an unchanged status. Callers
/// therefore never need to work out whether a change was "interesting enough".
/// </para>
///
/// <para><b>Privacy (§14.5).</b> Implementations publish what a user reads on a
/// timeline — titles, dates, categories, names — and never transcript bodies,
/// audio, file paths, or contact details. Text that can be arbitrarily long is
/// bounded by the publisher.</para>
///
/// <para><b>Where to publish from, as a rule.</b> A projection is only as good
/// as its call sites, and a missing one is invisible from here — the companion
/// shows something stale and nothing is logged on either side. An audit of every
/// writer produced these, and they are worth knowing before adding a call:
/// <list type="bullet">
/// <item><description><b>Publish the event, then every person whose links
/// moved.</b> A person's <c>EventCount</c> comes from the <c>event_people</c>
/// junction, which <c>IEventService</c> and <c>EventExtractionService</c> write
/// far more often than <c>IPersonService</c> does. Only paths that touch that
/// junction publish people; a title or tag edit moves nobody's count.</description></item>
/// <item><description><b>Order matters, because outbox delivery is ordered and
/// a truncated batch is a prefix.</b> Event before people: the other way round
/// shows a count that disagrees with the events the companion is holding, which
/// is a contradiction visible in its own data rather than mere lag. On delete
/// the tombstone leads. On approval the people go between the event and the
/// pending-event tombstone.</description></item>
/// <item><description><b>On delete, read the ids before the write.</b> Both
/// <c>event_people</c> foreign keys are <c>ON DELETE CASCADE</c> and SQLite
/// performs the cascade, so there is nothing left to read afterwards. Note that
/// the EF InMemory provider does <i>not</i> cascade, so a read-too-late bug
/// passes an InMemory test and publishes nothing in production — that case needs
/// a file-backed fixture.</description></item>
/// <item><description><b>Bulk writers publish once, at the end, from collected
/// ids.</b> <c>ImportService</c> stages a whole file into one context and commits
/// with a single <c>SaveChangesAsync</c>; the publisher reads entities back
/// through repositories on their own connections, so a publish issued before that
/// commit sees nothing and silently publishes nothing.</description></item>
/// <item><description><b>A count is not a content change.</b> An event
/// projection carries how many media items are attached and nothing else about
/// them, so caption and reorder edits are real writes that are invisible to a
/// companion, and a file-only orphan sweep changes no projection at all.</description></item>
/// </list></para>
///
/// <para><b>Shared rows fan out.</b> The EXIF backfill in
/// <c>MediaService.AttachAsync</c> writes coordinates onto a <c>Location</c>
/// row, which is shared. The attaching event is published by the attach
/// itself; every OTHER event linked to a backfilled location is republished by
/// the backfill, because its map pin moved too and nothing else would say so.
/// </para>
///
/// <para><b>Backfill.</b> Publishing on change is only half of a feed: an
/// archive that existed before a companion was paired, an archive restored
/// from a backup (which replaces <c>sync_outbox</c> on disk along with
/// everything else), or an archive pointed at a fresh server has rows the
/// outbox will never mention again. <see cref="PublishAllAsync"/> walks the
/// whole archive and projects every row. <c>SyncSettingsStore</c> flags a
/// backfill when a registration is saved, <c>BackupService.RestoreAsync</c>
/// flags one after a restore, and the sync worker runs the flagged backfill in
/// its next cycle before draining the outbox; the head may also call it
/// directly (a "resend my archive" action).</para>
/// </summary>
public interface ITimelineProjectionPublisher
{
    /// <summary>
    /// Projects every era, event, person and pending event in the archive
    /// (in that order, so a truncated delivery is still a consistent prefix:
    /// eras before the events that reference them, events before the people
    /// whose counts they explain). Idempotent at the row level — a projection
    /// identical to one still waiting in the outbox is never queued twice —
    /// with <paramref name="mode"/> deciding what an already-delivered row
    /// means. Entities deleted since the outbox last saw them cannot be
    /// tombstoned here: there is nothing left to enumerate.
    /// </summary>
    /// <param name="mode">What to do about projections the outbox already delivered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many projections were queued, per entity type, and how many were unchanged.</returns>
    Task<TimelineProjectionBackfillResult> PublishAllAsync(
        TimelineProjectionBackfillMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event as an <c>event</c> upsert, with its tags, people and
    /// locations denormalised inline. No-ops when the event does not exist (a
    /// deleted event is tombstoned through <see cref="PublishDeletedAsync"/>,
    /// which still has its id) and when the projection is unchanged.
    /// </summary>
    /// <param name="eventId">The event to project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishEventAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an era as an <c>era</c> upsert. No-ops for a missing era or an
    /// unchanged projection.
    /// </summary>
    /// <param name="eraId">The era to project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishEraAsync(string eraId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a person as a <c>person</c> upsert. No-ops for a missing person
    /// or an unchanged projection.
    ///
    /// A person who was merged away is NOT a deletion: the row survives carrying
    /// its <c>merged_into_id</c>, and this method publishes it so consumers can
    /// follow the pointer to the surviving person instead of showing a duplicate
    /// or a dangling reference from older events.
    /// </summary>
    /// <param name="personId">The person to project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishPersonAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an extracted event awaiting review as a <c>pending_event</c>
    /// upsert, so a companion can show the queue and act on it. No-ops for a
    /// missing pending event or an unchanged projection.
    /// </summary>
    /// <param name="pendingEventId">The pending event to project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishPendingEventAsync(string pendingEventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tombstones a removed entity with a <c>delete</c> operation, so a
    /// companion drops its copy instead of showing a memory the user deleted.
    /// Takes the id rather than the row because the row is already gone by the
    /// time a caller knows to publish this.
    ///
    /// Repeated calls collapse: a delete published immediately after another
    /// delete for the same entity is dropped. Note that a merged person is not a
    /// deletion — see <see cref="PublishPersonAsync"/>.
    /// </summary>
    /// <param name="entity">Which entity type the id belongs to.</param>
    /// <param name="entityId">The id that no longer exists.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishDeletedAsync(
        TimelineProjectionEntity entity, string entityId, CancellationToken cancellationToken = default);
}
