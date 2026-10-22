import Foundation
import os

/// The app's single durable database: schema, location, and the one-call opener
/// `AppEnvironment` uses at launch. Captures and settings share this file so
/// backup/restore stays atomic (Interfaces.swift). The file lives in Application
/// Support and is deliberately INCLUDED in iCloud/device backups — recordings
/// metadata is the user's data, not a cache.
enum AppDatabase {
    /// Migration batches for `SQLiteDatabase.migrate`. Append new batches; never
    /// edit shipped ones — `PRAGMA user_version` records how many have run.
    static let migrations: [String] = [
        """
        CREATE TABLE IF NOT EXISTS captures (
            id                   TEXT PRIMARY KEY NOT NULL,
            type                 TEXT NOT NULL,
            state                TEXT NOT NULL,
            captured_at          REAL NOT NULL,
            timezone_id          TEXT NOT NULL,
            local_offset_minutes INTEGER NOT NULL,
            title_hint           TEXT,
            user_note            TEXT,
            file_name            TEXT NOT NULL,
            byte_length          INTEGER NOT NULL DEFAULT 0,
            sha256               TEXT NOT NULL DEFAULT '',
            duration_seconds     REAL NOT NULL DEFAULT 0,
            created_at           REAL NOT NULL,
            upload_attempts      INTEGER NOT NULL DEFAULT 0,
            last_error           TEXT,
            artifact_id          TEXT,
            uploaded_at          REAL
        );
        CREATE INDEX IF NOT EXISTS idx_captures_state ON captures(state);
        CREATE INDEX IF NOT EXISTS idx_captures_created_at ON captures(created_at);
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );
        """
    ]

    /// Application Support/MemoryLine/memoryline.db, creating directories as needed.
    static func defaultURL() throws -> URL {
        let support = try FileManager.default.url(
            for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let directory = support.appendingPathComponent("MemoryLine", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory.appendingPathComponent("memoryline.db", isDirectory: false)
    }

    /// Opens the app database at its default location and applies pending migrations.
    static func open() throws -> SQLiteDatabase {
        try open(at: defaultURL())
    }

    /// Opens a database at an explicit location (tests) and applies pending migrations.
    static func open(at url: URL) throws -> SQLiteDatabase {
        let database = try SQLiteDatabase(path: url.path)
        try database.migrate(migrations)
        return database
    }
}

/// Schema for the `capture_location` side table: the single location snapshot
/// a capture may carry (design §14.4). Applied by `SQLiteCaptureStore.init` as
/// re-runnable DDL rather than appended to `AppDatabase.migrations`, for the
/// reason `CaptureStatusSchema` documents: `PRAGMA user_version` counts that
/// array, and a table added from `init` needs no number both apps must agree
/// on. A side table rather than new `captures` columns also leaves that
/// table's positional column mapping exactly as it was.
enum CaptureLocationSchema {
    /// One row per located capture, keyed by capture ID and removed with the
    /// capture by `SQLiteCaptureStore.delete`. No foreign key: the row must
    /// never block a capture delete, and the table is created on databases
    /// whose `captures` rows predate it.
    static let sql = """
        CREATE TABLE IF NOT EXISTS capture_location (
            capture_id            TEXT PRIMARY KEY NOT NULL,
            latitude              REAL NOT NULL,
            longitude             REAL NOT NULL,
            horizontal_accuracy_m REAL,
            heading_degrees       REAL,
            speed_mps             REAL
        );
        """

    /// Applies the schema. Safe to call on every launch and from every store
    /// instance sharing the connection.
    static func migrate(_ database: SQLiteDatabase) throws {
        try database.exec(sql)
    }
}

/// `CaptureStore` over a shared `SQLiteDatabase`. The `captures` table mirrors
/// `CaptureRecord` one-to-one (snake_case columns, dates as Unix epoch REAL,
/// state/type as their raw-value TEXT); the optional location rides in the
/// `capture_location` side table and is read back with a LEFT JOIN, so a
/// capture without one decodes exactly as before. Thread-safe: all statements
/// run under the database's internal lock.
final class SQLiteCaptureStore: CaptureStore {
    private let database: SQLiteDatabase

    /// False only when the side table could not be created on an otherwise
    /// working database. Captures then keep working without locations: reads
    /// select NULL placeholders instead of joining, and nothing is written.
    private let locationTableReady: Bool

    /// Column order shared by every SELECT so row decoding stays positional and consistent.
    private static let columns = """
        id, type, state, captured_at, timezone_id, local_offset_minutes, title_hint, user_note, \
        file_name, byte_length, sha256, duration_seconds, created_at, upload_attempts, last_error, \
        artifact_id, uploaded_at
        """

    /// Side-table columns appended after `columns` (indices 17–21); NULL when the capture has no location.
    private static let locationColumns = """
        capture_location.latitude, capture_location.longitude, capture_location.horizontal_accuracy_m, \
        capture_location.heading_degrees, capture_location.speed_mps
        """

    /// SQL literal list of upload-pending states, derived from the state machine
    /// so the query can never drift from `CaptureLifecycleState.isUploadPending`.
    /// Raw values are compile-time enum constants, so inlining them is safe.
    private static let pendingStatesSQL = CaptureLifecycleState.allCases
        .filter(\.isUploadPending)
        .map { "'\($0.rawValue)'" }
        .joined(separator: ", ")

    init(database: SQLiteDatabase) {
        self.database = database
        // Idempotent DDL against a database that has already opened and
        // migrated. It cannot reasonably fail there, but this initializer is
        // non-throwing with many callers, so a failure degrades to "no
        // locations" with a log line rather than taking captures down with it.
        do {
            try CaptureLocationSchema.migrate(database)
            locationTableReady = true
        } catch {
            AppLog.persistence.error(
                "capture_location table could not be prepared; captures continue without locations (\(String(describing: error), privacy: .public))")
            locationTableReady = false
        }
    }

    func insert(_ capture: CaptureRecord) throws {
        let sql = """
            INSERT INTO captures (\(Self.columns))
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """
        try database.run(sql, [.text(capture.id)] + Self.nonIdentifierValues(of: capture))
        try writeLocation(of: capture)
    }

    func update(_ capture: CaptureRecord) throws {
        let sql = """
            UPDATE captures SET
                type = ?, state = ?, captured_at = ?, timezone_id = ?, local_offset_minutes = ?,
                title_hint = ?, user_note = ?, file_name = ?, byte_length = ?, sha256 = ?,
                duration_seconds = ?, created_at = ?, upload_attempts = ?, last_error = ?,
                artifact_id = ?, uploaded_at = ?
            WHERE id = ?;
            """
        try database.run(sql, Self.nonIdentifierValues(of: capture) + [.text(capture.id)])
        try writeLocation(of: capture)
    }

    func delete(id: String) throws {
        try database.run("DELETE FROM captures WHERE id = ?;", [.text(id)])
        if locationTableReady {
            try database.run("DELETE FROM capture_location WHERE capture_id = ?;", [.text(id)])
        }
    }

    /// Mirrors `capture.location` into the side table: upsert when present,
    /// delete when nil. Runs after the `captures` write as a second statement;
    /// a crash between the two leaves at worst a capture whose location is
    /// missing or stale, and the snapshot is best-effort by design (§14.4).
    private func writeLocation(of capture: CaptureRecord) throws {
        guard locationTableReady else { return }
        guard let location = capture.location else {
            try database.run("DELETE FROM capture_location WHERE capture_id = ?;", [.text(capture.id)])
            return
        }
        try database.run(
            """
            INSERT INTO capture_location
                (capture_id, latitude, longitude, horizontal_accuracy_m, heading_degrees, speed_mps)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(capture_id) DO UPDATE SET
                latitude = excluded.latitude, longitude = excluded.longitude,
                horizontal_accuracy_m = excluded.horizontal_accuracy_m,
                heading_degrees = excluded.heading_degrees, speed_mps = excluded.speed_mps;
            """,
            [
                .text(capture.id),
                .real(location.latitude),
                .real(location.longitude),
                location.horizontalAccuracyM.map(SQLiteValue.real) ?? .null,
                location.headingDegrees.map(SQLiteValue.real) ?? .null,
                location.speedMps.map(SQLiteValue.real) ?? .null
            ])
    }

    /// `SELECT` prefix every read shares: the capture columns, then the five
    /// location columns — joined from the side table, or NULL placeholders
    /// when the table is unavailable — so decoding stays positional.
    private var selectSQL: String {
        locationTableReady
            ? "SELECT \(Self.columns), \(Self.locationColumns) FROM captures " +
                "LEFT JOIN capture_location ON capture_location.capture_id = captures.id"
            : "SELECT \(Self.columns), NULL, NULL, NULL, NULL, NULL FROM captures"
    }

    func capture(id: String) throws -> CaptureRecord? {
        try database.query(
            "\(selectSQL) WHERE captures.id = ?;",
            [.text(id)],
            Self.record(from:)
        ).first
    }

    func allCaptures() throws -> [CaptureRecord] {
        try database.query(
            "\(selectSQL) ORDER BY captures.created_at DESC, captures.id DESC;",
            [],
            Self.record(from:))
    }

    func pendingUploads() throws -> [CaptureRecord] {
        try database.query(
            """
            \(selectSQL)
            WHERE captures.state IN (\(Self.pendingStatesSQL))
            ORDER BY captures.created_at ASC, captures.id ASC;
            """,
            [],
            Self.record(from:))
    }

    func abandonedRecordings() throws -> [CaptureRecord] {
        try database.query(
            """
            \(selectSQL)
            WHERE captures.state = '\(CaptureLifecycleState.recording.rawValue)'
            ORDER BY captures.created_at ASC, captures.id ASC;
            """,
            [],
            Self.record(from:))
    }

    func pendingUploadCount() throws -> Int {
        Int(try database.scalarInt64(
            "SELECT COUNT(*) FROM captures WHERE state IN (\(Self.pendingStatesSQL));") ?? 0)
    }

    // MARK: - Row mapping

    /// Every column except `id`, in `columns` order. `insert` prepends the id,
    /// `update` appends it (for the WHERE clause).
    private static func nonIdentifierValues(of capture: CaptureRecord) -> [SQLiteValue] {
        [
            .text(capture.type.rawValue),
            .text(capture.state.rawValue),
            .date(capture.capturedAt),
            .text(capture.timezoneId),
            .integer(Int64(capture.localOffsetMinutes)),
            .nullableText(capture.titleHint),
            .nullableText(capture.userNote),
            .text(capture.fileName),
            .integer(capture.byteLength),
            .text(capture.sha256),
            .real(capture.durationSeconds),
            .date(capture.createdAt),
            .integer(Int64(capture.uploadAttempts)),
            .nullableText(capture.lastError),
            .nullableText(capture.artifactId),
            .nullableDate(capture.uploadedAt)
        ]
    }

    /// Decodes a row selected with `selectSQL` (`columns` at 0–16, the
    /// location columns at 17–21). Unknown enum raw values (only possible
    /// after a version downgrade) coerce to safe fallbacks instead of failing
    /// the whole fetch: type → `.unclassified`, state → `.failedRecoverable`
    /// (keeps the capture visible and retryable).
    private static func record(from row: SQLiteRowReader) -> CaptureRecord {
        let location: LocationContext? = row.isNull(17) || row.isNull(18)
            ? nil
            : LocationContext(
                latitude: row.double(17),
                longitude: row.double(18),
                horizontalAccuracyM: row.isNull(19) ? nil : row.double(19),
                headingDegrees: row.isNull(20) ? nil : row.double(20),
                speedMps: row.isNull(21) ? nil : row.double(21))
        return CaptureRecord(
            id: row.text(0),
            type: CaptureType(rawValue: row.text(1)) ?? .unclassified,
            state: CaptureLifecycleState(rawValue: row.text(2)) ?? .failedRecoverable,
            capturedAt: row.date(3),
            timezoneId: row.text(4),
            localOffsetMinutes: row.int(5),
            titleHint: row.optionalText(6),
            userNote: row.optionalText(7),
            fileName: row.text(8),
            byteLength: row.int64(9),
            sha256: row.text(10),
            durationSeconds: row.double(11),
            createdAt: row.date(12),
            uploadAttempts: row.int(13),
            lastError: row.optionalText(14),
            artifactId: row.optionalText(15),
            uploadedAt: row.optionalDate(16),
            location: location
        )
    }
}

/// `SettingsStore` over the `settings(key TEXT PRIMARY KEY, value TEXT)` table
/// in the shared app database. Setting nil deletes the row. The protocol is
/// non-throwing, so failures are logged (keys only, never values) and reads
/// fall back to nil/default — settings are non-critical by design.
final class SQLiteSettingsStore: SettingsStore {
    private let database: SQLiteDatabase
    private let logger = AppLog.persistence

    init(database: SQLiteDatabase) {
        self.database = database
    }

    func string(_ key: String) -> String? {
        do {
            return try database.query(
                "SELECT value FROM settings WHERE key = ?;",
                [.text(key)]
            ) { $0.text(0) }.first
        } catch {
            logger.error("Settings read failed for key \(key, privacy: .public)")
            return nil
        }
    }

    func set(_ value: String?, for key: String) {
        do {
            if let value {
                try database.run(
                    """
                    INSERT INTO settings (key, value) VALUES (?, ?)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """,
                    [.text(key), .text(value)])
            } else {
                try database.run("DELETE FROM settings WHERE key = ?;", [.text(key)])
            }
        } catch {
            logger.error("Settings write failed for key \(key, privacy: .public)")
        }
    }

    func bool(_ key: String, default defaultValue: Bool) -> Bool {
        switch string(key)?.lowercased() {
        case "1", "true": return true
        case "0", "false": return false
        default: return defaultValue
        }
    }

    func set(_ value: Bool, for key: String) {
        set(value ? "1" : "0", for: key)
    }
}
