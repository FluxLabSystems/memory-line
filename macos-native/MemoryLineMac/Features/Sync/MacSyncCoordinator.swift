import Foundation
import Observation
import os

/// Pulls the sync change feed and applies everything Windows authors on it:
/// capture status (design §19 Phase 3) and the read-only timeline projection —
/// events, eras, people and the pending-event review queue.
///
/// This is the macOS counterpart of the iOS `StatusSyncCoordinator`, and it is
/// deliberately a reimplementation rather than a shared type: that coordinator
/// is built on `BGTaskScheduler`, which does not exist on macOS, and it consumes
/// only the capture-status half of the feed. The Mac is the browsing surface, so
/// it also consumes the projection the phone has no screen for.
///
/// **Two appliers, one page.** Capture status is applied inline here; timeline
/// entities go through `TimelineProjectionApplier`. That split is the reason this
/// type does not grow a switch over the contract as entity types accumulate —
/// see the argument in that file. The two write different tables in separate
/// transactions, so a page is not applied atomically; it does not need to be,
/// because every write on both paths is an idempotent last-write-wins upsert or
/// a delete, and a page that fails part-way is replayed in full.
///
/// The pull/ack semantics below mirror the iOS loop exactly, because they are
/// a contract with the server rather than a local choice:
///  - page until `hasMore` is false, bounded by `maxPagesPerPull`;
///  - persist the cursor **before** acking, so a failed ack never loses ground;
///  - hold the cursor when *applying* fails, so the page replays;
///  - never ack a cursor that did not advance.
@MainActor
@Observable
final class MacSyncCoordinator {
    /// What the UI shows. `.failed` carries text already safe to display —
    /// it never contains transcript content or credentials (design §14.5).
    enum State: Equatable {
        case idle
        case syncing
        case failed(String)
    }

    private static let pageLimit = 200
    /// Bounds one pass so a large backlog cannot spin forever; the next pass
    /// picks up where this one stopped.
    private static let maxPagesPerPull = 20

    private let settings: any SettingsStore
    private let api: any SyncAPI
    private let statusStore: any CaptureStatusStore
    private let projections: TimelineProjectionApplier
    private let logger = AppLog.logger(category: "sync")

    private(set) var state: State = .idle
    private(set) var lastPulledAt: Date?
    /// Number of changes applied by the most recent pass — status rows and
    /// timeline entities together — for the UI.
    private(set) var lastAppliedCount = 0

    @ObservationIgnored private var ticker: Task<Void, Never>?

    /// `projectionStore` has no default. A coordinator constructed without one
    /// would pull the timeline feed, drop every event on the floor and report a
    /// perfectly healthy sync — which is precisely the state the Mac was in
    /// before this existed, and not one worth being able to reach by omission.
    init(
        settings: any SettingsStore,
        api: any SyncAPI,
        statusStore: any CaptureStatusStore,
        projectionStore: any TimelineProjectionStore
    ) {
        self.settings = settings
        self.api = api
        self.statusStore = statusStore
        self.projections = TimelineProjectionApplier(store: projectionStore)
    }

    // No deinit cancelling the ticker: the loop captures self weakly, so it
    // cannot keep this object alive, and MacAppEnvironment owns the
    // coordinator for the process lifetime. Callers that need it stopped
    // (unpairing) call stopPeriodicSync explicitly.

    /// True once this Mac is paired and sync has not been switched off.
    var canSync: Bool {
        settings.bool(AppSettingsKey.syncEnabled, default: false)
            && settings.string(AppSettingsKey.deviceId)?.isEmpty == false
            && settings.string(AppSettingsKey.serverURL)?.isEmpty == false
    }

    /// Starts the periodic pull. macOS has no `BGTaskScheduler`, and it does
    /// not need one: the app is either running (and can simply keep a timer)
    /// or not running (and has nothing to do). Calling this again replaces the
    /// existing ticker rather than stacking a second one.
    func startPeriodicSync(interval: Duration = .seconds(120)) {
        ticker?.cancel()
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                await self?.pullNow()
                do {
                    try await Task.sleep(for: interval)
                } catch {
                    return // cancelled
                }
            }
        }
    }

    func stopPeriodicSync() {
        ticker?.cancel()
        ticker = nil
    }

    /// Runs one pull pass. Safe to call while a pass is already running — the
    /// second call returns immediately rather than interleaving cursor writes.
    func pullNow() async {
        guard state != .syncing, canSync else { return }

        state = .syncing
        var cursor = storedCursor()
        var pages = 0
        var applied = 0

        while pages < Self.maxPagesPerPull {
            pages += 1

            let page: SyncPullResponse
            do {
                page = try await api.pull(cursor: cursor, limit: Self.pageLimit)
            } catch let error as SyncAPIError {
                state = .failed(error.needsReauthentication
                    ? "This Mac is no longer paired. Re-pair in Settings to resume sync."
                    : error.message)
                logger.warning("pull failed status=\(error.statusCode ?? -1) retryable=\(error.retryable)")
                return
            } catch {
                state = .failed("Could not reach the sync server.")
                logger.error("pull failed error=\(String(describing: type(of: error)), privacy: .public)")
                return
            }

            do {
                applied += try apply(page.changes)
            } catch {
                // Local persistence failed: hold the cursor so this page is
                // replayed rather than silently skipped. The message does not
                // name which applier failed — the user can act on neither
                // answer, and the log line carries the detail.
                state = .failed("Could not save the latest changes from Windows.")
                logger.error("apply failed: \(String(describing: error), privacy: .public)")
                return
            }

            // `nextCursor` echoes the request cursor for an empty page; acking
            // that again would be a pointless round trip on every pass.
            if page.nextCursor > cursor {
                cursor = page.nextCursor
                persistCursor(cursor)
                do {
                    try await api.ack(cursor: cursor)
                } catch {
                    // The cursor is already durable, so a failed ack costs only
                    // the server's retention bookkeeping; the next successful
                    // ack carries a higher cursor and subsumes this one.
                    logger.notice("ack failed at cursor \(cursor)")
                }
            }

            if !page.hasMore { break }
        }

        lastAppliedCount = applied
        lastPulledAt = Date()
        state = .idle
        logger.info("pull finished cursor=\(cursor) pages=\(pages) applied=\(applied)")
    }

    // MARK: - Applying changes

    /// Applies the page and returns how many changes were consumed.
    ///
    /// Two passes over the same array rather than one switch: capture status is
    /// this type's own business, and the timeline entities belong to an applier
    /// that can also be driven by a backfill or a test with a hand-built page.
    /// The two filters are disjoint — `capture_status` is not in
    /// `SyncChangeEntityType.projectedByWindows` — so nothing is applied twice.
    ///
    /// Entity types neither pass claims are ignored on purpose. `capture` and
    /// `capture_artifact` describe recordings whose authoritative copy is the
    /// local `captures` row this Mac already wrote, and `pending_event_decision`
    /// is a verdict some companion authored, not a projection to consume.
    private func apply(_ changes: [SyncChangeDto]) throws -> Int {
        var applied = 0
        for change in changes where change.entityType == SyncChangeEntityType.captureStatus {
            if try apply(change) { applied += 1 }
        }
        applied += try projections.apply(changes)
        return applied
    }

    private func apply(_ change: SyncChangeDto) throws -> Bool {
        guard let json = change.payloadJson, let data = json.data(using: .utf8) else {
            logger.notice("capture_status change \(change.changeId) has no payload; skipped")
            return false
        }

        let payload: CaptureStatusChangePayload
        do {
            payload = try SyncJSON.decoder.decode(CaptureStatusChangePayload.self, from: data)
        } catch {
            // A change we cannot parse must not wedge the cursor forever — a
            // future contract version may add shapes this build predates. Skip
            // it and let the pass continue.
            logger.notice("capture_status change \(change.changeId) failed to decode; skipped")
            return false
        }

        // Last-write-wins on the Windows-authored timestamp. Changes arrive in
        // cursor order, but a replayed page can redeliver an older one.
        if let existing = try statusStore.status(captureId: payload.captureId),
           existing.updatedAt > payload.updatedAtUtc {
            return false
        }

        try statusStore.upsert(CaptureStatusRecord(
            captureId: payload.captureId,
            status: payload.status,
            processingStage: payload.processingStage,
            transcriptAvailable: payload.transcriptAvailable,
            transcriptPreview: payload.transcriptPreview,
            transcriptCharCount: payload.transcriptCharCount,
            pendingEventCount: payload.pendingEventCount,
            approvedEventCount: payload.approvedEventCount,
            failureReason: payload.failureReason,
            failureRetryable: payload.failureRetryable,
            updatedAt: payload.updatedAtUtc))
        return true
    }

    // MARK: - Cursor

    private func storedCursor() -> Int64 {
        guard let raw = settings.string(MacSyncSettingsKey.pullCursor),
              let value = Int64(raw) else {
            return 0
        }
        return value
    }

    private func persistCursor(_ cursor: Int64) {
        settings.set(String(cursor), for: MacSyncSettingsKey.pullCursor)
    }

    /// Forgets the pull position so the next pass starts from the beginning of
    /// the feed.
    ///
    /// Called on unpair, and it is not optional there. Change ids are the
    /// server's, not this device's: a Mac that unpairs and pairs again — to the
    /// same server or a different one — is a new device with an empty local
    /// projection, and resuming from the old cursor would skip the entire
    /// backlog and leave a timeline that never fills in. Whoever clears the
    /// cursor must also clear the projection it describes, which is why
    /// `MacAppEnvironment.unpair()` does both together.
    func resetCursor() {
        settings.set(nil, for: MacSyncSettingsKey.pullCursor)
        lastAppliedCount = 0
        lastPulledAt = nil
    }
}

/// Setting keys owned by the Mac's sync loop.
///
/// `pullCursor` intentionally uses the same `"pull_cursor"` string as the iOS
/// companion's `SyncCursorKey`. The key lives in each app's own local settings
/// table and is never exchanged, so the two are independent values that happen
/// to share a name; the name is duplicated because the iOS declaration sits in
/// `Features/Sync`, which macOS does not compile.
enum MacSyncSettingsKey {
    static let pullCursor = "pull_cursor"
}
