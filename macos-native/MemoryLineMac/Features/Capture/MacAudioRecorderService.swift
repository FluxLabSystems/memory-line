import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation
import Observation
import os

/// The Mac's microphone recorder: `MacAudioRecording` on top of `AVAudioEngine`,
/// writing 16 kHz mono 16-bit PCM WAV into `AudioStorage.recordingsDirectory()`.
///
/// It is a reimplementation of the iOS `AudioRecorderService`, not a port. The
/// iOS type is built around `AVAudioSession` — category, activation,
/// interruption and route-change notifications — none of which exists on macOS.
/// What *is* carried over verbatim is the persistence discipline (§8.4/§16.1),
/// because that is a durability contract rather than a platform detail:
///
///  1. the capture row is inserted in `.recording` state **before** the
///     microphone opens, so a crash mid-recording leaves a row that
///     `recoverAbandonedRecordings()` can finalise on the next launch;
///  2. `stop()` writes `.savedLocal` + byte length + SHA-256 + duration to the
///     store **before** it returns, so the record the caller receives (and the
///     upload pipeline picks up) is already durable;
///  3. `cancel()` removes both the partial file and the row, leaving nothing
///     for the upload pipeline or the recovery sweep to find.
///
/// ## Why `AVAudioEngine` and not `AVAudioRecorder`
///
/// `AVAudioRecorder` can be configured for LinearPCM and would produce a
/// conforming WAV with far less code, but on macOS it always records from the
/// **system default input** — there is no `AVAudioSession` to route it and no
/// device property on the recorder itself. Honouring `start(deviceId:)` with it
/// would mean changing the user's system-wide default input device, which is
/// not ours to change. `AVAudioEngine` exposes the input node's underlying
/// AUHAL audio unit, and `kAudioOutputUnitProperty_CurrentDevice` on that unit
/// selects the device for this process only. Device selection is the whole
/// reason the Mac seam has a `deviceId` parameter (port plan §4.1), so the
/// engine wins.
///
/// ## Why the format is fixed, and where it is enforced
///
/// The Windows side transcribes these recordings with Whisper, which parses
/// **only 16 kHz 16-bit PCM WAV** (`WhisperCompatibleFormats` is `.wav`, and
/// `WhisperSpeechToTextService` rejects anything without a RIFF/WAVE header).
/// The hardware almost never offers that format — a built-in Mac microphone is
/// typically 48 kHz float32 — so every tap buffer goes through an
/// `AVAudioConverter` into the target format before it reaches the file. The
/// `.wav` path extension is what makes `AVAudioFile` choose the RIFF container,
/// so the file name and the settings dictionary have to agree; both are derived
/// from `outputFormat` below.
///
/// ## Threading
///
/// Everything the UI reads is main-actor state on an `@Observable` class. The
/// audio callback runs on AVFAudio's own render thread and never touches this
/// object: it drives a `RecordingSink`, which converts, hands the converted
/// buffer to a serial writer queue, and publishes a level/frame count behind a
/// lock. The main actor samples that snapshot on a 20 Hz loop, exactly as the
/// iOS recorder samples `AVAudioRecorder`'s meters. No file I/O happens on the
/// main thread: writes ride the writer queue, and hashing runs on a detached
/// task.
@MainActor
@Observable
final class MacAudioRecorderService: MacAudioRecording {

    // MARK: - Fixed output format

    /// Whisper's required input rate. Also the unit of `elapsed`/`durationSeconds`:
    /// both are derived from frames written, not wall-clock time.
    private static let outputSampleRate: Double = 16_000
    private static let outputChannelCount: AVAudioChannelCount = 1

    /// Requested tap size in *input* frames (~85 ms at 48 kHz). AVFAudio treats
    /// this as a hint and may hand back a different length, so nothing here
    /// depends on the value beyond keeping callbacks reasonably infrequent.
    private static let tapBufferSize: AVAudioFrameCount = 4096

    /// Files smaller than this are discarded rather than queued for upload.
    /// At 16 kHz mono 16-bit that is roughly 32 ms of audio, so in practice the
    /// test is "did any audio reach the file at all, or is this just a header".
    /// The same floor is applied by the iOS recorder and by the recovery sweep.
    private static let minimumUsableBytes: Int64 = 1024

    // MARK: - Observable UI state

    private(set) var state: MacRecorderState = .idle
    private(set) var level: Double = 0
    private(set) var elapsed: TimeInterval = 0

    /// Cached so SwiftUI re-renders when the answer changes. `AVCaptureDevice`
    /// has no change notification, and the user can flip the switch in System
    /// Settings while the app is running, so views that care should call
    /// `refreshAuthorization()` when they become active.
    private(set) var authorization: MacMicrophoneAuthorization

    /// Capture type applied to the next recording. `MacAudioRecording.start`
    /// carries no type, so this is how the capture UI says what the next
    /// recording is.
    ///
    /// Read and written straight through `AppSettingsKey.defaultCaptureType`
    /// rather than cached: the capture screen persists its own picker to that
    /// key, and a recorder that read the key once at launch would keep filing
    /// captures under the old type for the rest of the session. Going through
    /// the store means the two cannot disagree however the composition root
    /// wires them. Not observation-tracked — the settings store is not
    /// observable — exactly as `MacAppEnvironment.isPaired` is not.
    var currentType: CaptureType {
        get {
            settings.string(AppSettingsKey.defaultCaptureType)
                .flatMap(CaptureType.init(rawValue:)) ?? .memory
        }
        set { settings.set(newValue.rawValue, for: AppSettingsKey.defaultCaptureType) }
    }

    // MARK: - Dependencies

    private let store: any CaptureStore
    private let settings: any SettingsStore
    /// Invoked on the main actor once a capture is durable, so the upload
    /// pipeline can start without waiting for its next tick. The composition
    /// root wires it to `MacUploading.captureFinalized(_:)`; it is optional
    /// only so a test or a preview can build a recorder with no uploader.
    private let onFinalized: ((CaptureRecord) -> Void)?
    private let logger = AppLog.logger(category: "recorder")

    // MARK: - Private recording state

    @ObservationIgnored private var engine: AVAudioEngine?
    @ObservationIgnored private var sink: RecordingSink?
    @ObservationIgnored private var activeRecord: CaptureRecord?
    /// Held rather than rebuilt from `AudioStorage` so the teardown paths cannot
    /// fail to find the file they need to delete.
    @ObservationIgnored private var activeURL: URL?
    @ObservationIgnored private var meterTask: Task<Void, Never>?
    /// True from the moment `stop()`/`cancel()` takes ownership of the active
    /// capture until it has finished with it. `stop()` suspends (hashing runs
    /// off the main actor), and a second user action — the toolbar button plus
    /// a menu command plus a hotkey all exist on macOS — could otherwise start
    /// a new recording on top of a half-finalised one.
    @ObservationIgnored private var isFinalizing = false
    // nonisolated(unsafe) for the same reason the iOS recorder needs it: deinit
    // is nonisolated on a @MainActor class and may only touch Sendable storage,
    // and NSObjectProtocol is not Sendable. Safe here because the token is only
    // written from main-actor code and read in deinit, when no other reference
    // to self survives.
    @ObservationIgnored nonisolated(unsafe) private var configurationObserver: NSObjectProtocol?

    /// Failures the caller may surface verbatim. Deliberately free of file
    /// paths, device names and OSStatus codes — those go to the log, not to the
    /// user (design §14.5).
    private enum RecorderFailure: LocalizedError {
        case microphoneDenied
        case noInputDevice
        case unsupportedInputFormat
        case couldNotStart
        case tooShortOrUnreadable
        case couldNotSaveCapture

        var errorDescription: String? {
            switch self {
            case .microphoneDenied:
                return "Memory Line does not have permission to use the microphone. "
                    + "Enable it in System Settings ▸ Privacy & Security ▸ Microphone."
            case .noInputDevice:
                return "No usable audio input is connected."
            case .unsupportedInputFormat:
                return "This input's audio format cannot be recorded. Try a different input device."
            case .couldNotStart:
                return "The audio input could not be started."
            case .tooShortOrUnreadable:
                return "The recording was too short or unreadable."
            case .couldNotSaveCapture:
                return "The capture could not be saved."
            }
        }
    }

    // MARK: - Init

    /// - Parameters:
    ///   - store: durable capture storage; rows are written before and after audio I/O.
    ///   - settings: backs `currentType` via `AppSettingsKey.defaultCaptureType`.
    ///   - onFinalized: invoked on the main actor after a capture becomes durable.
    init(
        store: any CaptureStore,
        settings: any SettingsStore,
        onFinalized: ((CaptureRecord) -> Void)? = nil
    ) {
        self.store = store
        self.settings = settings
        self.onFinalized = onFinalized
        self.authorization = Self.currentAuthorization()
    }

    deinit {
        if let configurationObserver {
            NotificationCenter.default.removeObserver(configurationObserver)
        }
    }

    // MARK: - Authorization

    /// Re-reads the microphone authorization without prompting. Call it when a
    /// capture view appears: the user may have granted or revoked access in
    /// System Settings since the last read.
    @discardableResult
    func refreshAuthorization() -> MacMicrophoneAuthorization {
        authorization = Self.currentAuthorization()
        return authorization
    }

    func requestAuthorization() async -> MacMicrophoneAuthorization {
        let current = Self.currentAuthorization()
        guard current == .undetermined else {
            authorization = current
            return current
        }
        // Only ever prompts once per install; afterwards TCC answers from its
        // own database and this returns the stored decision immediately.
        let granted = await AVCaptureDevice.requestAccess(for: .audio)
        authorization = granted ? .authorized : .denied
        return authorization
    }

    /// `AVCaptureDevice` is the only microphone-authorization API on macOS —
    /// `AVAudioApplication.recordPermission` is iOS-only, and TCC treats
    /// "microphone" as one permission regardless of which framework asks.
    private static func currentAuthorization() -> MacMicrophoneAuthorization {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            return .authorized
        case .notDetermined:
            return .undetermined
        case .denied, .restricted:
            return .denied
        @unknown default:
            // A status this build does not know about is not consent.
            return .denied
        }
    }

    // MARK: - Recording control

    /// Starts a recording, honouring `deviceId` when that device is still
    /// present and falling back to the system default otherwise.
    ///
    /// Ordering matters: the capture row is committed before `engine.start()`,
    /// and `state` only becomes `.recording` once audio is actually flowing —
    /// so the UI is never told a recording succeeded that the store does not
    /// know about. If anything between those two points fails, the row and the
    /// part-written file are both removed and the reason is thrown.
    func start(deviceId: String?) async throws {
        guard activeRecord == nil, !isFinalizing else {
            // A second start is a UI double-fire (button + menu item + hotkey
            // all reach here), not a programming error worth an alert.
            logger.notice("start ignored: a capture is already in progress")
            return
        }
        if case .failed = state { state = .idle }

        guard await requestAuthorization() == .authorized else {
            logger.info("start refused: microphone access not granted")
            throw RecorderFailure.microphoneDenied
        }
        // The authorization prompt suspends; re-check that nothing else claimed
        // the recorder while we were away.
        guard activeRecord == nil, !isFinalizing else { return }

        var record = CaptureRecord.newLocal(type: currentType, fileName: "")
        record.fileName = "capture_\(record.id).wav"
        let url = try AudioStorage.url(forFileName: record.fileName)

        do {
            try store.insert(record)
        } catch {
            logger.error("capture row insert failed: \(error.localizedDescription, privacy: .public)")
            throw RecorderFailure.couldNotSaveCapture
        }

        do {
            let engine = AVAudioEngine()
            let input = engine.inputNode
            // Before reading the input format: the format belongs to the
            // device, so selecting one afterwards would leave the converter
            // configured for the wrong sample rate.
            selectInputDevice(deviceId, on: input)

            let inputFormat = input.outputFormat(forBus: 0)
            // A Mac with no input at all reports a zeroed format here rather
            // than failing, and installing a tap with it throws an ObjC
            // exception that Swift cannot catch — so check before touching it.
            guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else {
                throw RecorderFailure.noInputDevice
            }

            // Held as an explicit Optional: these AVFAudio initialisers are
            // failable, and an input layout the converter cannot handle (an
            // aggregate device with an unusual channel count, say) surfaces as
            // nil rather than as a thrown error.
            let target: AVAudioFormat? = AVAudioFormat(
                commonFormat: .pcmFormatInt16,
                sampleRate: Self.outputSampleRate,
                channels: Self.outputChannelCount,
                interleaved: true)
            guard let outputFormat = target else { throw RecorderFailure.unsupportedInputFormat }
            // Default channel mapping: mono passes through, stereo is downmixed.
            // If a multi-channel interface ever fails here, the fix is an
            // explicit `converter.channelMap = [0]` (take the first channel)
            // rather than loosening the output format.
            guard let converter = AVAudioConverter(from: inputFormat, to: outputFormat) else {
                throw RecorderFailure.unsupportedInputFormat
            }

            // `outputFormat.settings` and the commonFormat/interleaved pair
            // describe the same thing on purpose: AVAudioFile would otherwise
            // convert between its file format and its processing format behind
            // our back, and `write(from:)` rejects buffers whose format differs
            // from the processing format.
            let file = try AVAudioFile(
                forWriting: url,
                settings: outputFormat.settings,
                commonFormat: .pcmFormatInt16,
                interleaved: true)

            let sink = RecordingSink(converter: converter, outputFormat: outputFormat, file: file)
            // The tap block captures only the sink, never self: it runs on the
            // render thread, and the main-actor recorder has no business being
            // touched from there.
            input.installTap(onBus: 0, bufferSize: Self.tapBufferSize, format: inputFormat) { buffer, _ in
                sink.accept(buffer)
            }

            engine.prepare()
            try engine.start()

            self.engine = engine
            self.sink = sink
            self.activeRecord = record
            self.activeURL = url
            elapsed = 0
            level = 0
            state = .recording
            observeConfigurationChanges(of: engine)
            startMeterLoop()
            logger.info(
                "recording started captureId=\(record.id, privacy: .public) type=\(record.type.rawValue, privacy: .public)")
        } catch {
            // Nothing usable was produced, so leave no trace: an orphaned
            // `.recording` row would be picked up by the recovery sweep and an
            // orphaned file would never be uploaded or deleted.
            try? store.delete(id: record.id)
            try? FileManager.default.removeItem(at: url)
            state = .idle
            logger.error("recording start failed: \(error.localizedDescription, privacy: .public)")
            throw (error as? RecorderFailure) ?? RecorderFailure.couldNotStart
        }
    }

    /// Pauses by stopping the engine, which also drops the recording indicator
    /// in the menu bar — the honest signal for an app that is holding an open
    /// microphone. The sink is flagged first so a buffer already in flight on
    /// the render thread is discarded instead of landing after the pause.
    func pause() {
        guard state == .recording, let engine else { return }
        sink?.setPaused(true)
        engine.pause()
        state = .paused
        level = 0
        logger.info("recording paused at \(self.elapsed, format: .fixed(precision: 1))s")
    }

    /// Resumes the paused engine. The file, converter and tap all survive a
    /// pause, so the recording continues into the same file and `elapsed`
    /// (derived from frames written) never counts the paused stretch.
    func resume() {
        guard state == .paused, let engine else { return }
        do {
            try engine.start()
        } catch {
            // The input may have been unplugged while we were paused. The
            // partial capture is still on disk and still owned by this
            // recorder, so `stop()` can finalise it from the `.failed` state.
            logger.error("resume failed: \(error.localizedDescription, privacy: .public)")
            state = .failed("Recording could not resume. The audio input may have changed.")
            return
        }
        sink?.setPaused(false)
        state = .recording
        logger.info("recording resumed")
    }

    /// Finalises the recording and returns the stored `.savedLocal` capture.
    ///
    /// Three outcomes, and the difference is deliberate:
    ///  - **saved** — the row is updated before this returns, `onFinalized` has
    ///    fired, and the record comes back;
    ///  - **nothing usable** (silence, an instant stop, an unreadable file) —
    ///    file and row are both discarded, `state` carries a display-safe
    ///    explanation, and this returns nil rather than throwing: mechanically
    ///    the user's action worked;
    ///  - **could not persist** — the audio is good, so the file and the
    ///    `.recording` row are *kept* for `recoverAbandonedRecordings()`, and
    ///    this throws so the caller can say so at the point of the action.
    ///
    /// Keyed on there being an active capture rather than on `state`, so a
    /// recording stranded in `.failed` by a failed `resume()` can still be
    /// finalised instead of being orphaned.
    @discardableResult
    func stop() async throws -> CaptureRecord? {
        guard !isFinalizing, let sink, var record = activeRecord, let url = activeURL else { return nil }
        isFinalizing = true
        defer { isFinalizing = false }

        stopMeterLoop()
        teardownEngine()
        // Drains the writer queue and drops the last reference to the
        // AVAudioFile. AVAudioFile has no close(): deallocation is what writes
        // the final RIFF/data chunk lengths into the header, and hashing a file
        // whose header still claims zero frames would hand Whisper a file it
        // would reject.
        await sink.finish()

        let snapshot = sink.snapshot()
        if snapshot.failed {
            logger.notice("captureId=\(record.id, privacy: .public) lost at least one audio buffer")
        }
        // Frames written, not wall-clock: paused stretches contribute nothing,
        // and this is exactly the frame count the file now contains.
        let duration = Double(snapshot.frames) / Self.outputSampleRate

        self.sink = nil
        self.activeRecord = nil
        self.activeURL = nil
        level = 0
        elapsed = 0
        state = .idle

        let finalized: (byteLength: Int64, sha256: String)
        do {
            finalized = try await Self.fileAttributes(of: url)
        } catch {
            try? FileManager.default.removeItem(at: url)
            try? store.delete(id: record.id)
            state = .failed("The recording was too short or unreadable, so it was discarded.")
            logger.error(
                "finalise discarded captureId=\(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
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
            state = .failed("Could not save the capture. It will be recovered the next time Memory Line starts.")
            logger.error(
                "finalise update failed captureId=\(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            throw RecorderFailure.couldNotSaveCapture
        }

        onFinalized?(record)
        logger.info(
            "capture finalised captureId=\(record.id, privacy: .public) bytes=\(finalized.byteLength) duration=\(duration, format: .fixed(precision: 1))s")
        return record
    }

    /// Abandons the recording: the part-written file and the capture row both
    /// go. If the row delete fails the sweep at next launch removes it anyway —
    /// it finds a `.recording` row whose file is missing, which is exactly the
    /// case it is written to clean up.
    func cancel() async {
        guard !isFinalizing, let sink, let record = activeRecord else { return }
        isFinalizing = true
        defer { isFinalizing = false }

        stopMeterLoop()
        teardownEngine()
        await sink.finish()

        let url = activeURL
        self.sink = nil
        self.activeRecord = nil
        self.activeURL = nil

        if let url {
            try? FileManager.default.removeItem(at: url)
        }
        try? store.delete(id: record.id)

        level = 0
        elapsed = 0
        state = .idle
        logger.info("recording cancelled captureId=\(record.id, privacy: .public)")
    }

    // MARK: - Crash recovery

    /// Finalises captures stranded in `.recording` by a crash or a force quit
    /// (§8.4), the counterpart of inserting the row before the microphone
    /// opens. Call it once at launch, before any recording starts.
    ///
    /// Unlike the iOS sweep this insists the file parses as PCM with a non-zero
    /// frame count. A WAV whose writer died mid-recording can carry a header
    /// that still claims zero frames, and a zero-length WAV is worse than no
    /// capture: it would upload cleanly and then fail transcription on Windows.
    func recoverAbandonedRecordings() async {
        guard activeRecord == nil, !isFinalizing else { return }

        let abandoned: [CaptureRecord]
        do {
            abandoned = try store.abandonedRecordings()
        } catch {
            logger.error("abandoned-recording scan failed: \(error.localizedDescription, privacy: .public)")
            return
        }
        guard !abandoned.isEmpty else { return }
        logger.info("recovering \(abandoned.count) abandoned recording(s)")

        for var record in abandoned {
            guard let url = try? AudioStorage.url(forFileName: record.fileName),
                  FileManager.default.fileExists(atPath: url.path) else {
                try? store.delete(id: record.id)
                continue
            }
            let attributes = try? await Self.fileAttributes(of: url)
            // A header read, not a decode — cheap enough to stay on the main
            // actor, exactly as the iOS sweep reads a duration here.
            let duration = Self.pcmDuration(of: url)
            guard let attributes, duration > 0 else {
                try? FileManager.default.removeItem(at: url)
                try? store.delete(id: record.id)
                logger.info("dropped unusable abandoned recording captureId=\(record.id, privacy: .public)")
                continue
            }

            record.state = .savedLocal
            record.byteLength = attributes.byteLength
            record.sha256 = attributes.sha256
            record.durationSeconds = duration
            record.lastError = nil
            do {
                try store.update(record)
                onFinalized?(record)
                logger.info("recovered captureId=\(record.id, privacy: .public) bytes=\(attributes.byteLength)")
            } catch {
                logger.error(
                    "recovery update failed captureId=\(record.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    // MARK: - Input device selection

    /// Points the engine's input node at `requestedUID`, or leaves it on the
    /// system default.
    ///
    /// Never throws and never fails the recording: a remembered device that is
    /// no longer plugged in is the *normal* case on a Mac (port plan §4.1), and
    /// refusing to record because a headset is in another room would be absurd.
    /// Doing nothing is precisely the fallback we want — a freshly built
    /// `AVAudioEngine` already uses the current default input.
    private func selectInputDevice(_ requestedUID: String?, on input: AVAudioInputNode) {
        guard let requestedUID, !requestedUID.isEmpty else { return }

        guard let deviceID = Self.audioDeviceID(forUID: requestedUID), Self.hasInputChannels(deviceID) else {
            logger.notice("remembered input device is not available; using the system default")
            return
        }
        guard let audioUnit = input.audioUnit else {
            logger.notice("input node exposes no audio unit; using the system default")
            return
        }

        var device = deviceID
        let status = AudioUnitSetProperty(
            audioUnit,
            kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global,
            0,
            &device,
            UInt32(MemoryLayout<AudioDeviceID>.size))
        if status != noErr {
            logger.notice("could not select the requested input device (status \(status)); using the system default")
        }
    }

    /// Resolves a persisted CoreAudio UID to the numeric device ID the audio
    /// unit wants.
    ///
    /// `kAudioHardwarePropertyTranslateUIDToDevice` reports a *missing* device
    /// by returning `kAudioObjectUnknown` with no error, which is the hook the
    /// fallback above hangs on — the UID is persisted precisely because it
    /// survives reboots and reconnects while the numeric ID does not.
    private static func audioDeviceID(forUID uid: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyTranslateUIDToDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var cfUID = uid as CFString
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)

        let status = withUnsafeMutablePointer(to: &cfUID) { qualifier in
            AudioObjectGetPropertyData(
                AudioObjectID(kAudioObjectSystemObject),
                &address,
                UInt32(MemoryLayout<CFString>.size),
                qualifier,
                &size,
                &deviceID)
        }
        guard status == noErr, deviceID != AudioDeviceID(kAudioObjectUnknown) else { return nil }
        return deviceID
    }

    /// True when the device actually offers input channels. A UID can resolve
    /// to a device that exists but records nothing — an output-only device, or
    /// an aggregate whose input half has gone away — and pointing the AUHAL at
    /// one yields a silent recording rather than an error.
    private static func hasInputChannels(_ deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)

        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &size) == noErr, size > 0 else {
            return false
        }
        // AudioBufferList is variable-length, so it has to be allocated at the
        // size CoreAudio just reported rather than as a Swift value.
        let raw = UnsafeMutableRawPointer.allocate(
            byteCount: Int(size),
            alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, raw) == noErr else {
            return false
        }
        let buffers = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return buffers.contains { $0.mNumberChannels > 0 }
    }

    // MARK: - Engine lifecycle

    /// Watches for the engine losing its I/O configuration — a USB interface
    /// pulled mid-sentence, a dock disconnected. AVFAudio posts this for benign
    /// changes too (a new default output, a sample-rate change elsewhere), so
    /// the handler only acts when the engine has actually stopped.
    private func observeConfigurationChanges(of engine: AVAudioEngine) {
        configurationObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                await self.handleConfigurationChange()
            }
        }
    }

    private func handleConfigurationChange() async {
        guard state == .recording, let engine else { return }
        guard !engine.isRunning else {
            logger.notice("audio configuration changed; engine still running")
            return
        }
        // The input is gone and no more buffers are coming. Finalising keeps
        // what the user already said — far better than leaving a live-looking
        // recorder writing silence to a file nobody will finish.
        logger.notice("input configuration changed mid-recording; finalising early")
        // `try?` around an optional-returning call nests the optionals; flatten
        // so "stop threw" and "stop saved nothing" read the same here.
        let saved = (try? await stop()) ?? nil
        state = .failed(saved == nil
            ? "The audio input changed and recording stopped."
            : "The audio input changed, so recording stopped. What was recorded has been saved.")
    }

    private func teardownEngine() {
        if let configurationObserver {
            NotificationCenter.default.removeObserver(configurationObserver)
            self.configurationObserver = nil
        }
        guard let engine else { return }
        engine.stop()
        engine.inputNode.removeTap(onBus: 0)
        self.engine = nil
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

    /// Samples the sink rather than being pushed to from the render thread:
    /// one main-actor hop per 50 ms instead of one per audio buffer, and the UI
    /// updates at a fixed rate whatever the hardware's buffer size happens
    /// to be.
    private func tickMeter() {
        guard state == .recording, let sink else { return }
        let snapshot = sink.snapshot()
        elapsed = Double(snapshot.frames) / Self.outputSampleRate
        level = snapshot.level
    }

    // MARK: - Finalisation helpers

    /// Size and SHA-256 of the finished file, off the main actor — hashing an
    /// hour of audio is far too slow to do on the UI thread (§8.5/§16.1).
    private static func fileAttributes(of url: URL) async throws -> (byteLength: Int64, sha256: String) {
        try await Task.detached(priority: .userInitiated) {
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            let byteLength = (attributes[.size] as? NSNumber)?.int64Value ?? 0
            guard byteLength >= MacAudioRecorderService.minimumUsableBytes else {
                throw RecorderFailure.tooShortOrUnreadable
            }
            let sha256 = try FileHasher.sha256Hex(of: url)
            return (byteLength, sha256)
        }.value
    }

    /// Duration from the file's own frame count. Only used by the recovery
    /// sweep — a live `stop()` counts frames as it writes them, which stays
    /// correct even if the header does not.
    private static func pcmDuration(of url: URL) -> Double {
        guard let file = try? AVAudioFile(forReading: url) else { return 0 }
        let sampleRate = file.processingFormat.sampleRate
        guard sampleRate > 0 else { return 0 }
        return Double(file.length) / sampleRate
    }
}

// MARK: - Render-thread half

/// Everything that runs off the main actor while recording: format conversion
/// on the render thread, file writing on a serial queue, and a small snapshot
/// the main actor polls for the meter.
///
/// A separate object rather than methods on the recorder, so the tap block has
/// nothing main-actor-isolated to capture. `@unchecked Sendable` is the honest
/// annotation: `converter` is touched only from the render thread (it is
/// stateful — a resampler carries filter state between buffers — so it must be
/// used serially, which a single tap guarantees), `file` only from
/// `writerQueue`, and everything shared between them is behind the lock.
private final class RecordingSink: @unchecked Sendable {

    /// What the main actor is allowed to see. A struct rather than a tuple so
    /// it is unambiguously `Sendable` coming back out of `withLock`.
    struct Snapshot: Sendable {
        var level: Double = 0
        var frames: AVAudioFramePosition = 0
        var isPaused = false
        /// True once any buffer has been lost to a conversion or write failure.
        var failed = false
    }

    /// Slack on the converted buffer so a resampler that emits a frame or two
    /// more than the naive ratio predicts never has to be called twice.
    private static let conversionHeadroom: AVAudioFrameCount = 1024

    /// Only ever touched from the render thread, which delivers buffers
    /// serially. `var` because it is rebuilt if the tap starts handing us a
    /// format other than the one the engine advertised at set-up — see
    /// `activeConverter(for:)`.
    private var converter: AVAudioConverter
    private let outputFormat: AVAudioFormat
    private let logger = AppLog.logger(category: "recorder")
    private let writerQueue = DispatchQueue(
        label: "ca.fluxology.memoryline.mac.recorder.writer", qos: .userInitiated)

    /// Touched only on `writerQueue` after construction.
    private var file: AVAudioFile?
    private let shared = OSAllocatedUnfairLock(initialState: Snapshot())

    init(converter: AVAudioConverter, outputFormat: AVAudioFormat, file: AVAudioFile) {
        self.converter = converter
        self.outputFormat = outputFormat
        self.file = file
    }

    // MARK: - Main-actor surface

    func snapshot() -> Snapshot {
        shared.withLock { $0 }
    }

    func setPaused(_ paused: Bool) {
        shared.withLock { snapshot in
            snapshot.isPaused = paused
            if paused { snapshot.level = 0 }
        }
    }

    /// Waits for queued writes to land and closes the file. Async rather than a
    /// blocking `sync` fence because the caller is the main actor and the queue
    /// may be several buffers deep on a slow disk.
    func finish() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            writerQueue.async {
                // Releasing the last reference is the only way to close an
                // AVAudioFile, and closing is what finalises the WAV header.
                self.file = nil
                continuation.resume()
            }
        }
    }

    // MARK: - Render thread

    /// Converts one tap buffer to 16 kHz mono 16-bit PCM and queues it for
    /// writing. Called on AVFAudio's render thread.
    ///
    /// Taking a lock here is not hard-realtime-safe, and neither is the
    /// dispatch below. That is an accepted trade for a capture tap: unlike
    /// playback, a late buffer here costs a meter update, not an audible
    /// glitch, and the alternative — a lock-free ring buffer — is a great deal
    /// of machinery for a single-writer meter value.
    func accept(_ buffer: AVAudioPCMBuffer) {
        guard !shared.withLock({ $0.isPaused }) else { return }
        let inputSampleRate = buffer.format.sampleRate
        guard buffer.frameLength > 0, inputSampleRate > 0 else { return }
        guard let converter = activeConverter(for: buffer.format) else { return }

        let ratio = outputFormat.sampleRate / inputSampleRate
        let capacity = AVAudioFrameCount((Double(buffer.frameLength) * ratio).rounded(.up))
            + Self.conversionHeadroom
        guard let converted = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else {
            noteFailure("could not allocate a conversion buffer")
            return
        }

        // Sample-rate conversion is not frame-for-frame, so the pull-style API
        // is required here; the simple convert(to:from:) form is only valid
        // when the rates already match. One tap buffer is fed per call and the
        // converter is then told the input ran dry, which is what makes
        // .inputRanDry the expected success status rather than an error.
        var delivered = false
        var conversionError: NSError?
        let status = converter.convert(to: converted, error: &conversionError) { _, inputStatus in
            if delivered {
                inputStatus.pointee = .noDataNow
                return nil
            }
            delivered = true
            inputStatus.pointee = .haveData
            return buffer
        }
        guard status == .haveData || status == .inputRanDry else {
            noteFailure(conversionError?.localizedDescription ?? "audio conversion failed")
            return
        }
        guard converted.frameLength > 0 else { return }

        let level = Self.meterLevel(of: converted)
        shared.withLock { $0.level = level }

        // `converted` was allocated in this call and is never referenced here
        // again, so handing it to the writer queue is an exclusive transfer,
        // not shared mutable state. The tap's own buffer, which is only valid
        // for the duration of this callback, is deliberately not captured.
        writerQueue.async {
            guard let file = self.file else { return }
            do {
                try file.write(from: converted)
                self.shared.withLock { $0.frames += AVAudioFramePosition(converted.frameLength) }
            } catch {
                self.noteFailure(error.localizedDescription)
            }
        }
    }

    /// The converter for the format actually being delivered.
    ///
    /// The engine advertises the input format before recording starts, but the
    /// hardware underneath it can change — a device switched out from under a
    /// running engine, or a format the AUHAL only settles on once it is
    /// running. A converter built for the wrong input rate does not resample
    /// wrongly, it fails every buffer, so it is worth the one comparison per
    /// callback to notice and rebuild.
    private func activeConverter(for inputFormat: AVAudioFormat) -> AVAudioConverter? {
        if converter.inputFormat == inputFormat { return converter }
        guard let rebuilt = AVAudioConverter(from: inputFormat, to: outputFormat) else {
            noteFailure("no converter for the input format")
            return nil
        }
        logger.notice("input format changed mid-recording; conversion rebuilt")
        converter = rebuilt
        return rebuilt
    }

    /// Records that audio was lost and logs it exactly once — a failing disk
    /// would otherwise produce a log line per buffer, twelve times a second.
    private func noteFailure(_ description: String) {
        let alreadyReported = shared.withLock { snapshot -> Bool in
            let was = snapshot.failed
            snapshot.failed = true
            return was
        }
        guard !alreadyReported else { return }
        logger.error("audio capture path failed: \(description, privacy: .public)")
    }

    /// RMS of the converted buffer mapped onto 0...1 with a -50 dBFS floor —
    /// the same mapping the iOS recorder applies to `averagePower`, so both
    /// meters read alike. Measured after conversion because the layout there is
    /// known (mono, interleaved, 16-bit) whatever the hardware handed us.
    private static func meterLevel(of buffer: AVAudioPCMBuffer) -> Double {
        guard let samples = buffer.int16ChannelData?.pointee else { return 0 }
        let frameCount = Int(buffer.frameLength)
        guard frameCount > 0 else { return 0 }

        var sumOfSquares = 0.0
        for index in 0..<frameCount {
            let sample = Double(samples[index]) / 32_768.0
            sumOfSquares += sample * sample
        }
        let rms = (sumOfSquares / Double(frameCount)).squareRoot()
        // log10(0) is -infinity, so clamp the input rather than the result.
        let decibels = 20 * log10(max(rms, 0.000_001))
        let floorDecibels = -50.0
        return max(0, min(1, (decibels - floorDecibels) / -floorDecibels))
    }
}
