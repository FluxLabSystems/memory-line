import AVFAudio
import Foundation
import Observation
import os

/// Records one capture at a time with crash-safe persistence (design §8.4/§16.1):
/// the capture row is inserted in `.recording` state BEFORE audio starts, the
/// file is written incrementally by `AVAudioRecorder`, and `stop()` makes the row
/// durable (`.savedLocal` + size/hash/duration) before the UI reports the capture
/// saved. Captures stranded in `.recording` by a crash or kill are finalized on
/// next launch via `recoverAbandonedRecordings()`.
///
/// All UI-facing state is main-actor bound; only hashing and file-attribute reads
/// hop off the main actor during finalization.
@MainActor
@Observable
final class AudioRecorderService {

    /// In-memory recorder lifecycle driving the capture UI. Distinct from
    /// `CaptureLifecycleState`, which is the persisted pipeline state.
    enum RecorderPhase: Equatable {
        case idle
        case preparing
        case recording
        case paused
        case finalizing
    }

    // MARK: - Observable UI state

    /// Current recorder lifecycle phase.
    private(set) var phase: RecorderPhase = .idle

    /// Recorded time in seconds (excludes paused stretches); updated ~20x/second
    /// while recording and frozen while paused.
    private(set) var elapsed: TimeInterval = 0

    /// Input level normalized to 0...1 for the UI meter; 0 while not recording.
    private(set) var meterLevel: Float = 0

    /// Capture type applied to the next recording. The capture screen persists
    /// changes to `AppSettingsKey.defaultCaptureType`.
    var currentType: CaptureType

    /// Most recent user-visible failure (permission denied, unusable file, ...).
    /// Cleared on the next `start(type:)`; the UI may also set it to nil to dismiss.
    var lastError: String?

    // MARK: - Dependencies

    private let store: any CaptureStore
    private let settings: any SettingsStore
    /// One-shot location for the opt-in snapshot (design §14.4); nil where
    /// the platform offers none. Only consulted once audio is already rolling.
    private let locationProvider: (any CaptureLocationProviding)?
    private let onFinalized: (CaptureRecord) -> Void
    private let log = Logger(subsystem: "com.memoryline.companion", category: "recorder")

    // MARK: - Private recording state

    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var activeRecord: CaptureRecord?
    @ObservationIgnored private var meterTask: Task<Void, Never>?
    @ObservationIgnored private var locationTask: Task<Void, Never>?
    // nonisolated(unsafe) because deinit is nonisolated on a @MainActor class and
    // may only touch Sendable storage; [NSObjectProtocol] is not Sendable. Safe
    // here: the array is written once from the initializer's observer install and
    // read only in deinit, when no other reference to self survives.
    @ObservationIgnored nonisolated(unsafe) private var notificationObservers: [NSObjectProtocol] = []
    /// True only when the current pause was forced by an audio-session
    /// interruption (phone call, Siri, ...). Gates the `.shouldResume`
    /// auto-resume so a user-initiated pause — or a route-loss pause — is
    /// never overridden by the system.
    @ObservationIgnored private var pausedByInterruption = false

    private enum RecorderFailure: LocalizedError {
        case couldNotStart
        case tooShortOrUnreadable

        var errorDescription: String? {
            switch self {
            case .couldNotStart: return "The audio recorder could not start."
            case .tooShortOrUnreadable: return "The recording was too short or unreadable."
            }
        }
    }

    // MARK: - Init

    /// - Parameters:
    ///   - store: durable capture storage; rows are written before/after audio I/O.
    ///   - settings: read for `defaultCaptureType`, `spokenConfirmation` and the
    ///     location opt-in keys.
    ///   - locationProvider: the one-shot location snapshot, consulted only when
    ///     `AppSettingsKey.captureLocation` is on; nil disables the feature.
    ///   - onFinalized: invoked on the main actor after a capture becomes durable
    ///     (`.savedLocal`); the shell forwards this to `UploadCoordinator.captureFinalized(_:)`.
    init(
        store: any CaptureStore,
        settings: any SettingsStore,
        locationProvider: (any CaptureLocationProviding)? = nil,
        onFinalized: @escaping (CaptureRecord) -> Void
    ) {
        self.store = store
        self.settings = settings
        self.locationProvider = locationProvider
        self.onFinalized = onFinalized
        self.currentType = settings.string(AppSettingsKey.defaultCaptureType)
            .flatMap(CaptureType.init(rawValue:)) ?? .memory
        installSessionObservers()
    }

    deinit {
        for observer in notificationObservers {
            NotificationCenter.default.removeObserver(observer)
        }
    }

    // MARK: - Permission

    /// Ensures microphone permission, prompting only when still undetermined.
    /// Returns true when recording is allowed.
    func requestPermission() async -> Bool {
        switch AVAudioApplication.shared.recordPermission {
        case .granted:
            return true
        case .denied:
            return false
        case .undetermined:
            return await AVAudioApplication.requestRecordPermission()
        @unknown default:
            return await AVAudioApplication.requestRecordPermission()
        }
    }

    // MARK: - Recording control

    /// Starts a new recording of `type`. No-op unless idle. On any failure the
    /// service returns to `.idle` with `lastError` set for the UI.
    func start(type: CaptureType) async {
        guard phase == .idle else { return }
        lastError = nil
        phase = .preparing
        currentType = type

        guard await requestPermission() else {
            phase = .idle
            lastError = "Microphone access is off. Enable it in Settings to record."
            log.info("start aborted: microphone permission denied")
            return
        }
        // Re-check after the suspension point: cancelAndDiscard() may have run.
        guard phase == .preparing else { return }

        var record = CaptureRecord.newLocal(type: type, fileName: "")
        record.fileName = "capture_\(record.id).m4a"

        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(
                .playAndRecord,
                mode: .default,
                options: [.allowBluetooth, .defaultToSpeaker]
            )
            try session.setActive(true)

            let url = try AudioStorage.url(forFileName: record.fileName)

            // Persist the row BEFORE any audio is captured (crash safety §8.4/§16.1):
            // a crash mid-recording leaves a `.recording` row that
            // recoverAbandonedRecordings() finalizes on next launch.
            try store.insert(record)

            let recorderSettings: [String: Any] = [
                AVFormatIDKey: Int(kAudioFormatMPEG4AAC),
                AVSampleRateKey: 48_000.0,
                AVNumberOfChannelsKey: 1,
                AVEncoderBitRateKey: 64_000
            ]
            let audioRecorder = try AVAudioRecorder(url: url, settings: recorderSettings)
            audioRecorder.isMeteringEnabled = true
            guard audioRecorder.prepareToRecord(), audioRecorder.record() else {
                throw RecorderFailure.couldNotStart
            }

            recorder = audioRecorder
            activeRecord = record
            elapsed = 0
            meterLevel = 0
            phase = .recording
            startMeterLoop()
            publishWidgetState("recording")
            log.info("recording started captureId=\(record.id, privacy: .public) type=\(type.rawValue, privacy: .public)")
            // Only now, with audio already rolling: the fix is awaited by
            // nobody on this path, so it can never delay the recording.
            attachLocationIfEnabled(to: record.id)
        } catch {
            try? store.delete(id: record.id)
            if let url = try? AudioStorage.url(forFileName: record.fileName) {
                try? FileManager.default.removeItem(at: url)
            }
            deactivateSession()
            phase = .idle
            lastError = "Could not start recording: \(error.localizedDescription)"
            log.error("recording start failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Pauses an active recording (also invoked on audio-session interruptions
    /// and when the input route disappears, e.g. Bluetooth mic dropping off).
    func pause() {
        guard phase == .recording, let recorder else { return }
        // Assume a deliberate pause; the interruption handler re-marks the
        // pause as its own right after calling this.
        pausedByInterruption = false
        if recorder.isRecording {
            elapsed = recorder.currentTime
        }
        recorder.pause()
        phase = .paused
        meterLevel = 0
        publishWidgetState("paused")
        log.info("recording paused at \(self.elapsed, format: .fixed(precision: 1))s")
    }

    /// Resumes a paused recording, reactivating the audio session if an
    /// interruption deactivated it.
    func resume() {
        guard phase == .paused, let recorder else { return }
        pausedByInterruption = false
        do {
            try AVAudioSession.sharedInstance().setActive(true)
        } catch {
            log.warning("session reactivation failed: \(error.localizedDescription, privacy: .public)")
        }
        guard recorder.record() else {
            lastError = "Could not resume recording."
            log.error("resume failed")
            return
        }
        phase = .recording
        publishWidgetState("recording")
        log.info("recording resumed")
    }

    /// Stops and finalizes the active recording. The row is updated to
    /// `.savedLocal` (with byte length, SHA-256, duration) BEFORE this returns
    /// or any confirmation plays (§16.1). Unusable results (under 1 KB or
    /// unreadable) are discarded — file and row — and reported via `lastError`.
    /// Returns the finalized record, or nil when nothing usable was saved.
    @discardableResult
    func stop() async -> CaptureRecord? {
        guard phase == .recording || phase == .paused,
              let recorder, var record = activeRecord else { return nil }
        phase = .finalizing
        pausedByInterruption = false
        stopMeterLoop()
        if recorder.isRecording {
            elapsed = recorder.currentTime
        }
        let duration = elapsed
        let url = recorder.url
        recorder.stop()
        self.recorder = nil
        self.activeRecord = nil
        deactivateSession()

        let finalized: (byteLength: Int64, sha256: String)
        do {
            finalized = try await Self.finalizeAttributes(of: url)
        } catch {
            // Unusable recording: drop the file and the row rather than queueing junk.
            try? FileManager.default.removeItem(at: url)
            try? store.delete(id: record.id)
            resetToIdle()
            publishWidgetState("idle")
            lastError = "The recording was too short or unreadable, so it was discarded."
            log.error("finalize discarded captureId=\(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }

        record.state = .savedLocal
        record.byteLength = finalized.byteLength
        record.sha256 = finalized.sha256
        record.durationSeconds = duration
        record.lastError = nil
        do {
            try store.update(record)
        } catch {
            // The audio file is good; keep it and the `.recording` row so
            // recovery finalizes it on next launch instead of losing it (§8.4).
            resetToIdle()
            publishWidgetState("idle")
            lastError = "Could not save the capture record. It will be recovered on next launch."
            log.error("finalize update failed captureId=\(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }

        resetToIdle()
        publishWidgetState("idle")
        ConfirmationFeedback.captureSaved(
            spoken: settings.bool(AppSettingsKey.spokenConfirmation, default: true)
        )
        onFinalized(record)
        log.info("capture finalized captureId=\(record.id, privacy: .public) bytes=\(finalized.byteLength) duration=\(duration, format: .fixed(precision: 1))s")
        return record
    }

    /// Abandons the active recording: stops the recorder and deletes both the
    /// audio file and the capture row. Safe to call in any active phase.
    func cancelAndDiscard() async {
        guard phase == .recording || phase == .paused || phase == .preparing else { return }
        pausedByInterruption = false
        stopMeterLoop()
        locationTask?.cancel()
        locationTask = nil
        let recorder = self.recorder
        let record = self.activeRecord
        self.recorder = nil
        self.activeRecord = nil
        recorder?.stop()
        deactivateSession()

        if let record {
            if let url = try? AudioStorage.url(forFileName: record.fileName) {
                try? FileManager.default.removeItem(at: url)
            }
            try? store.delete(id: record.id)
            log.info("recording cancelled captureId=\(record.id, privacy: .public)")
        }
        resetToIdle()
        publishWidgetState("idle")
    }

    // MARK: - Crash recovery

    /// Finalizes captures stranded in `.recording` by a crash or kill (§8.4):
    /// a usable file becomes a `.savedLocal` capture (size + hash, duration
    /// best-effort) and is handed to `onFinalized`; rows whose file is missing
    /// or unusable are removed. Call once at launch, before recording starts.
    func recoverAbandonedRecordings() {
        guard phase == .idle else { return }
        let abandoned: [CaptureRecord]
        do {
            abandoned = try store.abandonedRecordings()
        } catch {
            log.error("abandoned-recording scan failed: \(error.localizedDescription, privacy: .public)")
            return
        }
        guard !abandoned.isEmpty else { return }
        log.info("recovering \(abandoned.count) abandoned recording(s)")

        for var record in abandoned {
            guard let url = try? AudioStorage.url(forFileName: record.fileName),
                  FileManager.default.fileExists(atPath: url.path) else {
                try? store.delete(id: record.id)
                continue
            }
            let byteLength = ((try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.int64Value ?? 0
            guard byteLength >= 1024, let sha256 = try? FileHasher.sha256Hex(of: url) else {
                try? FileManager.default.removeItem(at: url)
                try? store.delete(id: record.id)
                log.info("dropped unusable abandoned recording captureId=\(record.id, privacy: .public)")
                continue
            }
            record.state = .savedLocal
            record.byteLength = byteLength
            record.sha256 = sha256
            record.durationSeconds = (try? AVAudioPlayer(contentsOf: url))?.duration ?? 0
            record.lastError = nil
            do {
                try store.update(record)
                onFinalized(record)
                log.info("recovered captureId=\(record.id, privacy: .public) bytes=\(byteLength)")
            } catch {
                log.error("recovery update failed captureId=\(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
        publishWidgetState("idle")
    }

    // MARK: - Location snapshot (design §14.4)

    /// Fires the one-shot location request after recording has started, so it
    /// can never delay the first second of audio. Off unless the user turned
    /// on `AppSettingsKey.captureLocation`; precise only when its own switch
    /// is on too. The snapshot lands on the active record when it arrives, or
    /// — if the user already stopped — on the stored row, as long as the
    /// capture has not left the phone yet.
    private func attachLocationIfEnabled(to captureId: String) {
        locationTask?.cancel()
        locationTask = nil
        guard let locationProvider,
              settings.bool(AppSettingsKey.captureLocation, default: false) else { return }
        let precise = settings.bool(AppSettingsKey.captureLocationPrecise, default: false)
        locationTask = Task { [weak self] in
            let snapshot = await locationProvider.snapshot(precise: precise)
            guard !Task.isCancelled, let self, let snapshot else { return }
            self.attach(snapshot, to: captureId)
        }
    }

    private func attach(_ snapshot: LocationContext, to captureId: String) {
        if activeRecord?.id == captureId {
            activeRecord?.location = snapshot
            log.info("location attached to active captureId=\(captureId, privacy: .public)")
            return
        }
        // Stopped before the fix arrived: attach to the saved row while the
        // upload has not started; once it has, the create request is already
        // on its way without one, and a late snapshot would never be sent.
        guard var stored = try? store.capture(id: captureId), stored.state.isUploadPending else { return }
        stored.location = snapshot
        do {
            try store.update(stored)
            log.info("location attached to saved captureId=\(captureId, privacy: .public)")
        } catch {
            log.error("location attach failed captureId=\(captureId, privacy: .public): \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: - Metering

    private func startMeterLoop() {
        meterTask?.cancel()
        meterTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                self.tickMeter()
                try? await Task.sleep(for: .milliseconds(50))
            }
        }
    }

    private func stopMeterLoop() {
        meterTask?.cancel()
        meterTask = nil
    }

    private func tickMeter() {
        guard phase == .recording, let recorder else { return }
        recorder.updateMeters()
        elapsed = recorder.currentTime
        // averagePower is dBFS in -160...0; map -50...0 dB onto 0...1.
        let power = recorder.averagePower(forChannel: 0)
        let floorDb: Float = -50
        meterLevel = max(0, min(1, (power - floorDb) / -floorDb))
    }

    // MARK: - Session plumbing

    private func installSessionObservers() {
        let center = NotificationCenter.default
        let session = AVAudioSession.sharedInstance()

        let interruption = center.addObserver(
            forName: AVAudioSession.interruptionNotification,
            object: session,
            queue: nil
        ) { [weak self] notification in
            guard let info = notification.userInfo,
                  let rawType = info[AVAudioSessionInterruptionTypeKey] as? UInt,
                  let type = AVAudioSession.InterruptionType(rawValue: rawType) else { return }
            let options = (info[AVAudioSessionInterruptionOptionKey] as? UInt)
                .map(AVAudioSession.InterruptionOptions.init(rawValue:)) ?? []
            Task { @MainActor in
                guard let self else { return }
                switch type {
                case .began:
                    // Only a pause forced HERE may auto-resume later; a
                    // recorder the user already paused must stay paused.
                    if self.phase == .recording {
                        self.pause()
                        self.pausedByInterruption = true
                    }
                case .ended where options.contains(.shouldResume):
                    if self.pausedByInterruption {
                        self.pausedByInterruption = false
                        self.resume()
                    }
                default:
                    break
                }
            }
        }

        let routeChange = center.addObserver(
            forName: AVAudioSession.routeChangeNotification,
            object: session,
            queue: nil
        ) { [weak self] notification in
            guard let rawReason = notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
                  let reason = AVAudioSession.RouteChangeReason(rawValue: rawReason),
                  reason == .oldDeviceUnavailable else { return }
            Task { @MainActor in
                self?.pause()
            }
        }

        notificationObservers = [interruption, routeChange]
    }

    private func deactivateSession() {
        do {
            try AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        } catch {
            log.warning("session deactivation failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: - Helpers

    private func resetToIdle() {
        phase = .idle
        elapsed = 0
        meterLevel = 0
    }

    private func publishWidgetState(_ recordingState: String) {
        let pending = (try? store.pendingUploadCount()) ?? 0
        let lastCaptureAt = (try? store.allCaptures())?
            .first(where: { $0.state != .recording })?
            .capturedAt
        WidgetStatusPublisher.publish(
            recordingState: recordingState,
            pendingUploads: pending,
            lastCaptureAt: lastCaptureAt
        )
    }

    /// Reads the finalized file's size and SHA-256 off the main actor — hashing
    /// a long drive's recording is too slow for the UI thread.
    private static func finalizeAttributes(of url: URL) async throws -> (byteLength: Int64, sha256: String) {
        try await Task.detached(priority: .userInitiated) {
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            let byteLength = (attributes[.size] as? NSNumber)?.int64Value ?? 0
            guard byteLength >= 1024 else {
                throw RecorderFailure.tooShortOrUnreadable
            }
            let sha256 = try FileHasher.sha256Hex(of: url)
            return (byteLength, sha256)
        }.value
    }
}
