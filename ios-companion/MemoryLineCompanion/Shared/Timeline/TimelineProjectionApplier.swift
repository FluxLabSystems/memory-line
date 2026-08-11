import Foundation
import os

/// Applies Windows-authored timeline changes from the sync feed to the local
/// projection.
///
/// Deliberately independent of `MacSyncCoordinator`: this type knows about a
/// `[SyncChangeDto]` and a `TimelineProjectionStore` and nothing else. The
/// coordinator owns the cursor, the paging and the ack; the applier owns what a
/// change *means*. Keeping them apart is what lets a manual refresh, a future
/// backfill, or a test drive the same code with a hand-built page — and it keeps
/// the coordinator's growth linear as entity types accumulate, instead of
/// turning it into a switch over the whole contract.
///
/// The rules below are the ones the house already applies to `capture_status`,
/// because they are a contract with the server rather than a local preference
/// (claude.md, "Sync client semantics"):
///
///  - **Last-write-wins on `updatedAtUtc`**, never on `changeId` or `revision`.
///    Those are the feed's bookkeeping; `updatedAtUtc` is when the archive
///    actually changed. Changes arrive in cursor order, but a page replays
///    whenever applying it failed, so an older revision can genuinely be
///    redelivered after a newer one has landed.
///  - **An undecodable payload is skipped, not thrown.** Throwing would hold the
///    cursor and replay the same page forever, so one change carrying a shape
///    this build predates would wedge sync permanently. The change is logged by
///    id and the pass continues past it.
///  - **A store failure throws.** That is the one failure worth stopping for: it
///    means the write did not happen, so the caller must hold the cursor and let
///    the page replay.
///
/// One rule is specific to these entities rather than inherited: rows are keyed
/// on the change's `entityId`, not on the id inside the payload. The service
/// pins the former to a single spelling for exactly this reason and leaves the
/// latter free to differ — see `rowKey(for:payloadId:)`.
///
/// Privacy (design §14.5): nothing here logs payload content. Change ids, entity
/// types and counts only — never a title, a name, a location or a transcript
/// preview, all of which pass through this type.
struct TimelineProjectionApplier {
    private let store: any TimelineProjectionStore
    private let logger = AppLog.sync

    init(store: any TimelineProjectionStore) {
        self.store = store
    }

    /// Applies every timeline change in `changes` and returns how many were
    /// applied.
    ///
    /// Entity types outside `SyncChangeEntityType.projectedByWindows` are ignored
    /// without comment: the feed is shared, so a page routinely carries
    /// `capture`, `capture_status` and `assistant_turn` changes that belong to
    /// other features, and treating those as anomalies would bury the real
    /// anomalies in noise.
    ///
    /// The count is *changes applied*, not rows altered — a delete for an entity
    /// this device never held counts, because the change was consumed and the
    /// desired end state (absent) is what the store now has. Pre-checking to
    /// report rows-altered would double the queries to make a progress number
    /// slightly different.
    ///
    /// Throws only when the store fails.
    @discardableResult
    func apply(_ changes: [SyncChangeDto]) throws -> Int {
        var applied = 0
        for change in changes where SyncChangeEntityType.projectedByWindows.contains(change.entityType) {
            if try apply(change) { applied += 1 }
        }
        return applied
    }

    // MARK: - One change

    private func apply(_ change: SyncChangeDto) throws -> Bool {
        switch change.operation {
        case SyncOperation.upsert:
            return try applyUpsert(change)
        case SyncOperation.delete:
            return try applyDelete(change)
        default:
            // Same reasoning as an undecodable payload: an operation this build
            // does not know is a contract we are behind on, not a reason to
            // stall the cursor.
            logger.notice("timeline change \(change.changeId) has unknown operation; skipped")
            return false
        }
    }

    private func applyUpsert(_ change: SyncChangeDto) throws -> Bool {
        guard let json = change.payloadJson, let data = json.data(using: .utf8) else {
            logger.notice("timeline change \(change.changeId) has no payload; skipped")
            return false
        }

        switch change.entityType {
        case SyncChangeEntityType.event:
            guard var payload = decode(EventProjectionPayload.self, from: data, change: change) else {
                return false
            }
            payload.eventId = rowKey(for: change, payloadId: payload.eventId)
            let existing = try store.event(eventId: payload.eventId)?.updatedAtUtc
            guard isNotStale(payload.updatedAtUtc, existing: existing) else { return false }
            try store.upsertEvent(payload)

        case SyncChangeEntityType.era:
            guard var payload = decode(EraProjectionPayload.self, from: data, change: change) else {
                return false
            }
            payload.eraId = rowKey(for: change, payloadId: payload.eraId)
            let existing = try store.era(eraId: payload.eraId)?.updatedAtUtc
            guard isNotStale(payload.updatedAtUtc, existing: existing) else { return false }
            try store.upsertEra(payload)

        case SyncChangeEntityType.person:
            guard var payload = decode(PersonProjectionPayload.self, from: data, change: change) else {
                return false
            }
            payload.personId = rowKey(for: change, payloadId: payload.personId)
            let existing = try store.person(personId: payload.personId)?.updatedAtUtc
            guard isNotStale(payload.updatedAtUtc, existing: existing) else { return false }
            try store.upsertPerson(payload)

        case SyncChangeEntityType.pendingEvent:
            guard var payload = decode(PendingEventProjectionPayload.self, from: data, change: change) else {
                return false
            }
            payload.pendingEventId = rowKey(for: change, payloadId: payload.pendingEventId)
            let existing = try store.pendingEvent(pendingEventId: payload.pendingEventId)?.updatedAtUtc
            guard isNotStale(payload.updatedAtUtc, existing: existing) else { return false }
            try store.upsertPendingEvent(payload)

        default:
            // Unreachable while `apply(_:)` filters on `projected`; kept so this
            // switch stays honest if that filter ever moves.
            return false
        }
        return true
    }

    /// Deletes key off `change.entityId` rather than a payload: a delete carries
    /// none, and the feed's own addressing is the only identifier there is.
    ///
    /// The row is removed outright — no deleted-marker column. A tombstone would
    /// exist to reject an upsert older than the delete, and that cannot happen
    /// here: the feed is ordered by change id and a replayed page replays in the
    /// same order, so any upsert that preceded a delete is followed by that
    /// delete again. An upsert *after* a delete is Windows re-creating the
    /// entity, which is exactly what should be honoured. For a read-only
    /// projection with no local edits, a delete is terminal and needs nothing
    /// merged.
    private func applyDelete(_ change: SyncChangeDto) throws -> Bool {
        guard !change.entityId.isEmpty else {
            logger.notice("timeline delete change \(change.changeId) has no entity id; skipped")
            return false
        }

        switch change.entityType {
        case SyncChangeEntityType.event:
            try store.deleteEvent(eventId: change.entityId)
        case SyncChangeEntityType.era:
            try store.deleteEra(eraId: change.entityId)
        case SyncChangeEntityType.person:
            try store.deletePerson(personId: change.entityId)
        case SyncChangeEntityType.pendingEvent:
            try store.deletePendingEvent(pendingEventId: change.entityId)
        default:
            return false
        }
        return true
    }

    // MARK: - Helpers

    /// The key a row is stored under: the feed's `entityId`, not the payload's
    /// own copy of that id.
    ///
    /// The service pins `entityId` to one spelling — a UUID in canonical
    /// lowercase form — and rejects any other, precisely so that an upsert and a
    /// later tombstone address the same row. It does *not* pin the payload's
    /// copy: an event whose `eventId` reads `{A1B2…}` is accepted as long as it
    /// denotes the same UUID (`SyncChangeService.TryRelayProjection`, "Entity
    /// IDs"). SQLite compares TEXT byte for byte, so keying rows on the payload
    /// id would let a delete match nothing and leave a memory the user removed
    /// on Windows sitting on this device forever — the exact failure the contract
    /// note describes. Normalising here also means the id this app hands back to
    /// the UI is the one every other device uses.
    ///
    /// The payload id is the fallback only for a change carrying no `entityId`
    /// at all, which this server does not produce; storing such a row under the
    /// id it names itself is a better answer than dropping the memory.
    private func rowKey(for change: SyncChangeDto, payloadId: String) -> String {
        change.entityId.isEmpty ? payloadId : change.entityId
    }

    /// Last-write-wins: the incoming payload is applied unless the stored row is
    /// strictly newer. Equal timestamps re-apply, which is both harmless (the
    /// same `updatedAtUtc` means the same publisher-side state) and desirable —
    /// it keeps a replayed page idempotent rather than order-dependent.
    private func isNotStale(_ incoming: Date, existing: Date?) -> Bool {
        guard let existing else { return true }
        return existing <= incoming
    }

    /// Decodes a payload with the sync date strategy, turning a failure into a
    /// skip. The log line carries the change id and the entity type — both feed
    /// vocabulary — and never the payload or the decoding error, which would
    /// quote the offending JSON (§14.5).
    private func decode<Payload: Decodable>(
        _ type: Payload.Type,
        from data: Data,
        change: SyncChangeDto
    ) -> Payload? {
        do {
            return try SyncJSON.decoder.decode(type, from: data)
        } catch {
            logger.notice(
                "timeline change \(change.changeId) of type \(change.entityType, privacy: .public) failed to decode; skipped")
            return nil
        }
    }
}
