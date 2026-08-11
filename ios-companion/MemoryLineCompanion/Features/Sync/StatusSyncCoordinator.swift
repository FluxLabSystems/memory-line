import BackgroundTasks
import Foundation
import Observation
import os

// A file-private `SyncChangeOperation` used to live here carrying only
// `upsert`, because `capture_status` changes are never anything else, and the
// Swift DTO mirror had nowhere better to put it. It is gone in favour of the
// shared `SyncOperation` in DTOs.swift, which the timeline projection needed
// anyway — two spellings of the same two wire strings is one more than the wire
// has.

/// Pulls Windows-authored `capture_status` changes and applies them locally, so
/// the user can follow a capture's whole lifecycle from the phone without
/// opening Windows (design §19 Phase 3).
///
/// Direction of flow: Windows writes a status into its sync outbox → `POST
/// /sync/push` → the service change log → `GET /sync/pull` here. Nothing in the
/// payload is an instruction; it is a read-only projection, and editing and
/// approval stay on Windows.
///
/// Threading: the class is `@MainActor`, so `isPulling` doubles as the
/// in-flight guard — at most one pass (foreground kick, upload-pass follow-up,
/// or background refresh) runs at a time, and check-and-set is race-free.
///
/// Failure policy (design §16.2): any failure — network, server, or local
/// persistence — leaves the cursor where it was and returns. The next pass
/// replays the page; applying a change twice is a no-op by construction
/// (`resolvedState` only ever advances a capture, the status upsert is
/// idempotent, and a repeated failure is recognized from the stored status).
@MainActor
@Observable
final class StatusSyncCoordinator {
    /// True while a pull pass is in flight.
    var isPulling: Bool = false
    /// Human-readable summary of the most recent pass failure; nil when the
    /// last pass completed.
    var lastPullError: String?
    /// When the last completed pass finished.
    var lastPulledAt: Date?

    private let store: any CaptureStore
    private let settings: any SettingsStore
    private let api: any SyncAPI
    private let statusStore: any CaptureStatusStore
    private let projections: TimelineProjectionApplier
    private let notifier: any CaptureStatusNotifying
    private let logger = Logger(subsystem: "com.memoryline.companion", category: "StatusSyncCoordinator")

    /// Page size for `GET /sync/pull`; matches the service's own default.
    private static let pageLimit = 100

    /// Upper bound on pages per invocation, so one pass can never run
    /// unbounded. A long backlog simply finishes on the next trigger — the
    /// cursor is durable, so nothing is lost.
    private static let maxPagesPerPull = 20

    /// Set once `registerBackgroundTasks` has claimed the identifier. Without a
    /// registered launch handler a submitted request could never be delivered,
    /// so scheduling is skipped rather than left pending forever.
    @ObservationIgnored private var backgroundTasksRegistered = false

    /// - Parameters:
    ///   - store: local captures, whose lifecycle state a status may advance.
    ///   - settings: pairing/sync flags and the persisted pull cursor.
    ///   - api: sync client used for `GET /sync/pull` and `POST /sync/ack`.
    ///   - statusStore: durable projection of the Windows-side status detail
    ///     (transcript preview, review counts) that `CaptureRecord` has no
    ///     room for.
    ///   - projectionStore: the read-only copy of the Windows archive — events,
    ///     eras, people and the review queue. No default: a coordinator built
    ///     without one would pull the timeline feed, drop every event and report
    ///     a perfectly healthy sync, which is not a state worth reaching by
    ///     omission.
    ///   - notifier: local-notification presenter; injectable for tests.
    init(
        store: any CaptureStore,
        settings: any SettingsStore,
        api: any SyncAPI,
        statusStore: any CaptureStatusStore,
        projectionStore: any TimelineProjectionStore,
        notifier: any CaptureStatusNotifying
    ) {
        self.store = store
        self.settings = settings
        self.api = api
        self.statusStore = statusStore
        self.projections = TimelineProjectionApplier(store: projectionStore)
        self.notifier = notifier
    }

    /// Wiring convenience for the composition root: builds the standard
    /// `UNUserNotificationCenter`-backed notifier over the same settings store.
    convenience init(
        store: any CaptureStore,
        settings: any SettingsStore,
        api: any SyncAPI,
        statusStore: any CaptureStatusStore,
        projectionStore: any TimelineProjectionStore
    ) {
        self.init(
            store: store,
            settings: settings,
            api: api,
            statusStore: statusStore,
            projectionStore: projectionStore,
            notifier: CaptureStatusNotifier(settings: settings))
    }

    // MARK: - Public surface

    /// Registers the BGTaskScheduler launch handler for deferred status pulls.
    /// Must be called exactly once, before the app finishes launching.
    func registerBackgroundTasks() {
        let registered = BGTaskScheduler.shared.register(
            forTaskWithIdentifier: BackgroundTaskId.statusRefresh,
            using: nil
        ) { [weak self] task in
            // Runs on a BGTaskScheduler queue; hop to the main actor for all
            // coordinator state. Cancellation propagates into URLSession.
            let work = Task { @MainActor [weak self] in
                guard let self else {
                    task.setTaskCompleted(success: false)
                    return
                }
                await self.pullNow()
                self.scheduleStatusRefresh()
                task.setTaskCompleted(success: self.lastPullError == nil)
            }
            task.expirationHandler = { work.cancel() }
        }
        backgroundTasksRegistered = registered
        if registered {
            logger.debug("registered background task \(BackgroundTaskId.statusRefresh, privacy: .public)")
        } else {
            logger.error("failed to register background task \(BackgroundTaskId.statusRefresh, privacy: .public)")
        }
    }

    /// Fire-and-forget: starts a pull pass unless one is already running.
    func kick() {
        guard !isPulling else { return }
        Task { await self.pullNow() }
    }

    /// Pulls capture status from the service and applies it locally.
    ///
    /// Pages from the persisted cursor until the service reports no more
    /// changes (or `maxPagesPerPull` pages, whichever comes first). Each page is
    /// applied in cursor order, the new cursor is persisted, and only then is
    /// the page acknowledged, so a failure anywhere replays the page instead of
    /// skipping it.
    func pullNow() async {
        guard !isPulling else { return }
        guard settings.bool(AppSettingsKey.syncEnabled, default: false) else { return }
        guard
            settings.string(AppSettingsKey.deviceId)?.isEmpty == false,
            settings.string(AppSettingsKey.serverURL)?.isEmpty == false
        else { return }

        isPulling = true
        defer { isPulling = false }

        // Only reachable once the device is paired, which is what keeps the
        // notification prompt away from first launch (design §19 Phase 3).
        await notifier.requestAuthorizationIfNeeded()

        var cursor = storedCursor()
        var pages = 0

        while pages < Self.maxPagesPerPull {
            pages += 1

            let page: SyncPullResponse
            do {
                page = try await api.pull(cursor: cursor, limit: Self.pageLimit)
            } catch let error as SyncAPIError {
                lastPullError = error.needsReauthentication
                    ? "This device is no longer paired. Re-pair in Settings to resume sync."
                    : error.message
                logger.warning("status pull failed status=\(error.statusCode ?? -1) retryable=\(error.retryable)")
                return
            } catch {
                lastPullError = "Could not reach the sync server."
                logger.error("status pull failed error=\(String(describing: type(of: error)), privacy: .public)")
                return
            }

            let notifications: [CaptureStatusNotification]
            do {
                notifications = try apply(page.changes)
            } catch {
                // Local persistence failed; hold the cursor so the page replays.
                lastPullError = "Could not save the latest capture status."
                logger.error("status apply failed: \(String(describing: error), privacy: .public)")
                return
            }

            // `nextCursor` echoes the request cursor when the page was empty;
            // acking that again would be a pointless round trip every pass.
            if page.nextCursor > cursor {
                cursor = page.nextCursor
                persistCursor(cursor)
                do {
                    try await api.ack(cursor: cursor)
                } catch {
                    // The cursor is already durable here, so a failed ack only
                    // costs the server's own retention bookkeeping. The next
                    // successful ack carries a higher cursor and subsumes it.
                    logger.notice("status ack failed at cursor \(cursor)")
                }
            }

            for notification in notifications {
                await notifier.post(notification)
            }

            if !page.hasMore { break }
        }

        lastPullError = nil
        lastPulledAt = Date()
        scheduleStatusRefresh()
        logger.info("status pull finished cursor=\(cursor) pages=\(pages)")
    }

    /// Submits a `BGAppRefreshTaskRequest` so Windows-side progress keeps
    /// reaching the phone while the app is suspended. Skipped when the device
    /// is not paired or sync is off. Submitting the same identifier again
    /// simply replaces the pending request.
    func scheduleStatusRefresh() {
        guard
            backgroundTasksRegistered,
            settings.bool(AppSettingsKey.syncEnabled, default: false),
            settings.string(AppSettingsKey.deviceId)?.isEmpty == false
        else { return }
        let request = BGAppRefreshTaskRequest(identifier: BackgroundTaskId.statusRefresh)
        // Processing on Windows takes minutes, not seconds; the system decides
        // the actual run time from this floor and the app's usage budget.
        request.earliestBeginDate = Date(timeIntervalSinceNow: 30 * 60)
        try? BGTaskScheduler.shared.submit(request)
        logger.debug("scheduled background status refresh")
    }

    // MARK: - Applying changes

    /// Applies one page in cursor order and returns the transitions worth
    /// notifying about. Entity types this build does not consume simply carry
    /// the cursor forward.
    ///
    /// Two disjoint passes over the same array: `capture_status` is this type's
    /// own business — it moves a local capture's lifecycle and can raise a
    /// notification — while the timeline entities are pure projection and go
    /// through `TimelineProjectionApplier`. Keeping them apart is what stops
    /// this method growing a case per entity type as the contract does, and it
    /// lets the applier be driven by a test or a backfill with a hand-built
    /// page. The two filters cannot overlap, so nothing is applied twice.
    ///
    /// A page is not applied atomically and does not need to be: every write on
    /// both paths is an idempotent last-write-wins upsert or a delete, so a page
    /// that fails part-way is simply replayed.
    ///
    /// Throws only on local persistence failure, so the caller can hold the
    /// cursor and replay the page.
    private func apply(_ changes: [SyncChangeDto]) throws -> [CaptureStatusNotification] {
        var notifications: [CaptureStatusNotification] = []
        for change in changes where change.entityType == SyncChangeEntityType.captureStatus {
            if let notification = try apply(change) {
                notifications.append(notification)
            }
        }

        // Projections raise no notifications: a phone buzzing because Windows
        // republished an era is noise, and the transitions worth interrupting
        // someone for are the ones about their own recording.
        try projections.apply(changes)

        return notifications
    }

    /// Applies one `capture_status` change: the Windows-side detail is always
    /// recorded in the status store, and the capture's own state moves only
    /// when the status genuinely advances its remote lifecycle. Returns the
    /// notification-worthy transition, nil otherwise (unknown capture,
    /// malformed payload, a status that must not move it, or a failure already
    /// reported).
    private func apply(_ change: SyncChangeDto) throws -> CaptureStatusNotification? {
        // Only upserts carry a status; a delete would have no payload anyway.
        guard change.operation == SyncOperation.upsert else { return nil }
        guard let payloadJson = change.payloadJson, !payloadJson.isEmpty else {
            logger.notice("capture_status change \(change.changeId) had no payload")
            return nil
        }

        let payload: CaptureStatusChangePayload
        do {
            payload = try SyncJSON.decoder.decode(
                CaptureStatusChangePayload.self, from: Data(payloadJson.utf8))
        } catch {
            // A payload this build cannot read must not wedge the cursor
            // forever; skip it and keep draining.
            logger.error("capture_status payload could not be decoded change=\(change.changeId)")
            return nil
        }

        let captureId = payload.captureId.isEmpty ? change.entityId : payload.captureId
        // A capture deleted on the phone is not an error — drop the status
        // rather than leaving an orphan row behind.
        guard let capture = try store.capture(id: captureId) else { return nil }

        // Read before the upsert overwrites it: the stored status is what tells
        // a first failure apart from a replay of one already seen.
        let previousStatus = try statusStore.status(captureId: captureId)?.status

        try statusStore.upsert(CaptureStatusRecord(
            captureId: captureId,
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

        // A Windows-side PROCESSING failure must not touch the capture's upload
        // state. `CaptureRecord.state` has no processing-failure case:
        // `.failedRecoverable` means the UPLOAD failed and is upload-pending
        // (`CaptureLifecycleState.isUploadPending`, which is exactly what
        // `SQLiteCaptureStore.pendingUploads()` selects), so writing it here
        // would push a capture that uploaded perfectly well back into the
        // upload queue — re-reading and re-sending every part on every pass,
        // forever, with a wrong pending badge to match. The failure stays
        // visible through the status record just written, which is what History
        // reads (`CaptureLifecycleSummary` maps `failed` to `.processingFailed`
        // and lets it headline over the local state).
        if payload.status == SyncCaptureStatus.failed {
            // News only for a capture the phone believes it has handed over,
            // and only the first time: a replayed failure is not a transition.
            guard Self.remoteRank(capture.state) != nil,
                  previousStatus != SyncCaptureStatus.failed
            else { return nil }
            logger.info("capture processing failed on windows id=\(captureId, privacy: .public)")
            return .failed(
                captureId: captureId,
                reason: payload.failureReason ?? "Windows could not process this capture.")
        }

        guard let next = Self.resolvedState(for: payload.status, current: capture.state) else {
            logger.debug(
                "capture status recorded without a state change id=\(captureId, privacy: .public) status=\(payload.status, privacy: .public)")
            return nil
        }

        var updated = capture
        updated.state = next
        // The capture moved past whatever went wrong before.
        updated.lastError = nil
        try store.update(updated)
        logger.info(
            "capture advanced id=\(captureId, privacy: .public) state=\(next.rawValue, privacy: .public)")

        switch next {
        case .reviewReady:
            return .reviewReady(captureId: captureId)
        case .completed:
            return .completed(captureId: captureId)
        default:
            // `processing_remote` is progress, not news.
            return nil
        }
    }

    /// The lifecycle state a capture should take on for a wire status, or nil
    /// when the change must not touch it.
    ///
    /// - `local_only` / `uploading` echo what the phone already knows and never
    ///   change local state, and neither does a status this build predates.
    /// - `failed` describes the PROCESSING leg, which `CaptureRecord.state`
    ///   does not model — `.failedRecoverable` is the upload-failure state and
    ///   is upload-pending. A processing failure therefore leaves the capture's
    ///   state exactly where it was; it is recorded in the status store and the
    ///   History screens read it from there. Leaving the state alone is also
    ///   what keeps the rank floor honest: a failure neither lowers it (which
    ///   would let a late `received` re-apply) nor pins it (a Windows-side
    ///   retry reaching `review_ready` or `completed` still advances normally).
    /// - Everything else must strictly advance the capture (`remoteRank`), so a
    ///   replayed or out-of-order change can never move it backwards, and a
    ///   late status can never clobber a capture the phone still owns
    ///   (recording, saved, queued, uploading) or resurrect a cancelled one.
    nonisolated static func resolvedState(
        for wireStatus: String,
        current: CaptureLifecycleState
    ) -> CaptureLifecycleState? {
        let target: CaptureLifecycleState
        switch wireStatus {
        case SyncCaptureStatus.received, SyncCaptureStatus.processing:
            target = .processingRemote
        case SyncCaptureStatus.reviewReady:
            target = .reviewReady
        case SyncCaptureStatus.completed:
            target = .completed
        default:
            return nil
        }

        guard
            let currentRank = remoteRank(current),
            let targetRank = remoteRank(target),
            targetRank > currentRank
        else { return nil }
        return target
    }

    /// Ranks the remote half of the lifecycle (design §8.4). Nil for the states
    /// the phone still owns.
    ///
    /// `.failedRecoverable` ranks lowest rather than being excluded: a status
    /// only exists because Windows holds the capture, which proves the upload
    /// landed, so a capture parked there by a failed upload attempt is free to
    /// move forward again. Nothing here ever writes that state — only the
    /// upload path does.
    nonisolated private static func remoteRank(_ state: CaptureLifecycleState) -> Int? {
        switch state {
        case .failedRecoverable: return 0
        case .uploaded: return 1
        case .processingRemote: return 2
        case .reviewReady: return 3
        case .completed: return 4
        case .recording, .savedLocal, .queuedUpload, .uploading, .cancelled: return nil
        }
    }

    // MARK: - Cursor

    /// Last cursor this device applied (design §12.1). An unreadable value
    /// restarts from 0, which is safe: replaying the log only re-applies
    /// changes that are already no-ops.
    private func storedCursor() -> Int64 {
        guard
            let raw = settings.string(SyncCursorKey.pullCursor),
            let value = Int64(raw)
        else { return 0 }
        return max(0, value)
    }

    private func persistCursor(_ cursor: Int64) {
        settings.set(String(cursor), for: SyncCursorKey.pullCursor)
    }
}
