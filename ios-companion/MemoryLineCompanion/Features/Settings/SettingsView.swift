import SwiftUI
import UIKit

/// Settings: pairing with the self-hosted sync service, capture defaults,
/// sync switches, processing updates from Windows, the transcription
/// placeholder (design §22.4), and About.
///
/// Toggles/pickers mirror the durable `SettingsStore` through local @State and
/// persist immediately on change. Pairing errors render inline in the section
/// footer and are never logged together with the pairing code (design §14.5).
@MainActor
struct SettingsView: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.openURL) private var openURL

    // Pairing form
    @State private var serverURL = ""
    @State private var pairingCode = ""
    @State private var isPairing = false
    @State private var pairingError: String?
    @State private var isUnpairConfirmationPresented = false

    // Durable-setting mirrors (loaded in `loadFromStore`)
    @State private var defaultType: CaptureType = .memory
    @State private var spokenConfirmation = true
    @State private var autoUpload = true
    @State private var syncEnabled = false

    @State private var recordingsBytes: Int64 = 0

    var body: some View {
        NavigationStack {
            Form {
                pairingSection
                captureSection
                syncSection
                processingUpdatesSection
                transcriptionSection
                aboutSection
            }
            .navigationTitle("Settings")
            .confirmationDialog(
                "Unpair this device?",
                isPresented: $isUnpairConfirmationPresented,
                titleVisibility: .visible
            ) {
                Button("Unpair", role: .destructive) {
                    Task {
                        await env.unpair()
                        syncEnabled = false
                        pairingError = nil
                    }
                }
            } message: {
                Text("Recordings stay on this iPhone but stop uploading until you pair again. The server will no longer accept this device's credentials.")
            }
        }
        .onAppear(perform: loadFromStore)
        .onChange(of: defaultType) { _, newValue in
            env.settings.set(newValue.rawValue, for: AppSettingsKey.defaultCaptureType)
        }
        .onChange(of: spokenConfirmation) { _, newValue in
            env.settings.set(newValue, for: AppSettingsKey.spokenConfirmation)
        }
        .onChange(of: autoUpload) { _, newValue in
            env.settings.set(newValue, for: AppSettingsKey.autoUpload)
            env.uploads.autoUploadPreferenceChanged(enabled: newValue)
        }
        .onChange(of: syncEnabled) { _, newValue in
            env.settings.set(newValue, for: AppSettingsKey.syncEnabled)
            if newValue {
                env.uploads.kick()
            } else {
                // Sync off means no automatic uploads either — drop the
                // pending background retry along with it.
                env.uploads.autoUploadPreferenceChanged(enabled: false)
            }
        }
    }

    // MARK: - Pairing

    private var pairingSection: some View {
        Section {
            if env.isPaired {
                LabeledContent("Paired as") {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(env.settings.string(AppSettingsKey.deviceDisplayName) ?? "This iPhone")
                        Text(truncatedDeviceId)
                            .font(.caption.monospaced())
                            .foregroundStyle(.secondary)
                    }
                }
                Button {
                    Task { await env.uploads.syncNow() }
                } label: {
                    HStack {
                        Text("Sync now")
                        Spacer()
                        if env.uploads.isSyncing {
                            ProgressView()
                        }
                    }
                }
                .disabled(env.uploads.isSyncing)
                Button("Unpair from server", role: .destructive) {
                    isUnpairConfirmationPresented = true
                }
            } else {
                TextField("Server URL", text: $serverURL, prompt: Text("http://mac-mini.tailnet:8080"))
                    .keyboardType(.URL)
                    .textContentType(.URL)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                SecureField("Pairing code", text: $pairingCode)
                Button {
                    pair()
                } label: {
                    HStack {
                        Text(isPairing ? "Pairing\u{2026}" : "Pair")
                        Spacer()
                        if isPairing {
                            ProgressView()
                        }
                    }
                }
                .disabled(isPairing
                    || serverURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    || pairingCode.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        } header: {
            Text("Pairing")
        } footer: {
            pairingFooter
        }
    }

    @ViewBuilder
    private var pairingFooter: some View {
        if let pairingError {
            Text(pairingError)
                .foregroundStyle(.red)
        } else if env.isPaired {
            if let syncError = env.uploads.lastSyncError {
                Text(syncError)
                    .foregroundStyle(.red)
            } else {
                Text("Connected to \(env.settings.string(AppSettingsKey.serverURL) ?? "your sync server").")
            }
        } else {
            Text("Find the pairing code in the sync service's startup log or in pairing-code.txt inside its data directory.")
        }
    }

    private var truncatedDeviceId: String {
        guard let id = env.settings.string(AppSettingsKey.deviceId) else { return "" }
        return id.count > 8 ? String(id.prefix(8)) + "\u{2026}" : id
    }

    private func pair() {
        pairingError = nil
        isPairing = true
        let url = serverURL
        let code = pairingCode
        Task {
            do {
                try await env.pair(serverURL: url, pairingCode: code)
                pairingCode = ""
                syncEnabled = true
            } catch let error as SyncAPIError {
                pairingError = error.message
            } catch {
                pairingError = error.localizedDescription
            }
            isPairing = false
        }
    }

    // MARK: - Capture

    private var captureSection: some View {
        Section {
            Picker("Default capture type", selection: $defaultType) {
                ForEach(CaptureType.allCases) { type in
                    Text(type.displayName).tag(type)
                }
            }
            Toggle("Spoken confirmation", isOn: $spokenConfirmation)
        } header: {
            Text("Capture")
        } footer: {
            Text("One-touch captures (widget, Siri, Action Button) use the default type. Spoken confirmation announces \u{201C}Saved\u{201D} after each capture, for eyes-free use on the road.")
        }
    }

    // MARK: - Sync

    private var syncSection: some View {
        Section {
            Toggle("Sync enabled", isOn: $syncEnabled)
            Toggle("Auto-upload new captures", isOn: $autoUpload)
                .disabled(!syncEnabled)
            LabeledContent("Waiting to upload", value: "\(env.uploads.pendingCount)")
        } header: {
            Text("Sync")
        } footer: {
            Text("Recordings always save to this iPhone first. With auto-upload off, captures wait until you tap Sync now.")
        }
    }

    // MARK: - Processing updates (incoming status, design §19 Phase 3)

    /// The incoming half of sync: `capture_status` changes Windows publishes as
    /// a capture moves through transcription, extraction, and review. The Sync
    /// section above covers the outgoing half (uploads).
    private var processingUpdatesSection: some View {
        Section {
            LabeledContent("Last checked") {
                if let lastPulledAt = env.statusSync.lastPulledAt {
                    Text(lastPulledAt, format: .relative(presentation: .named))
                } else {
                    Text("Never")
                }
            }
            .accessibilityLabel(lastCheckedAccessibilityLabel)
            Button {
                Task { await env.statusSync.pullNow() }
            } label: {
                HStack {
                    Text("Check for updates")
                    Spacer()
                    if env.statusSync.isPulling {
                        ProgressView()
                    }
                }
            }
            .disabled(env.statusSync.isPulling || !env.isPaired)
            .accessibilityLabel("Check your PC for processing updates")
            // The alerts themselves are posted by the status pull; this is only
            // the way back to the system switch that governs them.
            if let settingsURL = URL(string: UIApplication.openSettingsURLString) {
                Button("Notification settings") {
                    openURL(settingsURL)
                }
                .accessibilityLabel("Open notification settings for Memory Line in iOS Settings")
            }
        } header: {
            Text("Processing updates")
        } footer: {
            processingUpdatesFooter
        }
    }

    @ViewBuilder
    private var processingUpdatesFooter: some View {
        if let pullError = env.statusSync.lastPullError {
            Text(pullError)
                .foregroundStyle(.red)
        } else {
            Text("Your iPhone checks for transcription and review progress when you open History, pull to refresh, or tap Check for updates, and it can alert you when a capture becomes ready for review. Transcripts and approvals stay on your Windows PC.")
        }
    }

    private var lastCheckedAccessibilityLabel: String {
        guard let lastPulledAt = env.statusSync.lastPulledAt else {
            return "Last checked for updates: never"
        }
        return "Last checked for updates \(lastPulledAt.formatted(date: .abbreviated, time: .shortened))"
    }

    // MARK: - Transcription (placeholder, design §22 decision 4)

    /// Deliberately not built: the toggle would switch on an on-device Whisper
    /// model that does not exist in this app yet, and shipping speech-to-text
    /// without being able to run it is worse than an honest disabled switch.
    /// The footer names what it actually waits on — the model, not the server,
    /// whose assistant endpoints already exist.
    private var transcriptionSection: some View {
        Section {
            Toggle("On-device transcription", isOn: .constant(false))
                .disabled(true)
        } header: {
            Text("Transcription")
        } footer: {
            Text("Not available yet \u{2014} recordings are transcribed on your Windows PC. This waits on an on-device speech-to-text model being built into the app (design \u{00A7}22, decision 4), not on the sync service, whose assistant endpoints already exist.")
        }
    }

    // MARK: - About

    private var aboutSection: some View {
        Section("About") {
            LabeledContent("Version", value: versionLabel)
            LabeledContent("Recordings on this iPhone") {
                Text(recordingsBytes, format: .byteCount(style: .file))
            }
            LabeledContent("Setup guide") {
                Text("ios-companion/README.md")
                    .font(.caption.monospaced())
            }
        }
    }

    private var versionLabel: String {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "\u{2014}"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        return build.map { "\(version) (\($0))" } ?? version
    }

    // MARK: - Store I/O

    private func loadFromStore() {
        serverURL = env.settings.string(AppSettingsKey.serverURL) ?? ""
        defaultType = CaptureType(
            rawValue: env.settings.string(AppSettingsKey.defaultCaptureType) ?? "") ?? .memory
        spokenConfirmation = env.settings.bool(AppSettingsKey.spokenConfirmation, default: true)
        autoUpload = env.settings.bool(AppSettingsKey.autoUpload, default: true)
        syncEnabled = env.settings.bool(AppSettingsKey.syncEnabled, default: false)
        recordingsBytes = Self.recordingsStorageBytes()
    }

    /// Total bytes of everything under the Recordings directory.
    private static func recordingsStorageBytes() -> Int64 {
        guard
            let directory = try? AudioStorage.recordingsDirectory(),
            let enumerator = FileManager.default.enumerator(
                at: directory,
                includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .fileSizeKey])
        else { return 0 }
        var total: Int64 = 0
        for case let fileURL as URL in enumerator {
            let values = try? fileURL.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .fileSizeKey])
            total += Int64(values?.totalFileAllocatedSize ?? values?.fileSize ?? 0)
        }
        return total
    }
}
