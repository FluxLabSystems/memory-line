import Foundation
import Observation
import UIKit
import os

/// Composition root for the app: owns the database, stores, sync client,
/// upload coordinator, and recorder, and wires them together exactly once at
/// launch. Injected into the view tree via `.environment(_:)` and read with
/// `@Environment(AppEnvironment.self)`.
///
/// All stored properties are `let` — the graph is immutable after `init`; the
/// observable pieces (`uploads`, `recorder`) publish their own state.
@MainActor
@Observable
final class AppEnvironment {
    let database: SQLiteDatabase
    let captures: SQLiteCaptureStore
    let settings: SQLiteSettingsStore
    let tokens: KeychainTokenStore
    let api: SyncAPIClient
    let uploads: UploadCoordinator
    let recorder: AudioRecorderService
    /// Windows-authored processing status per capture (design §19 Phase 3).
    let statuses: SQLiteCaptureStatusStore
    /// Read-only copy of the Windows archive — events, eras, people, review
    /// queue. Windows is the only writer; this phone renders what it is sent.
    let projections: SQLiteTimelineProjectionStore
    let statusSync: StatusSyncCoordinator

    private let logger = AppLog.logger(category: "environment")

    init() {
        let database = Self.openDatabaseRecoveringFromCorruptWAL()
        let captures = SQLiteCaptureStore(database: database)
        let settings = SQLiteSettingsStore(database: database)
        let tokens = KeychainTokenStore()
        let api = SyncAPIClient(settings: settings, tokens: tokens)
        let uploads = UploadCoordinator(store: captures, settings: settings, tokens: tokens, api: api)
        let statuses = Self.openStatusStore(database: database)
        let projections = Self.openProjectionStore(database: database)
        let statusSync = StatusSyncCoordinator(
            store: captures, settings: settings, api: api,
            statusStore: statuses, projectionStore: projections)
        // The upload pass pulls status when it finishes, so a manual "sync now"
        // also refreshes what Windows has done. Held weakly there; this
        // environment owns both coordinators for the process lifetime.
        uploads.statusSync = statusSync

        self.database = database
        self.captures = captures
        self.settings = settings
        self.tokens = tokens
        self.api = api
        self.uploads = uploads
        self.statuses = statuses
        self.projections = projections
        self.statusSync = statusSync
        // Capture the local `uploads` (not self) so the closure is valid before
        // `self` is fully initialized. Every finalized recording immediately
        // enters the upload pipeline (design §16.1).
        self.recorder = AudioRecorderService(store: captures, settings: settings) { capture in
            uploads.captureFinalized(capture)
        }
    }

    /// True when this device has completed pairing with a sync server.
    /// Backed by the settings store, so it is NOT observation-tracked; views
    /// that change pairing (SettingsView) re-read it via their own state.
    var isPaired: Bool { settings.string(AppSettingsKey.deviceId) != nil }

    /// Registers this device with the sync service using the pairing code,
    /// then persists the identity + tokens and starts uploading anything
    /// already queued. Throws `SyncAPIError` (or `KeychainError`) for the UI
    /// to display; the pairing code itself is never logged (design §14.5).
    func pair(serverURL: String, pairingCode: String) async throws {
        let request = DeviceRegisterRequest(
            pairingCode: pairingCode,
            displayName: UIDevice.current.name,
            appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
        let response = try await api.register(serverURL: serverURL, request: request)

        settings.set(SyncAPIClient.normalizedBaseURL(serverURL) ?? serverURL, for: AppSettingsKey.serverURL)
        settings.set(response.deviceId, for: AppSettingsKey.deviceId)
        settings.set(request.displayName, for: AppSettingsKey.deviceDisplayName)
        do {
            try tokens.save(accessToken: response.accessToken, refreshToken: response.refreshToken)
        } catch {
            // Without tokens the pairing is unusable — roll back the identity
            // so `isPaired` stays false and the user can retry cleanly.
            settings.set(nil, for: AppSettingsKey.deviceId)
            logger.error("pairing aborted: token save failed")
            throw error
        }
        settings.set(true, for: AppSettingsKey.syncEnabled)
        logger.info("paired as device \(response.deviceId, privacy: .public)")
        uploads.kick()
    }

    /// Unpairs from the sync server. Revocation is best-effort (the server may
    /// be unreachable); local credentials are always cleared. Local captures
    /// and recordings are untouched — they simply stop uploading.
    func unpair() async {
        do {
            try await api.revokeDevice()
            logger.info("device revoked on server")
        } catch {
            logger.notice("server revoke failed during unpair; clearing local credentials anyway")
        }
        tokens.clear()
        settings.set(nil, for: AppSettingsKey.deviceId)
        settings.set(false, for: AppSettingsKey.syncEnabled)
    }

    // MARK: - Database bootstrap

    /// Opens the app database, tolerating a corrupt WAL sidecar: if the first
    /// open (or its migrations) fails, the `-wal`/`-shm` sidecars are deleted
    /// and the open is retried once. This is only reached when the WAL is
    /// already unreadable, so discarding it trades the tail of the log for a
    /// working database instead of a permanently bricked app.
    ///
    /// If the retry also fails we `fatalError` deliberately: every durability
    /// guarantee (design §16.1 — a capture is committed locally before the UI
    /// reports success) flows through this database. Limping on without it
    /// would accept recordings whose metadata silently vanishes, which is
    /// strictly worse than a visible crash at launch.
    /// Opens the Phase 3 status projection over the already-open capture
    /// database. Its migration is plain DDL against a database that has just
    /// opened and migrated successfully, so a failure here means that same
    /// database — the one holding every capture — is unusable, which is the
    /// case `openDatabaseRecoveringFromCorruptWAL` already treats as fatal.
    /// Failing loudly here rather than degrading keeps that policy in one
    /// place instead of leaving a half-working app.
    private static func openStatusStore(database: SQLiteDatabase) -> SQLiteCaptureStatusStore {
        do {
            return try SQLiteCaptureStatusStore(database: database)
        } catch {
            fatalError(
                "Memory Line could not prepare its capture-status table on an otherwise healthy "
                + "database: \(error)")
        }
    }

    /// Opens the timeline projection over the same database, with the same
    /// policy as the status store above and for the same reason: its DDL runs
    /// against a database that has already opened and migrated, so a failure
    /// here means that database is unusable.
    private static func openProjectionStore(database: SQLiteDatabase) -> SQLiteTimelineProjectionStore {
        do {
            return try SQLiteTimelineProjectionStore(database: database)
        } catch {
            fatalError(
                "Memory Line could not prepare its timeline tables on an otherwise healthy "
                + "database: \(error)")
        }
    }

    private static func openDatabaseRecoveringFromCorruptWAL() -> SQLiteDatabase {
        let url: URL
        do {
            url = try AppDatabase.defaultURL()
        } catch {
            fatalError("Memory Line cannot create its Application Support directory: \(error)")
        }
        do {
            return try AppDatabase.open(at: url)
        } catch {
            AppLog.persistence.error(
                "database open failed (\(String(describing: error), privacy: .public)); removing WAL sidecars and retrying once")
        }
        try? FileManager.default.removeItem(atPath: url.path + "-wal")
        try? FileManager.default.removeItem(atPath: url.path + "-shm")
        do {
            return try AppDatabase.open(at: url)
        } catch {
            fatalError(
                "Memory Line could not open its capture database even after discarding the WAL sidecar. "
                + "The database file at \(url.path) is unreadable; restore the app from a backup or reinstall. "
                + "Underlying error: \(error)")
        }
    }
}
