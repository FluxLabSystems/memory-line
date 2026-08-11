import Foundation
import Observation
import os

/// Queues review verdicts and delivers them to the sync service.
///
/// This is the only place this app writes to the change feed. Everything else it
/// holds is a projection Windows published; a verdict travels the other way, and
/// the contract admits it precisely because it is not an edit — one idempotent
/// bit, no field values to merge (`TimelineProjectionContracts.cs`).
///
/// **Queue first, send second, and the queue is on disk.** A reviewer working
/// through a list on a laptop is exactly the user most likely to close the lid
/// mid-pass, so a decision becomes durable before any network call is attempted.
/// The alternative — send-and-hope — loses a user's considered judgement to a
/// quit, which is a worse failure than a delayed sync.
///
/// **What retries and what does not.** A transport failure retries forever, on
/// purpose: an unreachable server is a state that ends, and giving up would
/// discard a decision the user made. A per-entry rejection from the service does
/// not retry at all, because a retry would send identical bytes to identical
/// validation; the row is marked failed and the reason is shown. The distinction
/// is the same one the cursor makes in `MacSyncCoordinator`, for the same reason.
@MainActor
@Observable
final class MacReviewCoordinator {
    enum State: Equatable {
        case idle
        case sending
        /// Carries text already safe to display — never transcript content or
        /// credentials (design §14.5).
        case failed(String)
    }

    private let settings: any SettingsStore
    private let api: any MacDecisionPushing
    private let store: any ReviewDecisionStore
    private let projections: any TimelineProjectionStore
    private let logger = AppLog.logger(category: "review")

    private(set) var state: State = .idle
    /// Verdicts by pending-event id, so the review list can render each item's
    /// status without querying per row.
    private(set) var decisions: [String: ReviewDecisionRecord] = [:]

    @ObservationIgnored private var ticker: Task<Void, Never>?

    init(
        settings: any SettingsStore,
        api: any MacDecisionPushing,
        store: any ReviewDecisionStore,
        projections: any TimelineProjectionStore
    ) {
        self.settings = settings
        self.api = api
        self.store = store
        self.projections = projections
    }

    /// True once this Mac is paired — the precondition for delivering anything.
    /// Note that deciding does **not** require it: a verdict is queued whether or
    /// not the server is reachable, which is the point of the queue.
    var canSend: Bool {
        settings.string(AppSettingsKey.deviceId)?.isEmpty == false
            && settings.string(AppSettingsKey.serverURL)?.isEmpty == false
    }

    /// Loads the queue into `decisions` for the UI. Cheap; safe to call on view
    /// appearance and after every pull.
    func reload() {
        do {
            decisions = Dictionary(
                try store.all().map { ($0.pendingEventId, $0) },
                uniquingKeysWith: { first, _ in first })
        } catch {
            logger.error("could not read the decision queue: \(String(describing: error), privacy: .public)")
        }
    }

    /// Records a verdict and tries to deliver it immediately.
    ///
    /// A decision already delivered is not replaced: Windows has acted on it, and
    /// a companion cannot un-approve an event that now exists in the archive —
    /// the service drops a verdict for a review it has already resolved, so
    /// queuing a second one would only produce a confusing local state.
    func decide(pendingEventId: String, verdict: ReviewVerdict) async {
        do {
            if let existing = try store.decision(for: pendingEventId), existing.state != .pending {
                logger.notice("verdict for an already-delivered review ignored")
                return
            }

            let record = ReviewDecisionRecord(
                pendingEventId: pendingEventId,
                verdict: verdict,
                decidedAt: Date(),
                clientSequence: try store.nextClientSequence())
            try store.upsert(record)
            decisions[pendingEventId] = record
        } catch {
            state = .failed("Could not save your decision.")
            logger.error("queueing a decision failed: \(String(describing: error), privacy: .public)")
            return
        }

        await drain()
    }

    /// Starts the periodic drain. Like the sync loop, macOS needs no
    /// `BGTaskScheduler`: the app is either running and can keep a timer, or not
    /// running and has nothing to send. Calling this again replaces the ticker
    /// rather than stacking a second one.
    func startPeriodicDrain(interval: Duration = .seconds(120)) {
        ticker?.cancel()
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                await self?.drain()
                do {
                    try await Task.sleep(for: interval)
                } catch {
                    return // cancelled
                }
            }
        }
    }

    func stopPeriodicDrain() {
        ticker?.cancel()
        ticker = nil
    }

    /// Pushes every queued verdict in one request and applies the per-entry
    /// results.
    ///
    /// One request rather than one per decision: the endpoint takes a batch, a
    /// reviewer clearing a queue produces a burst, and the results come back
    /// keyed by `clientSequence` so nothing is lost by batching.
    func drain() async {
        guard state != .sending, canSend else { return }

        let queued: [ReviewDecisionRecord]
        do {
            queued = try store.pending()
        } catch {
            logger.error("could not read the decision queue: \(String(describing: error), privacy: .public)")
            return
        }
        guard !queued.isEmpty else {
            state = .idle
            return
        }

        guard let deviceId = settings.string(AppSettingsKey.deviceId), !deviceId.isEmpty else {
            return
        }

        state = .sending
        let entries = queued.compactMap { entry(for: $0, deviceId: deviceId) }
        guard !entries.isEmpty else {
            state = .idle
            return
        }

        let response: SyncPushResponse
        do {
            response = try await api.push(SyncPushRequest(entries: entries))
        } catch let error as SyncAPIError {
            // A transport-level failure says nothing about any individual
            // verdict, so nothing is marked failed here — the rows stay pending
            // and the next tick tries again.
            state = .failed(error.needsReauthentication
                ? "This Mac is no longer paired. Re-pair in Settings to send your reviews."
                : error.message)
            logger.warning("decision push failed status=\(error.statusCode ?? -1)")
            return
        } catch {
            state = .failed("Could not reach the sync server.")
            logger.error("decision push failed: \(String(describing: type(of: error)), privacy: .public)")
            return
        }

        apply(response, to: queued)
        state = .idle
    }

    /// Drops delivered rows whose pending event Windows has confirmed by
    /// publishing its tombstone.
    ///
    /// This is what makes a `sent` row self-cleaning, and why `sent` exists at
    /// all rather than deleting on acceptance: between the server accepting a
    /// verdict and Windows publishing the result there is a real interval, and
    /// deleting immediately would make the item pop back into the queue looking
    /// undecided. Keeping the row until the projection agrees closes that window
    /// without inventing a timeout.
    func pruneConfirmed() {
        do {
            for record in try store.all() where record.state == .sent {
                if try projections.pendingEvent(pendingEventId: record.pendingEventId) == nil {
                    try store.delete(pendingEventId: record.pendingEventId)
                    decisions[record.pendingEventId] = nil
                }
            }
        } catch {
            logger.error("pruning delivered decisions failed: \(String(describing: error), privacy: .public)")
        }
    }

    /// Discards the whole queue. Called on unpair alongside the projection reset:
    /// these verdicts address pending events that are about to be deleted, and
    /// their client sequences belong to a device registration that no longer
    /// exists.
    func discardAll() {
        stopPeriodicDrain()
        do {
            try store.deleteAll()
        } catch {
            logger.error("clearing the decision queue failed: \(String(describing: error), privacy: .public)")
        }
        decisions = [:]
        state = .idle
    }

    // MARK: - Building and applying

    private func entry(for record: ReviewDecisionRecord, deviceId: String) -> SyncPushEntry? {
        let payload = PendingEventDecisionPayload(
            pendingEventId: record.pendingEventId,
            decision: record.verdict.rawValue,
            decidedByDeviceId: deviceId,
            decidedAtUtc: record.decidedAt,
            // No corrections: this Mac shows the extraction and takes a verdict
            // on it. Correcting a title or a date before approving is a real part
            // of the contract, but it is an editing surface this screen does not
            // have yet, and sending an empty corrections object would be a
            // different statement from sending none.
            corrections: nil)

        guard let json = try? SyncJSON.encoder.encode(payload),
              let text = String(data: json, encoding: .utf8) else {
            logger.error("could not encode a queued decision; skipped this pass")
            return nil
        }

        return SyncPushEntry(
            clientSequence: record.clientSequence,
            entityType: SyncChangeEntityType.pendingEventDecision,
            entityId: record.pendingEventId,
            operation: SyncOperation.upsert,
            payloadJson: text,
            changedAtUtc: record.decidedAt)
    }

    private func apply(_ response: SyncPushResponse, to queued: [ReviewDecisionRecord]) {
        let bySequence = Dictionary(
            queued.map { ($0.clientSequence, $0) }, uniquingKeysWith: { first, _ in first })

        for result in response.results {
            guard var record = bySequence[result.clientSequence] else {
                // A result for something this pass did not send. Not actionable,
                // and not worth failing over.
                continue
            }

            record.attempts += 1
            if result.accepted || result.duplicate {
                // A duplicate is a success: the server already has what we were
                // trying to send. Treating it as a failure would retry forever
                // against a receipt that will keep saying the same thing.
                record.state = .sent
                record.lastError = nil
            } else {
                // Per-entry rejections are validation outcomes, not transport
                // ones. The same bytes would fail the same way, so this stops.
                record.state = .failed
                record.lastError = result.error ?? "The sync server rejected this decision."
                logger.notice("decision rejected by the server; not retrying")
            }

            do {
                try store.upsert(record)
                decisions[record.pendingEventId] = record
            } catch {
                logger.error("recording a decision result failed: \(String(describing: error), privacy: .public)")
            }
        }
    }
}
