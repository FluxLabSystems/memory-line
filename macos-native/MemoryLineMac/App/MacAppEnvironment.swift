import Foundation
import Observation
import os

/// Composition root for the macOS app, mirroring the iOS companion's
/// `AppEnvironment`. The database, stores, and sync client are built once at
/// launch and injected into the view tree with `.environment(_:)`.
///
/// The stores, sync client and Keychain come from the shared sources under
/// `ios-companion/MemoryLineCompanion/Shared/`, which the Xcode project
/// compiles into this target (see macos-native/README.md). The recorder,
/// uploader and status sync are macOS-specific rather than shared, because the
/// iOS versions are built on `AVAudioSession` and `BGTaskScheduler`, neither of
/// which exists here (`docs/design/MACOS-PORT-PLAN.md` §4). Their request
/// sequences and state machines are mirrored; only the scheduling differs.
///
/// Three things the phone does not have to think about, and this does:
///
///  - **A choice of input device.** Macs gain and lose inputs mid-session, so
///    `inputs` observes the hardware rather than reading it once.
///  - **Device display name** comes from the host name rather than
///    `UIDevice.current.name`.
///  - **No background execution.** The app is running or it is not, so the
///    loops are plain tickers started from `start()`.
@MainActor
@Observable
final class MacAppEnvironment {
    let database: SQLiteDatabase
    let captures: SQLiteCaptureStore
    let settings: SQLiteSettingsStore
    let tokens: KeychainTokenStore
    let api: SyncAPIClient
    /// Windows-authored processing status per capture (design §19 Phase 3).
    let statuses: SQLiteCaptureStatusStore
    /// Read-only copy of the Windows archive — events, eras, people, review
    /// queue. Windows is the only writer; this Mac renders what it is sent.
    let projections: SQLiteTimelineProjectionStore
    let sync: MacSyncCoordinator
    let uploads: MacUploadCoordinator
    let recorder: MacAudioRecorderService
    let inputs: MacAudioDeviceEnumerator
    /// The one thing this Mac writes to the change feed: review verdicts.
    let reviews: MacReviewCoordinator
    /// The Ask conversation: questions submitted to the service, answers the
    /// Windows PC sends back. This Mac never answers anything itself. Shared
    /// source (`Shared/Assistant/AskCoordinator.swift`); the phone composes
    /// the same coordinator with surface "ios".
    let ask: AskCoordinator

    private let logger = AppLog.logger(category: "environment")

    /// The stores whose initializers apply DDL are built by `bootstrap()` and
    /// passed in, because they throw and this does not: a composition root that
    /// could fail halfway through wiring would leave a half-built object graph
    /// with no sensible thing to do about it.
    init(
        database: SQLiteDatabase,
        settings: SQLiteSettingsStore,
        statuses: SQLiteCaptureStatusStore,
        projections: SQLiteTimelineProjectionStore,
        decisions: SQLiteReviewDecisionStore
    ) {
        let captures = SQLiteCaptureStore(database: database)
        let tokens = KeychainTokenStore()
        let api = SyncAPIClient(settings: settings, tokens: tokens)
        let uploads = MacUploadCoordinator(store: captures, settings: settings, api: api)

        self.database = database
        self.captures = captures
        self.settings = settings
        self.tokens = tokens
        self.api = api
        self.statuses = statuses
        self.projections = projections
        self.sync = MacSyncCoordinator(
            settings: settings, api: api, statusStore: statuses, projectionStore: projections)
        self.uploads = uploads
        self.reviews = MacReviewCoordinator(
            settings: settings, api: api, store: decisions, projections: projections)
        self.ask = AskCoordinator(settings: settings, api: api, surface: "mac")
        self.inputs = MacAudioDeviceEnumerator()
        // Capture the local `uploads` rather than self: the closure has to be
        // valid before `self` is fully initialized. Every finalized recording
        // enters the upload pipeline immediately instead of waiting for the
        // next drain tick — the same coupling the iOS companion makes.
        self.recorder = MacAudioRecorderService(store: captures, settings: settings) { capture in
            uploads.captureFinalized(capture)
        }
    }

    /// Starts the background loops and repairs anything a previous run left
    /// half-finished. Called once from the app's first window, not from `init`:
    /// a composition root that kicked off network and disk work while building
    /// the object graph would be untestable and would fire before the UI can
    /// show what it is doing.
    func start() async {
        // A recording row is written before audio capture begins, so a crash
        // mid-recording leaves a row in `.recording` that nothing will ever
        // finish. Repair those before anything reads the library.
        await recorder.recoverAbandonedRecordings()

        reviews.reload()

        guard canSync else { return }
        sync.startPeriodicSync()
        uploads.startPeriodicDrain()
        reviews.startPeriodicDrain()
    }

    /// True once this Mac is paired and sync is enabled — the precondition for
    /// both background loops.
    private var canSync: Bool { sync.canSync }

    /// Starts a recording when the recorder is idle (or stopped by an error)
    /// and finalises the active one otherwise — the single transport action
    /// the File ▸ New Recording command, its ⌘N shortcut and a
    /// `memoryline://record` link all share, so three entry points cannot
    /// drift into three behaviours. The input device is the one the capture
    /// window persisted; the recorder treats an absent or unplugged one as
    /// "follow the system default".
    ///
    /// Failures are logged rather than shown: a menu command and a URL have
    /// no pane of their own. The capture window and the menu-bar item remain
    /// the surfaces that explain a denied microphone or a start that failed,
    /// and both read the recorder's state directly.
    func toggleRecording() async {
        switch recorder.state {
        case .idle, .failed:
            do {
                try await recorder.start(deviceId: settings.string(MacCaptureSettingsKey.inputDeviceId))
            } catch {
                logger.error("recording could not start from a command: \(error.localizedDescription, privacy: .public)")
            }
        case .recording, .paused:
            do {
                _ = try await recorder.stop()
            } catch {
                logger.error("recording could not be saved from a command: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// True when this Mac has completed pairing with a sync server.
    /// Backed by the settings store, so it is NOT observation-tracked; views
    /// that change pairing re-read it via their own state.
    var isPaired: Bool { settings.string(AppSettingsKey.deviceId) != nil }

    var serverURL: String? { settings.string(AppSettingsKey.serverURL) }

    /// Registers this Mac with the sync service using a pairing code, then
    /// persists the identity and tokens. Throws `SyncAPIError` (or
    /// `KeychainError`) for the UI to display; the pairing code is never logged
    /// (design §14.5).
    func pair(serverURL: String, pairingCode: String) async throws {
        // `platform` MUST be set explicitly: DeviceRegisterRequest defaults it to
        // "ios" for the companion, and the memberwise initializer would happily
        // register this Mac as an iPhone.
        //
        // "other", not "macos": the wire contract's enum is [ios, windows, other]
        // (shared-contracts/openapi/memory-line-sync-v1.yaml) and the server
        // rejects anything else outright (DeviceService.AllowedPlatforms). Adding
        // a first-class "macos" value is a cross-cutting change tracked in
        // docs/design/MACOS-PORT-PLAN.md §7.
        let request = DeviceRegisterRequest(
            pairingCode: pairingCode,
            platform: "other",
            displayName: Self.deviceDisplayName,
            appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
        let response = try await api.register(serverURL: serverURL, request: request)

        settings.set(SyncAPIClient.normalizedBaseURL(serverURL) ?? serverURL, for: AppSettingsKey.serverURL)
        settings.set(response.deviceId, for: AppSettingsKey.deviceId)
        settings.set(request.displayName, for: AppSettingsKey.deviceDisplayName)
        do {
            try tokens.save(accessToken: response.accessToken, refreshToken: response.refreshToken)
        } catch {
            // Without tokens the pairing is unusable — roll back the identity so
            // `isPaired` stays false and the user can retry cleanly.
            settings.set(nil, for: AppSettingsKey.deviceId)
            logger.error("pairing aborted: token save failed")
            throw error
        }
        settings.set(true, for: AppSettingsKey.syncEnabled)
        logger.info("paired as device \(response.deviceId, privacy: .public)")
        // Pull immediately so the Library fills in without waiting for the
        // first tick, then keep both loops running. Anything recorded while
        // unpaired is still sitting in the store, so starting the drain here
        // is what finally sends it.
        sync.startPeriodicSync()
        uploads.startPeriodicDrain()
        reviews.startPeriodicDrain()
    }

    /// Unpairs from the sync server. Revocation is best-effort (the server may
    /// be unreachable); local credentials are always cleared.
    func unpair() async {
        // Stop both loops first: a pull or an upload racing the credential
        // clear would fail with a 401 and surface a misleading "no longer
        // paired" error for something the user just did deliberately.
        sync.stopPeriodicSync()
        uploads.stopPeriodicDrain()
        reviews.stopPeriodicDrain()
        do {
            try await api.revokeDevice()
            logger.info("device revoked on server")
        } catch {
            logger.notice("server revoke failed during unpair; clearing local credentials anyway")
        }
        tokens.clear()
        settings.set(nil, for: AppSettingsKey.deviceId)
        settings.set(false, for: AppSettingsKey.syncEnabled)

        // The timeline projection is a copy of someone's archive that arrived
        // over the wire; an unpaired Mac has no business keeping it on disk
        // (design §14). Dropping it and forgetting the cursor are one act: the
        // cursor names a position in the *server's* change log, so keeping it
        // after a re-pair would resume mid-feed and leave the freshly emptied
        // projection with no backlog to refill from. Doing either alone is a
        // bug — either a timeline that never fills in, or one that outlives the
        // pairing that justified it.
        sync.resetCursor()
        // Queued verdicts go with it: they address pending events that are about
        // to be deleted, and their client sequences belong to a device
        // registration that no longer exists.
        reviews.discardAll()
        // So does the Ask conversation: its session belongs to that same
        // registration, and its turns are the archive's words.
        ask.reset()
        do {
            try projections.deleteAll()
        } catch {
            // Credentials are already gone, so the device is unpaired whatever
            // happens here. Say so rather than failing the unpair over stale
            // read-only rows the next pull would overwrite anyway.
            logger.error("could not clear the timeline projection on unpair: \(String(describing: error), privacy: .public)")
        }
    }

    /// Name this Mac presents to the sync service when pairing. The local host
    /// name is the closest macOS analogue to `UIDevice.current.name` and is what
    /// the user sees in Windows Settings → Sync.
    ///
    /// `nonisolated` because it reads only `ProcessInfo` — nothing main-actor
    /// about it. Without this it inherits the type's `@MainActor` isolation and
    /// cannot be read from a nonisolated context such as a plain XCTest method.
    nonisolated static var deviceDisplayName: String {
        let host = ProcessInfo.processInfo.hostName
        // hostName is often the Bonjour form ("studio.local"); trim the suffix.
        return host.hasSuffix(".local") ? String(host.dropLast(6)) : host
    }

    // MARK: - Bootstrap

    /// Opens the app database, tolerating a corrupt WAL sidecar, exactly as the
    /// iOS companion does: if the first open (or its migrations) fails, the
    /// `-wal`/`-shm` sidecars are deleted and the open is retried once.
    ///
    /// If the retry also fails this traps deliberately — every durability
    /// guarantee flows through this database, and limping on without it would
    /// accept work whose metadata silently vanishes.
    static func bootstrap() -> MacAppEnvironment {
        let url: URL
        do {
            url = try AppDatabase.defaultURL()
        } catch {
            fatalError("Memory Line cannot create its Application Support directory: \(error)")
        }

        let database: SQLiteDatabase
        do {
            database = try AppDatabase.open(at: url)
        } catch {
            AppLog.persistence.error(
                "database open failed (\(String(describing: error), privacy: .public)); removing WAL sidecars and retrying once")
            try? FileManager.default.removeItem(atPath: url.path + "-wal")
            try? FileManager.default.removeItem(atPath: url.path + "-shm")
            do {
                database = try AppDatabase.open(at: url)
            } catch {
                fatalError(
                    "Memory Line could not open its capture database even after discarding the WAL sidecar. "
                    + "The database file at \(url.path) is unreadable; restore from a backup or reinstall. "
                    + "Underlying error: \(error)")
            }
        }

        let settings = SQLiteSettingsStore(database: database)

        let statuses: SQLiteCaptureStatusStore
        do {
            statuses = try SQLiteCaptureStatusStore(database: database)
        } catch {
            fatalError(
                "Memory Line could not prepare its capture-status table on an otherwise healthy "
                + "database: \(error)")
        }

        let projections: SQLiteTimelineProjectionStore
        do {
            projections = try SQLiteTimelineProjectionStore(database: database)
        } catch {
            fatalError(
                "Memory Line could not prepare its timeline tables on an otherwise healthy "
                + "database: \(error)")
        }

        let decisions: SQLiteReviewDecisionStore
        do {
            decisions = try SQLiteReviewDecisionStore(database: database, settings: settings)
        } catch {
            fatalError(
                "Memory Line could not prepare its review-decision queue on an otherwise healthy "
                + "database: \(error)")
        }

        return MacAppEnvironment(
            database: database,
            settings: settings,
            statuses: statuses,
            projections: projections,
            decisions: decisions)
    }
}
