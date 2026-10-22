import BackgroundTasks
import Foundation
import Observation
import os

/// Drives the capture upload pipeline (design §16): scans the durable queue,
/// pushes each pending capture through create → initiate → parts → complete,
/// persists every state transition through `CaptureStore`, and reflects
/// progress into the UI (`isSyncing` / `pendingCount` / `lastSyncError`) and
/// the widgets.
///
/// Threading: the class is `@MainActor`; all published state changes happen on
/// the main actor. The per-capture pipeline (file slicing and network I/O)
/// runs in `nonisolated` async methods so the main actor is never blocked.
///
/// Failure policy (§16.2): retryable failures (network, 408/429/5xx, or a
/// server-flagged retryable error) park the capture in `.failedRecoverable`
/// and abort the rest of the pass — the next kick or background task retries.
/// Non-retryable 4xx failures also park the capture in `.failedRecoverable`
/// with a clear `lastError` surfaced in History, but the pass continues with
/// the next capture. Each pass processes every pending capture at most once,
/// so a failing server can never cause a tight loop.
@MainActor
@Observable
final class UploadCoordinator {
    /// True while an upload pass is actively transferring captures.
    var isSyncing: Bool = false
    /// Human-readable summary of the most recent pass failure; nil when the
    /// last pass fully succeeded (or nothing was pending).
    var lastSyncError: String?
    /// Number of captures waiting to reach the sync service.
    var pendingCount: Int = 0

    private let store: any CaptureStore
    private let settings: any SettingsStore
    /// Held to satisfy the prescribed wiring; token handling itself lives in
    /// the `SyncAPI` implementation.
    private let tokens: any TokenStore
    private let api: any SyncAPI
    private let logger = Logger(subsystem: "com.memoryline.companion", category: "UploadCoordinator")

    /// In-flight guard: at most one pass (kick / syncNow / retry / background
    /// task) runs at a time. Main-actor confined, so check-and-set is race-free.
    @ObservationIgnored private var isRunning = false

    /// Raised by `kick()` when a pass is already running — e.g. a capture
    /// finalized mid-pass. The running pass consumes it and sweeps the queue
    /// again, so the new capture uploads now instead of waiting for the next
    /// external trigger.
    @ObservationIgnored private var pendingRerun = false

    /// Status pull to run after each upload pass, so the phone learns what
    /// Windows did with the captures it just sent (design §19 Phase 3). Weak
    /// because `AppEnvironment` owns both coordinators; the status side never
    /// calls back here, so the pull can neither form a cycle nor re-enter the
    /// upload pipeline.
    @ObservationIgnored weak var statusSync: StatusSyncCoordinator?

    init(store: any CaptureStore, settings: any SettingsStore, tokens: any TokenStore, api: any SyncAPI) {
        self.store = store
        self.settings = settings
        self.tokens = tokens
        self.api = api
        self.pendingCount = (try? store.pendingUploadCount()) ?? 0
    }

    // MARK: - Public surface

    /// Registers the BGTaskScheduler launch handler for deferred upload
    /// retries. Must be called exactly once, before the app finishes launching.
    func registerBackgroundTasks() {
        let registered = BGTaskScheduler.shared.register(
            forTaskWithIdentifier: BackgroundTaskId.uploadRetry,
            using: nil
        ) { [weak self] task in
            // Runs on a BGTaskScheduler queue; hop to the main actor for all
            // coordinator state. Cancellation propagates into URLSession.
            let work = Task { @MainActor [weak self] in
                guard let self else {
                    task.setTaskCompleted(success: false)
                    return
                }
                // Background retries are automatic, never manual: they must
                // honor the `autoUpload` setting instead of overriding it.
                await self.run(manual: false)
                self.scheduleUploadRetry()
                task.setTaskCompleted(success: self.lastSyncError == nil)
            }
            task.expirationHandler = { work.cancel() }
        }
        if registered {
            logger.debug("registered background task \(BackgroundTaskId.uploadRetry, privacy: .public)")
        } else {
            logger.error("failed to register background task \(BackgroundTaskId.uploadRetry, privacy: .public)")
        }
    }

    /// Fire-and-forget: starts an automatic upload pass unless one is already
    /// running (then the running pass is asked to sweep the queue again).
    /// Automatic passes respect the `autoUpload` setting.
    func kick() {
        guard !isRunning else {
            pendingRerun = true
            return
        }
        Task { await self.run(manual: false) }
    }

    /// Runs a full upload pass now. Manual passes override `autoUpload` (but
    /// still require `syncEnabled`).
    func syncNow() async {
        await run(manual: true)
    }

    /// Manually retries a single capture from History. Overrides `autoUpload`.
    func retry(captureId: String) async {
        guard !isRunning else { return }
        isRunning = true
        defer { isRunning = false }

        guard settings.bool(AppSettingsKey.syncEnabled, default: false) else {
            lastSyncError = "Sync is turned off. Enable it in Settings."
            return
        }
        guard
            let deviceId = settings.string(AppSettingsKey.deviceId), !deviceId.isEmpty,
            settings.string(AppSettingsKey.serverURL)?.isEmpty == false
        else {
            lastSyncError = "Not paired with a sync server yet. Pair in Settings."
            return
        }
        recoverInterruptedUploads()
        guard let record = try? store.capture(id: captureId), record.state.isUploadPending else {
            refreshPendingCount()
            return
        }

        isSyncing = true
        lastSyncError = nil
        let outcome = await processCapture(record, deviceId: deviceId)
        isSyncing = false
        if case .failed(let message, _) = outcome {
            lastSyncError = message
        }
        refreshPendingCount()
        publishWidgetState()
        scheduleUploadRetry()
    }

    /// Called by Settings when the auto-upload preference changes (or when the
    /// master sync switch turns off). Disabling cancels the pending deferred
    /// background retry so the system stops waking the app to upload;
    /// enabling reschedules one if captures are waiting. Keeps BGTaskScheduler
    /// knowledge out of the view layer.
    func autoUploadPreferenceChanged(enabled: Bool) {
        if enabled {
            scheduleUploadRetry()
        } else {
            BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: BackgroundTaskId.uploadRetry)
            logger.debug("cancelled pending background upload retry")
        }
    }

    /// Called by the app shell right after a capture is finalized and saved.
    /// Persists nothing (the recorder already did) — refreshes the widget
    /// snapshot, schedules a deferred retry, and kicks an upload pass.
    func captureFinalized(_ capture: CaptureRecord) {
        refreshPendingCount()
        publishWidgetState(lastCaptureAt: capture.capturedAt)
        scheduleUploadRetry()
        kick()
    }

    // MARK: - Upload pass

    /// One trigger's worth of work: the upload sweep, then a status pull on the
    /// same trigger so the user's "sync now" also refreshes what Windows has
    /// done (design §19 Phase 3). The pull runs after `runUploadPass` has
    /// released the in-flight guard, so it neither blocks a capture finalized
    /// mid-pull nor re-enters this pass.
    private func run(manual: Bool) async {
        guard await runUploadPass(manual: manual) else { return }
        await statusSync?.pullNow()
    }

    /// Runs the upload sweep. Returns false when the pass never started (sync
    /// off, auto-upload off, or not paired) — the same conditions that make a
    /// status pull pointless.
    private func runUploadPass(manual: Bool) async -> Bool {
        guard !isRunning else { return false }
        isRunning = true
        defer { isRunning = false }

        refreshPendingCount()

        guard settings.bool(AppSettingsKey.syncEnabled, default: false) else {
            if manual {
                lastSyncError = "Sync is turned off. Enable it in Settings."
            }
            return false
        }
        if !manual, !settings.bool(AppSettingsKey.autoUpload, default: true) {
            return false
        }
        guard
            let deviceId = settings.string(AppSettingsKey.deviceId), !deviceId.isEmpty,
            settings.string(AppSettingsKey.serverURL)?.isEmpty == false
        else {
            if manual || pendingCount > 0 {
                lastSyncError = "Not paired with a sync server yet. Pair in Settings."
            }
            return false
        }

        // Captures stranded in `.uploading` by a crash/kill rejoin the queue.
        recoverInterruptedUploads()

        // Sweep the queue repeatedly: a capture finalized while a sweep is in
        // flight arrives as a `pendingRerun` kick (or simply as a fresh row in
        // the re-query below) and uploads in a follow-up sweep instead of
        // stranding until the next external trigger. Each capture is attempted
        // at most once per invocation (`attemptedIds`), so a persistently
        // failing server can never spin this loop.
        var passError: String?
        var attemptedIds: Set<String> = []

        repeat {
            pendingRerun = false

            let queue: [CaptureRecord]
            do {
                queue = try store.pendingUploads()
            } catch {
                passError = "Could not read the upload queue."
                logger.error("pendingUploads query failed: \(String(describing: error), privacy: .public)")
                break
            }
            let sweep = queue.filter { !attemptedIds.contains($0.id) }
            guard !sweep.isEmpty else { break }

            logger.info("upload sweep started pending=\(sweep.count) manual=\(manual)")
            isSyncing = true
            lastSyncError = nil

            var aborted = false
            for record in sweep {
                attemptedIds.insert(record.id)
                let outcome = await processCapture(record, deviceId: deviceId)
                refreshPendingCount()
                publishWidgetState()
                if case .failed(let message, let abortPass) = outcome {
                    passError = message
                    if abortPass {
                        aborted = true
                        break
                    }
                }
            }
            if aborted { break }
        } while pendingRerun || hasUnattemptedPendingWork(excluding: attemptedIds)

        isSyncing = false
        lastSyncError = passError
        refreshPendingCount()
        publishWidgetState()
        scheduleUploadRetry()
        logger.info("upload pass finished pending=\(self.pendingCount) failed=\(passError != nil)")
        return true
    }

    /// True when the queue holds captures this invocation has not attempted
    /// yet — e.g. finalized while the last sweep was transferring. A query
    /// failure reads as "no new work" so it cannot keep the loop alive.
    private func hasUnattemptedPendingWork(excluding attemptedIds: Set<String>) -> Bool {
        guard let queue = try? store.pendingUploads() else { return false }
        return queue.contains { !attemptedIds.contains($0.id) }
    }

    /// Outcome of one capture's trip through the pipeline.
    private enum CaptureOutcome {
        /// Uploaded and persisted as `.uploaded`.
        case uploaded
        /// No longer pending (uploaded elsewhere, deleted, ...); nothing done.
        case skipped
        /// Failed; `abortPass` stops the pass (auth/network-wide problems).
        case failed(message: String, abortPass: Bool)
    }

    /// Uploads one capture: create (idempotent by captureId) → initiate
    /// (idempotent for the identical declaration) → parts (re-PUT overwrites)
    /// → complete (idempotent for the same hash). Runs off the main actor.
    nonisolated private func processCapture(_ pending: CaptureRecord, deviceId: String) async -> CaptureOutcome {
        var capture: CaptureRecord
        do {
            guard let fresh = try store.capture(id: pending.id) else { return .skipped }
            capture = fresh
        } catch {
            return .failed(message: "Could not read the capture from local storage.", abortPass: false)
        }
        guard capture.state.isUploadPending else { return .skipped }

        // Preconditions: the finalized audio file and its integrity metadata.
        let fileURL: URL
        do {
            fileURL = try AudioStorage.url(forFileName: capture.fileName)
        } catch {
            return markFailed(&capture, message: "Could not locate the recording file.", abortPass: false)
        }
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            return markFailed(
                &capture,
                message: "The audio file for this capture is missing on this device.",
                abortPass: false)
        }
        guard !capture.sha256.isEmpty, capture.byteLength > 0 else {
            // Finalization never completed its metadata write — treat as corrupt.
            return markFailed(
                &capture,
                message: "The recording was not finalized correctly (missing hash).",
                abortPass: false)
        }
        if let attributes = try? FileManager.default.attributesOfItem(atPath: fileURL.path),
           let size = attributes[.size] as? NSNumber,
           size.int64Value != capture.byteLength {
            return markFailed(
                &capture,
                message: "The audio file changed after it was finalized.",
                abortPass: false)
        }

        capture.state = .uploading
        capture.uploadAttempts += 1
        capture.lastError = nil
        do {
            try store.update(capture)
        } catch {
            return .failed(message: "Could not update local storage.", abortPass: false)
        }

        do {
            // 1. Capture metadata — idempotent replay returns 200 with the
            //    existing capture, so re-runs after partial failures are safe.
            _ = try await api.createCapture(CaptureCreateRequest(
                captureId: capture.id,
                sourceDeviceId: deviceId,
                captureType: capture.type.rawValue,
                capturedAtUtc: capture.capturedAt,
                timezoneId: capture.timezoneId,
                localOffsetMinutes: capture.localOffsetMinutes,
                tripId: nil,
                titleHint: capture.titleHint,
                userNote: capture.userNote,
                // Present only when the location opt-in was on at capture
                // start (design §14.4); the snapshot is never logged (§14.5).
                locationContext: capture.location,
                clientSchemaVersion: 1))

            // 2. Stable artifact ID, persisted BEFORE initiate so retries
            //    re-declare the same artifact instead of minting duplicates.
            let artifactId: String
            if let existing = capture.artifactId, !existing.isEmpty {
                artifactId = existing
            } else {
                artifactId = UUID().uuidString.lowercased()
                capture.artifactId = artifactId
                try store.update(capture)
            }

            // 3. Declare the artifact; the server answers with the part plan.
            let plan = try await api.initiateArtifact(captureId: capture.id, ArtifactInitiateRequest(
                artifactId: artifactId,
                artifactType: "audio_original",
                mediaType: "audio/mp4",
                originalFileName: capture.fileName,
                expectedByteLength: capture.byteLength,
                expectedSha256: capture.sha256))
            if plan.artifactId != artifactId {
                // Defensive: keep the record aligned with the server's ID.
                capture.artifactId = plan.artifactId
                try store.update(capture)
            }

            // 4. Upload the bytes. A 409 here is `artifact_invalid_state` —
            //    the server already completed this artifact and an earlier
            //    pass lost the /complete response, so it now rejects part
            //    re-PUTs. Fall through to the completion claim, which the
            //    server replays with 200 for a matching sha256; the capture
            //    only fails if that completion fails too. (Initiate needs no
            //    such fallback: an identical re-declaration replays 200 even
            //    for a completed artifact, and its only 409,
            //    `artifact_conflict`, means genuinely mismatched declarations.)
            do {
                try await uploadParts(
                    for: capture,
                    artifactId: plan.artifactId,
                    partSizeBytes: plan.partSizeBytes,
                    expectedPartCount: plan.expectedPartCount,
                    fileURL: fileURL)
            } catch let error as SyncAPIError
                where error.statusCode == 409 || error.apiError?.code == "artifact_invalid_state" {
                logger.info("part upload got 409 for id=\(capture.id, privacy: .public); replaying completion for the finished artifact")
            }

            // 5. Completion claim — validated server-side against the
            //    declaration and the assembled bytes.
            _ = try await api.completeArtifact(artifactId: plan.artifactId, ArtifactCompleteRequest(
                partCount: plan.expectedPartCount,
                totalByteLength: capture.byteLength,
                sha256: capture.sha256))

            capture.state = .uploaded
            capture.uploadedAt = Date()
            capture.lastError = nil
            try store.update(capture)
            logger.info("uploaded capture id=\(capture.id, privacy: .public) attempts=\(capture.uploadAttempts)")
            return .uploaded
        } catch let error as SyncAPIError {
            let message: String
            let abortPass: Bool
            if error.needsReauthentication {
                message = "This device is no longer paired. Re-pair in Settings to resume uploads."
                abortPass = true
            } else if error.retryable {
                // Network/server-wide condition — later captures would hit it too.
                message = error.message
                abortPass = true
            } else {
                message = error.message
                abortPass = false
            }
            logger.warning("upload failed id=\(capture.id, privacy: .public) status=\(error.statusCode ?? -1) retryable=\(error.retryable)")
            return markFailed(&capture, message: message, abortPass: abortPass)
        } catch {
            logger.error("upload failed id=\(capture.id, privacy: .public) error=\(String(describing: type(of: error)), privacy: .public)")
            return markFailed(&capture, message: "Upload failed: \(error.localizedDescription)", abortPass: false)
        }
    }

    /// Parks the capture in `.failedRecoverable` with a user-facing reason and
    /// persists it, so History can surface the failure and offer Retry.
    nonisolated private func markFailed(
        _ capture: inout CaptureRecord,
        message: String,
        abortPass: Bool
    ) -> CaptureOutcome {
        capture.state = .failedRecoverable
        capture.lastError = message
        // Copied out before logging: Logger's interpolation is an @autoclosure,
        // which cannot capture an inout parameter.
        let captureId = capture.id
        do {
            try store.update(capture)
        } catch {
            logger.error("could not persist failure state for id=\(captureId, privacy: .public)")
        }
        return .failed(message: message, abortPass: abortPass)
    }

    // MARK: - Part slicing and upload

    /// Uploads the audio bytes according to the server's part plan. A file
    /// that fits one part is streamed directly from its own URL (no copy);
    /// larger files are sliced into `part_%05d` files under
    /// `AudioStorage.partsDirectory()`, uploaded in order, and each slice is
    /// deleted as soon as its part succeeds. Leftover slices (from an earlier
    /// interrupted pass) are removed before slicing and after completion.
    nonisolated private func uploadParts(
        for capture: CaptureRecord,
        artifactId: String,
        partSizeBytes: Int64,
        expectedPartCount: Int,
        fileURL: URL
    ) async throws {
        guard partSizeBytes > 0, expectedPartCount >= 1 else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "The server returned an invalid upload plan.")
        }

        if expectedPartCount == 1 {
            try await api.uploadArtifactPart(artifactId: artifactId, partNumber: 1, fileURL: fileURL)
            return
        }

        let localPartCount = Int((capture.byteLength + partSizeBytes - 1) / partSizeBytes)
        guard localPartCount == expectedPartCount else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "The server's upload plan does not match the file size.")
        }

        let partsDirectory = try AudioStorage.partsDirectory()
        cleanUpPartFiles(in: partsDirectory)

        let reader = try FileHandle(forReadingFrom: fileURL)
        defer { try? reader.close() }

        var remainingTotal = capture.byteLength
        for partNumber in 1...expectedPartCount {
            let partURL = partsDirectory.appendingPathComponent(String(format: "part_%05ld", partNumber))
            let partLength = Swift.min(partSizeBytes, remainingTotal)
            try slice(from: reader, to: partURL, byteCount: partLength)
            try await api.uploadArtifactPart(artifactId: artifactId, partNumber: partNumber, fileURL: partURL)
            try? FileManager.default.removeItem(at: partURL)
            remainingTotal -= partLength
        }

        cleanUpPartFiles(in: partsDirectory)
    }

    /// Streams `byteCount` bytes from `reader` into a fresh file at `partURL`
    /// using bounded 1 MiB chunks, so slicing never loads a part into memory.
    nonisolated private func slice(from reader: FileHandle, to partURL: URL, byteCount: Int64) throws {
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: partURL.path) {
            try fileManager.removeItem(at: partURL)
        }
        guard fileManager.createFile(atPath: partURL.path, contents: nil) else {
            throw SyncAPIError(
                statusCode: nil, apiError: nil, retryable: false,
                message: "Could not create a temporary upload file.")
        }
        let writer = try FileHandle(forWritingTo: partURL)
        // FileHandle.write(contentsOf:) writes through to disk before
        // returning, so the deferred close only releases the descriptor.
        defer { try? writer.close() }

        var remaining = byteCount
        while remaining > 0 {
            let chunkSize = Int(Swift.min(remaining, 1_048_576))
            guard let chunk = try reader.read(upToCount: chunkSize), !chunk.isEmpty else {
                throw SyncAPIError(
                    statusCode: nil, apiError: nil, retryable: false,
                    message: "The audio file is shorter than its recorded size.")
            }
            try writer.write(contentsOf: chunk)
            remaining -= Int64(chunk.count)
        }
    }

    /// Removes every `part_*` slice in the parts directory. Only one upload
    /// pass runs at a time, so a blanket sweep is safe.
    nonisolated private func cleanUpPartFiles(in directory: URL) {
        let fileManager = FileManager.default
        guard let names = try? fileManager.contentsOfDirectory(atPath: directory.path) else { return }
        for name in names where name.hasPrefix("part_") {
            try? fileManager.removeItem(at: directory.appendingPathComponent(name))
        }
    }

    // MARK: - Housekeeping

    /// Captures left in `.uploading` by a crash or process kill would never be
    /// picked up again (`isUploadPending` excludes `.uploading`); repark them
    /// as `.failedRecoverable` so the pass retries them. Only called while the
    /// in-flight guard is held, so no live upload can be affected.
    private func recoverInterruptedUploads() {
        guard let all = try? store.allCaptures() else { return }
        for var record in all where record.state == .uploading {
            record.state = .failedRecoverable
            record.lastError = "Upload was interrupted."
            try? store.update(record)
            logger.warning("recovered interrupted upload id=\(record.id, privacy: .public)")
        }
    }

    private func refreshPendingCount() {
        pendingCount = (try? store.pendingUploadCount()) ?? pendingCount
    }

    /// Publishes the widget snapshot with a fresh pending count. Recording
    /// state and last-capture time belong to the recorder, so the stored
    /// app-group values pass through unchanged unless the caller supplies a
    /// newer capture timestamp.
    private func publishWidgetState(lastCaptureAt: Date? = nil) {
        let defaults = UserDefaults(suiteName: WidgetSharedState.appGroupId)
        let recordingState = defaults?.string(forKey: WidgetSharedState.recordingStateKey) ?? "idle"
        var lastCapture = lastCaptureAt
        if lastCapture == nil,
           let stored = defaults?.object(forKey: WidgetSharedState.lastCaptureAtKey) as? Double,
           stored > 0 {
            lastCapture = Date(timeIntervalSince1970: stored)
        }
        WidgetStatusPublisher.publish(
            recordingState: recordingState,
            pendingUploads: pendingCount,
            lastCaptureAt: lastCapture)
    }

    /// Submits a `BGProcessingTaskRequest` so pending captures upload later
    /// even if the app is suspended before connectivity returns. Skipped when
    /// nothing is pending or the user turned sync/auto-upload off. Submitting
    /// the same identifier again simply replaces the pending request.
    private func scheduleUploadRetry() {
        guard
            pendingCount > 0,
            settings.bool(AppSettingsKey.syncEnabled, default: false),
            settings.bool(AppSettingsKey.autoUpload, default: true)
        else { return }
        let request = BGProcessingTaskRequest(identifier: BackgroundTaskId.uploadRetry)
        request.requiresNetworkConnectivity = true
        request.requiresExternalPower = false
        // Modest floor keeps rapid failure/reschedule cycles paced; the system
        // decides the actual run time (network availability, idle, budget).
        request.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(request)
        logger.debug("scheduled background upload retry pending=\(self.pendingCount)")
    }
}
