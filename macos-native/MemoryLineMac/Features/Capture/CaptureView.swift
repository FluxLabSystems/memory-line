import SwiftUI

/// Main-window capture screen: record / stop, pause / resume, discard, a live
/// timer and input meter, and the input-device choice the phone never needs
/// (port plan §4.1).
///
/// Three shape decisions worth explaining:
///
///  - **A grouped `Form`, not the iPhone's hero button.** The iOS screen is one
///    huge round button because it is used one-handed at the wheel. The Mac
///    screen has to carry a device picker and a capture-type default alongside
///    the transport, and a grouped form is how this app already renders that
///    kind of surface (`PairingSettingsView`). The transport still sits in the
///    first section, large and prominent.
///  - **Dependencies arrive through the initializer as `any` existentials over
///    the seam protocols** (`CaptureSeams.swift`), plus the shared
///    `SettingsStore` for the two preferences this screen owns. Nothing here
///    names a concrete recorder, enumerator, or `MacAppEnvironment`, so this
///    file compiles and reviews before any of them exist, and a test or preview
///    can pass a stub. `@Environment(SomeType.self)` would have required a
///    concrete observable type to already exist.
///  - **It never touches the upload pipeline.** `MacAudioRecording.stop()`
///    persists the capture as `.savedLocal`; driving it to the server is
///    `MacUploading`'s job, wired at the composition root. A view that also
///    poked the uploader would give two owners to one transition.
struct CaptureView: View {
    private let recorder: any MacAudioRecording
    private let inputs: any MacAudioDeviceEnumerating
    private let settings: any SettingsStore
    /// Called when the capture-type picker changes, before the new value is
    /// persisted.
    ///
    /// `MacAudioRecording.start(deviceId:)` carries no capture type, and this
    /// view is not allowed to know the concrete recorder, so the stored default
    /// is all it can write on its own — and a recorder that read that default
    /// once at launch would keep filing captures under the old type for the
    /// rest of the session. The composition root, which does know the recorder,
    /// wires this to whatever holds "the type of the next recording". Optional
    /// so the view still works when nothing needs telling.
    private let onCaptureTypeChanged: ((CaptureType) -> Void)?

    @Environment(\.openURL) private var openURL

    @State private var inputSelection: InputSelection = .systemDefault
    @State private var captureType: CaptureType = .memory
    /// Set after we prompt or re-check, so the pane updates even if the
    /// recorder's `authorization` is a cached snapshot rather than an observed
    /// property. Whatever is here is always the more recent read.
    @State private var authorizationOverride: MacMicrophoneAuthorization?
    @State private var isBusy = false
    @State private var actionMessage: ActionMessage?
    @State private var showDiscardConfirmation = false

    init(
        recorder: any MacAudioRecording,
        inputs: any MacAudioDeviceEnumerating,
        settings: any SettingsStore,
        onCaptureTypeChanged: ((CaptureType) -> Void)? = nil
    ) {
        self.recorder = recorder
        self.inputs = inputs
        self.settings = settings
        self.onCaptureTypeChanged = onCaptureTypeChanged
    }

    /// 12 Hz: fast enough that the meter reads as live and the seconds never
    /// look stuck, slow enough to stay invisible in the energy panel.
    private static let readoutRefreshInterval = 1.0 / 12.0

    private struct ActionMessage: Identifiable {
        let id = UUID()
        let text: String
        let isError: Bool
    }

    var body: some View {
        Group {
            if authorization == .denied {
                microphoneDeniedView
            } else {
                captureForm
            }
        }
        .navigationTitle("Capture")
        .toolbar {
            ToolbarItem {
                Button {
                    inputs.refresh()
                } label: {
                    Label("Refresh Inputs", systemImage: "arrow.clockwise")
                }
                .help("Re-read the list of connected audio inputs")
            }
        }
        .task {
            // Read the persisted choices here rather than in `init`: a View's
            // initializer runs on every re-render, and the settings store is a
            // database round trip.
            inputSelection = InputSelection(storedDeviceId: settings.string(MacCaptureSettingsKey.inputDeviceId))
            captureType = CaptureType(rawValue: settings.string(AppSettingsKey.defaultCaptureType) ?? "") ?? .memory
            inputs.startObserving()
        }
        .onDisappear {
            // Same reasoning as LibraryView's sync ticker: do not keep hardware
            // notifications running for a pane nobody is looking at. A recording
            // in progress is unaffected — the recorder holds its own device.
            inputs.stopObserving()
        }
        .confirmationDialog(
            "Discard this recording?",
            isPresented: $showDiscardConfirmation,
            titleVisibility: .visible
        ) {
            Button("Discard Recording", role: .destructive) { discardRecording() }
            Button("Keep Recording", role: .cancel) {}
        } message: {
            Text("The audio recorded so far is deleted and no capture is kept.")
        }
    }

    // MARK: - Form

    private var captureForm: some View {
        Form {
            if let failureMessage {
                Section {
                    Label {
                        Text(failureMessage)
                    } icon: {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                    }
                }
            }

            Section {
                transport
            }

            Section("Input") {
                inputPicker
                    // Switching device mid-recording would mean tearing down the
                    // audio graph under a file that is being written, so the
                    // choice is frozen while a recording is live.
                    .disabled(isActive)
                inputNote
            }

            Section("Capture type") {
                typePicker
                Text("New recordings made on this Mac are filed as this type. It is the same stored default the iPhone companion uses, so both heads agree.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - Transport

    private var transport: some View {
        VStack(spacing: 14) {
            liveReadout

            HStack(spacing: 12) {
                Button {
                    toggleRecording()
                } label: {
                    Label(isActive ? "Stop and Save" : "Record",
                          systemImage: isActive ? "stop.fill" : "record.circle")
                        .frame(minWidth: 110)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .tint(isActive ? Color.red : Color.accentColor)
                .disabled(isBusy)
                .accessibilityLabel(isActive ? "Stop and save recording" : "Start recording")
                .accessibilityHint(recordButtonHint)

                if isActive {
                    Button {
                        if isPaused { recorder.resume() } else { recorder.pause() }
                    } label: {
                        Label(isPaused ? "Resume" : "Pause",
                              systemImage: isPaused ? "play.fill" : "pause.fill")
                    }
                    .controlSize(.large)
                    .disabled(isBusy)
                    .accessibilityLabel(isPaused ? "Resume recording" : "Pause recording")

                    Button(role: .destructive) {
                        showDiscardConfirmation = true
                    } label: {
                        Label("Discard", systemImage: "trash")
                    }
                    .controlSize(.large)
                    .disabled(isBusy)
                    .accessibilityLabel("Discard recording")
                    .accessibilityHint("Deletes the audio recorded so far.")
                }

                if isBusy {
                    ProgressView().controlSize(.small)
                }
            }

            if let actionMessage {
                Text(actionMessage.text)
                    .font(.caption)
                    .foregroundStyle(actionMessage.isError ? .red : .secondary)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 6)
    }

    /// Timer and meter. While a recording is live these are redrawn from a
    /// clock rather than trusting observation: the seam promises values for
    /// `elapsed` and `level`, not that they are stored observable properties —
    /// a recorder that derives `elapsed` from a start date publishes nothing,
    /// and the timer would sit at 00:00. Idle there is nothing to animate, so
    /// no clock runs.
    @ViewBuilder
    private var liveReadout: some View {
        if isActive {
            TimelineView(.periodic(from: .now, by: Self.readoutRefreshInterval)) { _ in
                readout
            }
        } else {
            readout
        }
    }

    private var readout: some View {
        VStack(spacing: 10) {
            // Rendered even when idle, showing 00:00, so starting a recording
            // does not shove the buttons down the pane.
            Text(CaptureElapsedFormat.clock(recorder.elapsed))
                .font(.system(.largeTitle, design: .monospaced).weight(.medium))
                .foregroundStyle(isActive ? .primary : .secondary)
                .accessibilityLabel("Elapsed recording time")
                .accessibilityValue(CaptureElapsedFormat.spoken(recorder.elapsed))
                .accessibilityAddTraits(.updatesFrequently)

            meter

            Text(stateCaption)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    /// Input level. Drawn rather than delegated to `Gauge` so the fill can stay
    /// a plain capsule that matches the companion's meter, and so the whole
    /// thing is one accessibility element with a spoken percentage instead of a
    /// pair of unlabelled shapes.
    private var meter: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(.quaternary)
                Capsule()
                    .fill(isRecording ? Color.red : Color.secondary)
                    .frame(width: max(3, proxy.size.width * CGFloat(meterLevel)))
                    .animation(.linear(duration: 0.08), value: meterLevel)
            }
        }
        .frame(width: 260, height: 6)
        .opacity(isActive ? 1 : 0.4)
        .accessibilityElement()
        .accessibilityLabel("Input level")
        .accessibilityValue("\(Int((meterLevel * 100).rounded())) percent")
        .accessibilityAddTraits(.updatesFrequently)
    }

    // MARK: - Input device

    private var inputPicker: some View {
        Picker("Input device", selection: inputSelectionBinding) {
            // Explicitly selectable, and choosing it deletes the stored id
            // rather than storing a sentinel: the seam treats "absent" and
            // "unknown" identically, so there is only one way to say this.
            Text("System Default").tag(InputSelection.systemDefault)

            ForEach(inputs.devices) { device in
                Text(device.isSystemDefault ? "\(device.name) (system default)" : device.name)
                    .tag(InputSelection.device(id: device.id))
            }

            if let unavailableSelectionId {
                // Keeps the saved choice selectable while its hardware is away.
                // Without a matching tag the picker would render blank and look
                // as though the setting had been lost.
                Text("Saved device (not connected)")
                    .tag(InputSelection.device(id: unavailableSelectionId))
            }
        }
    }

    @ViewBuilder
    private var inputNote: some View {
        if unavailableSelectionId != nil {
            Label {
                Text("The saved input device is not connected, so recordings use the system default. The choice is kept, not cleared — reconnect the device and it is used again.")
            } icon: {
                Image(systemName: "exclamationmark.triangle")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        } else if inputs.devices.isEmpty {
            Text("No audio inputs found. Connect a microphone, then use Refresh Inputs.")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else if isActive {
            Text("The input cannot be changed while a recording is running.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    /// The persisted device id when it is not among the connected inputs.
    ///
    /// An empty list is deliberately not treated as "your device vanished": the
    /// enumerator has not necessarily run yet when the view first appears, and
    /// flashing a warning on every launch would teach the user to ignore it.
    private var unavailableSelectionId: String? {
        guard case .device(let id) = inputSelection else { return nil }
        guard !inputs.devices.isEmpty else { return nil }
        return inputs.devices.contains { $0.id == id } ? nil : id
    }

    /// Writes through to the settings store from the picker's binding rather
    /// than from `onChange`: loading the saved value into `@State` on appear
    /// would otherwise read back as a change and rewrite the row every time
    /// this pane is shown.
    private var inputSelectionBinding: Binding<InputSelection> {
        Binding(
            get: { inputSelection },
            set: { newValue in
                inputSelection = newValue
                settings.set(newValue.storedDeviceId, for: MacCaptureSettingsKey.inputDeviceId)
            })
    }

    /// Picker selection. A dedicated case for the system default keeps that
    /// state expressible as a tag; optional tags leave a picker blank the
    /// moment the selection does not match a row, which is exactly the case
    /// this screen has to handle well.
    private enum InputSelection: Hashable {
        case systemDefault
        case device(id: String)

        init(storedDeviceId: String?) {
            self = storedDeviceId.map { InputSelection.device(id: $0) } ?? .systemDefault
        }

        var storedDeviceId: String? {
            switch self {
            case .systemDefault: return nil
            case .device(let id): return id
            }
        }
    }

    // MARK: - Capture type

    private var typePicker: some View {
        Picker("File new recordings as", selection: captureTypeBinding) {
            ForEach(CaptureType.allCases) { type in
                Label(type.displayName, systemImage: type.symbolName)
                    .tag(type)
            }
        }
    }

    /// Persists to `AppSettingsKey.defaultCaptureType`, the same key the iOS
    /// companion's capture screen writes, so a Mac and a phone signed into one
    /// account describe their captures the same way. `onCaptureTypeChanged`
    /// carries it to the live recorder; see that property for why both are
    /// needed.
    private var captureTypeBinding: Binding<CaptureType> {
        Binding(
            get: { captureType },
            set: { newValue in
                captureType = newValue
                onCaptureTypeChanged?(newValue)
                settings.set(newValue.rawValue, for: AppSettingsKey.defaultCaptureType)
            })
    }

    // MARK: - Microphone permission

    /// The freshest authorization known to this view.
    private var authorization: MacMicrophoneAuthorization {
        authorizationOverride ?? recorder.authorization
    }

    /// Denied is a dead end the app cannot escape on its own, so it replaces
    /// the pane instead of disabling a button and leaving the user to guess —
    /// the same "say what is actually true" treatment the Ask pane gives a
    /// question the PC has not picked up yet.
    private var microphoneDeniedView: some View {
        ContentUnavailableView {
            Label("Microphone access is off", systemImage: "mic.slash")
        } description: {
            Text("macOS only lets an app ask once, so Memory Line cannot prompt again. Turn the microphone on in System Settings ▸ Privacy & Security ▸ Microphone, then quit and reopen Memory Line — a running app does not pick up the change.")
        } actions: {
            Button("Open Privacy Settings") {
                MicrophoneSettingsLink.open(with: openURL)
            }
            .buttonStyle(.borderedProminent)

            Button("Check Again") {
                Task { authorizationOverride = await recorder.requestAuthorization() }
            }
        }
    }

    // MARK: - Actions

    private func toggleRecording() {
        switch recorder.state {
        case .idle, .failed:
            Task { await startRecording() }
        case .recording, .paused:
            Task { await stopRecording() }
        }
    }

    private func startRecording() async {
        actionMessage = nil
        isBusy = true
        defer { isBusy = false }

        var status = authorization
        if status == .undetermined {
            // The record button is the prompt: asking on launch, before the
            // user has shown any intent to record, is the surest way to get a
            // reflexive "Don't Allow" that cannot be undone in-app.
            status = await recorder.requestAuthorization()
            authorizationOverride = status
        }
        // A denial swaps the whole pane for `microphoneDeniedView`, which
        // explains the situation better than a message under the button.
        guard status == .authorized else { return }

        do {
            // The stored id is passed through even when the device is missing:
            // the seam defines an unknown id as "follow the system default", so
            // the recorder owns that fallback and the UI only reports it.
            try await recorder.start(deviceId: inputSelection.storedDeviceId)
        } catch {
            actionMessage = ActionMessage(text: error.localizedDescription, isError: true)
        }
    }

    private func stopRecording() async {
        isBusy = true
        defer { isBusy = false }

        do {
            if let capture = try await recorder.stop() {
                // Deliberately says nothing about uploading: the capture is
                // saved locally, and whether an upload pass exists yet is not
                // this view's business to promise.
                actionMessage = ActionMessage(
                    text: "Saved to this Mac's library — \(CaptureElapsedFormat.clock(capture.durationSeconds)).",
                    isError: false)
            } else {
                actionMessage = ActionMessage(
                    text: "Nothing was recorded, so nothing was saved.",
                    isError: false)
            }
        } catch {
            actionMessage = ActionMessage(text: error.localizedDescription, isError: true)
        }
    }

    private func discardRecording() {
        isBusy = true
        Task {
            await recorder.cancel()
            isBusy = false
            actionMessage = ActionMessage(text: "Recording discarded.", isError: false)
        }
    }

    // MARK: - Derived state

    private var isRecording: Bool { recorder.state == .recording }
    private var isPaused: Bool { recorder.state == .paused }
    private var isActive: Bool { isRecording || isPaused }

    /// Clamped so a conformer that overshoots cannot draw the fill past the
    /// track or speak "137 percent".
    private var meterLevel: Double { min(max(recorder.level, 0), 1) }

    private var failureMessage: String? {
        if case .failed(let message) = recorder.state { return message }
        return nil
    }

    private var stateCaption: String {
        switch recorder.state {
        case .idle: return "Not recording"
        case .recording: return "Recording"
        case .paused: return "Paused"
        case .failed: return "Recording stopped"
        }
    }

    private var recordButtonHint: String {
        if isActive { return "Finalises the file and saves the capture." }
        if authorization == .undetermined { return "macOS asks for microphone access the first time." }
        return "Starts a new voice capture."
    }
}

// MARK: - Shared helpers

/// Elapsed-time formatting shared by the capture window and the menu-bar item,
/// so a recording never reads 1:05 in one place and 65 in the other.
enum CaptureElapsedFormat {
    /// `mm:ss`, growing to `h:mm:ss` only when there are hours to show — a
    /// leading `0:` on every short capture is just noise.
    static func clock(_ interval: TimeInterval) -> String {
        let (hours, minutes, seconds) = components(interval)
        if hours > 0 {
            return String(format: "%d:%02d:%02d", hours, minutes, seconds)
        }
        return String(format: "%02d:%02d", minutes, seconds)
    }

    /// The same duration for VoiceOver, which reads "02:31" as a time of day.
    static func spoken(_ interval: TimeInterval) -> String {
        let (hours, minutes, seconds) = components(interval)
        var parts: [String] = []
        if hours > 0 { parts.append("\(hours) hour\(hours == 1 ? "" : "s")") }
        if minutes > 0 { parts.append("\(minutes) minute\(minutes == 1 ? "" : "s")") }
        parts.append("\(seconds) second\(seconds == 1 ? "" : "s")")
        return parts.joined(separator: " ")
    }

    /// Guarded rather than a bare `Int(_:)` conversion: that traps on a NaN or
    /// infinite Double, and this reads a value from a recorder these views do
    /// not own. A garbage duration should read 00:00, not take the app down.
    private static func components(_ interval: TimeInterval) -> (hours: Int, minutes: Int, seconds: Int) {
        guard interval.isFinite, interval > 0, interval < Double(Int.max) else { return (0, 0, 0) }
        let total = Int(interval.rounded(.down))
        return (total / 3600, (total % 3600) / 60, total % 60)
    }
}

/// Deep link to System Settings ▸ Privacy & Security ▸ Microphone.
///
/// Uses the `openURL` environment action rather than `NSWorkspace` so the
/// capture feature stays pure SwiftUI and remains testable without AppKit.
enum MicrophoneSettingsLink {
    private static let urlString = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"

    static func open(with openURL: OpenURLAction) {
        guard let url = URL(string: urlString) else { return }
        openURL(url)
    }
}
