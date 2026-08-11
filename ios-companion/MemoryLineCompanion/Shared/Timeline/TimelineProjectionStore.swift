import Foundation

/// Durable storage for the Windows-authored timeline projection: events, eras,
/// people and the pending-event review queue (design §19; macOS port plan §5,
/// phase 3).
///
/// The stored shape *is* the wire shape — the methods take and return the
/// payload types from `TimelineProjectionModels.swift` rather than a parallel
/// set of record structs. That is a deliberate departure from
/// `CaptureStatusStore`, which keeps `CaptureStatusRecord` beside
/// `CaptureStatusChangePayload`. A record type earns its place when it holds an
/// invariant the wire does not: a different lifetime, a local-only field, a
/// state machine. These projections have none of that. Windows is the only
/// writer, nothing here is edited locally, and every field is stored exactly as
/// it arrives, so a record type would be a rename with no invariant of its own —
/// two structs to keep in step with one C# class instead of one.
///
/// Implementations are thread-safe and calls are synchronous and cheap, matching
/// `CaptureStore` and `CaptureStatusStore`.
protocol TimelineProjectionStore: AnyObject, Sendable {
    // MARK: Events

    /// Inserts or replaces the event with `event.eventId`.
    func upsertEvent(_ event: EventProjectionPayload) throws
    func deleteEvent(eventId: String) throws
    func event(eventId: String) throws -> EventProjectionPayload?
    /// Events whose span intersects `from...to`, oldest first. See the
    /// implementation for what "intersects" means and why.
    func events(from: Date, to: Date) throws -> [EventProjectionPayload]

    // MARK: Eras

    func upsertEra(_ era: EraProjectionPayload) throws
    func deleteEra(eraId: String) throws
    func era(eraId: String) throws -> EraProjectionPayload?
    /// Every era in publisher order. Eras are few and are drawn as timeline
    /// backgrounds, so there is no windowed variant: the UI needs all of them to
    /// draw any of them.
    func allEras() throws -> [EraProjectionPayload]

    // MARK: People

    func upsertPerson(_ person: PersonProjectionPayload) throws
    func deletePerson(personId: String) throws
    func person(personId: String) throws -> PersonProjectionPayload?
    /// Every stored person, by name. Includes merge tombstones (see
    /// `PersonProjectionPayload.mergedIntoId`) — filtering them here would break
    /// the other half of this table's job, which is resolving the person ids an
    /// event carries, and those deliberately keep pointing at merged-away ids.
    /// A hub that lists people filters `mergedIntoId == nil` itself.
    func allPeople() throws -> [PersonProjectionPayload]

    // MARK: Pending events

    func upsertPendingEvent(_ pendingEvent: PendingEventProjectionPayload) throws
    func deletePendingEvent(pendingEventId: String) throws
    func pendingEvent(pendingEventId: String) throws -> PendingEventProjectionPayload?
    /// The review queue, most recently published first. Confidence-based triage
    /// is a client-side sort over this list: the queue is bounded by how much
    /// Windows has extracted and not yet had reviewed, so it does not need a
    /// second index to reorder.
    func allPendingEvents() throws -> [PendingEventProjectionPayload]

    // MARK: Lifecycle

    /// Drops the entire projection. This is a copy of someone's archive that
    /// arrived over the wire; when the Mac unpairs it should not stay on disk
    /// (design §14). Also the reset path if the feed ever has to be re-pulled
    /// from cursor 0 — nothing here is authoritative, so discarding it costs
    /// only a re-pull.
    func deleteAll() throws
}

/// Schema for the timeline projection tables.
///
/// The batch is re-runnable (`CREATE TABLE IF NOT EXISTS`) and is applied by
/// `SQLiteTimelineProjectionStore.init` rather than appended to
/// `AppDatabase.migrations`, following `CaptureStatusSchema` for exactly the
/// reason documented there: that array is the seam `SQLiteCaptureStore.swift`
/// owns and `PRAGMA user_version` counts its entries, so bumping the version
/// from a second array would make the next `AppDatabase.open` reject the file as
/// "newer than this app supports".
///
/// This file was macOS-only when it was written, and the rule then read as "do
/// not advance `user_version` on a database the phone also opens with a shorter
/// migration array, or the phone bricks on next launch". Now that both apps
/// compile it the hazard is gone — but the rule stays, and for a better reason:
/// `user_version` counts one array, and a schema applied from `init` can be
/// added to either app at any time without either having to agree about a number.
/// That is exactly the property that let this move from one target to two without
/// a migration.
///
/// Table names are prefixed `timeline_` because this schema shares a database
/// file with `captures`, `settings` and `capture_status`. An unprefixed `events`
/// table would be a landmine the day that code wants the name.
enum TimelineProjectionSchema {
    /// No foreign keys between these tables, and none to `captures`. Pages
    /// arrive in cursor order, so an event legitimately precedes the people and
    /// the era it references, and a pending event can outlive the capture it was
    /// extracted from (deleted locally, or never present on this device at all). A
    /// constraint would reject rows the feed is entitled to send in that order;
    /// the dangling reference is a property of the feed, not a corruption of the
    /// store, and the UI resolves it or shows the id.
    static let sql = """
        CREATE TABLE IF NOT EXISTS timeline_event (
            event_id          TEXT PRIMARY KEY NOT NULL,
            title             TEXT NOT NULL,
            description       TEXT,
            start_date        REAL NOT NULL,
            end_date          REAL,
            date_precision    TEXT NOT NULL DEFAULT 'day',
            display_date      TEXT,
            earliest_possible REAL,
            latest_possible   REAL,
            category          TEXT,
            era_id            TEXT,
            tags_json         TEXT NOT NULL DEFAULT '[]',
            person_ids_json   TEXT NOT NULL DEFAULT '[]',
            locations_json    TEXT NOT NULL DEFAULT '[]',
            latitude          REAL,
            longitude         REAL,
            media_count       INTEGER NOT NULL DEFAULT 0,
            updated_at        REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_timeline_event_start_date ON timeline_event(start_date);
        CREATE INDEX IF NOT EXISTS idx_timeline_event_era_id ON timeline_event(era_id);
        CREATE TABLE IF NOT EXISTS timeline_era (
            era_id        TEXT PRIMARY KEY NOT NULL,
            name          TEXT NOT NULL,
            subtitle      TEXT,
            start_date    REAL NOT NULL,
            end_date      REAL,
            color_code    TEXT NOT NULL DEFAULT '#808080',
            category_id   TEXT,
            category_name TEXT,
            display_order INTEGER NOT NULL DEFAULT 0,
            updated_at    REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_timeline_era_start_date ON timeline_era(start_date);
        CREATE TABLE IF NOT EXISTS timeline_person (
            person_id      TEXT PRIMARY KEY NOT NULL,
            name           TEXT NOT NULL,
            nickname       TEXT,
            relationship   TEXT,
            merged_into_id TEXT,
            is_favorite    INTEGER NOT NULL DEFAULT 0,
            event_count    INTEGER NOT NULL DEFAULT 0,
            updated_at     REAL NOT NULL
        );
        CREATE TABLE IF NOT EXISTS timeline_pending_event (
            pending_event_id   TEXT PRIMARY KEY NOT NULL,
            capture_id         TEXT,
            title              TEXT NOT NULL,
            description        TEXT,
            start_date         REAL NOT NULL,
            end_date           REAL,
            date_precision     TEXT NOT NULL DEFAULT 'day',
            display_date       TEXT,
            category           TEXT,
            confidence_score   REAL NOT NULL DEFAULT 0,
            tags_json          TEXT NOT NULL DEFAULT '[]',
            people_names_json  TEXT NOT NULL DEFAULT '[]',
            transcript_preview TEXT,
            updated_at         REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_timeline_pending_event_updated_at ON timeline_pending_event(updated_at);
        """

    /// Applies the schema. Safe to call on every launch and from every store
    /// instance sharing the connection.
    static func migrate(_ database: SQLiteDatabase) throws {
        try database.exec(sql)
    }
}

/// `TimelineProjectionStore` over the shared `SQLiteDatabase`. Each table mirrors
/// its payload one-to-one (snake_case columns, dates as Unix epoch REAL, booleans
/// as INTEGER 0/1), with one shape decision worth defending.
///
/// **Why `tags`, `personIds`, `locations` and `peopleNames` are JSON text columns
/// and not junction tables.**
///
/// They arrive inline because the contract denormalises them on purpose: a
/// consumer of a read-only projection draws a bubble rather than rebuilding a
/// relational graph, and syncing junction rows would multiply the change volume
/// to reproduce a join the consumer performs anyway. Junction tables here would
/// re-normalise data that arrived flat and then join it straight back on every
/// read. Three reasons beyond "it arrived that way":
///
///  1. **There is no independent write.** Windows republishes a whole event and
///     the local write is a full replace, so one upsert would become a DELETE
///     plus N INSERTs across three child tables — and it would have to be
///     atomic. `SQLiteDatabase` exposes no transaction on its public surface;
///     each statement takes the connection lock separately, so a concurrent
///     reader could observe an event with half its tags. One row, one statement,
///     one lock acquisition is not a shortcut here, it is the only shape that is
///     correct with the wrapper this app has.
///  2. **Nothing queries by tag.** What junction tables buy is an indexed
///     "events with person P" — a query the timeline does not make. The person
///     projection carries `eventCount` precisely so the People hub can sort
///     without touching the event table at all.
///  3. **The whole projection is a cache.** It is rebuilt by re-pulling the feed,
///     so this schema carries no migration debt. If a real by-tag query does
///     arrive, the honest answer is a derived index — a junction table
///     *maintained from* these columns, or `json_each`, which the system SQLite
///     provides — built beside the authoritative column rather than replacing
///     what is authoritative.
///
/// The cost, stated plainly: those four fields are opaque to SQL. Filtering by
/// tag today means a scan, and no constraint stops `personIds` naming a person
/// the feed has not delivered yet.
///
/// Thread-safe: every statement runs under the database's internal lock.
final class SQLiteTimelineProjectionStore: TimelineProjectionStore {
    private let database: SQLiteDatabase

    /// Column order shared by every event SELECT so row decoding stays positional.
    private static let eventColumns = """
        event_id, title, description, start_date, end_date, date_precision, display_date, \
        earliest_possible, latest_possible, category, era_id, tags_json, person_ids_json, \
        locations_json, latitude, longitude, media_count, updated_at
        """

    private static let eraColumns = """
        era_id, name, subtitle, start_date, end_date, color_code, category_id, category_name, \
        display_order, updated_at
        """

    private static let personColumns = """
        person_id, name, nickname, relationship, merged_into_id, is_favorite, event_count, updated_at
        """

    private static let pendingEventColumns = """
        pending_event_id, capture_id, title, description, start_date, end_date, date_precision, \
        display_date, category, confidence_score, tags_json, people_names_json, \
        transcript_preview, updated_at
        """

    /// Opens the store, applying `TimelineProjectionSchema` first so the tables
    /// exist before any statement runs.
    init(database: SQLiteDatabase) throws {
        self.database = database
        try TimelineProjectionSchema.migrate(database)
    }

    // MARK: - Events

    func upsertEvent(_ event: EventProjectionPayload) throws {
        let sql = """
            INSERT INTO timeline_event (\(Self.eventColumns))
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(event_id) DO UPDATE SET
                title = excluded.title,
                description = excluded.description,
                start_date = excluded.start_date,
                end_date = excluded.end_date,
                date_precision = excluded.date_precision,
                display_date = excluded.display_date,
                earliest_possible = excluded.earliest_possible,
                latest_possible = excluded.latest_possible,
                category = excluded.category,
                era_id = excluded.era_id,
                tags_json = excluded.tags_json,
                person_ids_json = excluded.person_ids_json,
                locations_json = excluded.locations_json,
                latitude = excluded.latitude,
                longitude = excluded.longitude,
                media_count = excluded.media_count,
                updated_at = excluded.updated_at;
            """
        try database.run(sql, [
            .text(event.eventId),
            .text(event.title),
            .nullableText(event.description),
            .date(event.startDate),
            .nullableDate(event.endDate),
            .text(event.datePrecision),
            .nullableText(event.displayDate),
            .nullableDate(event.earliestPossible),
            .nullableDate(event.latestPossible),
            .nullableText(event.category),
            .nullableText(event.eraId),
            .text(Self.encodeList(event.tags)),
            .text(Self.encodeList(event.personIds)),
            .text(Self.encodeList(event.locations)),
            Self.nullableReal(event.latitude),
            Self.nullableReal(event.longitude),
            .integer(Int64(event.mediaCount)),
            .date(event.updatedAtUtc)
        ])
    }

    func deleteEvent(eventId: String) throws {
        try database.run("DELETE FROM timeline_event WHERE event_id = ?;", [.text(eventId)])
    }

    func event(eventId: String) throws -> EventProjectionPayload? {
        try database.query(
            "SELECT \(Self.eventColumns) FROM timeline_event WHERE event_id = ?;",
            [.text(eventId)],
            Self.decodeEvent(from:)
        ).first
    }

    /// Events whose span **intersects** the window, not merely those that start
    /// inside it: an event that ran from 1998 to 2001 is part of what the user
    /// sees when they scroll to 1999, and a start-only test would make it vanish
    /// there and reappear at its left edge. An event with no `endDate` is a
    /// point, so it counts as its own end (`COALESCE`).
    ///
    /// Both bounds are inclusive. Windows onto a timeline are drawn, not tiled,
    /// so a memory on the boundary appearing in two different views is invisible
    /// to the user, while one dropping out of both is a bug report.
    ///
    /// Ordered by start date with the id as a stable tiebreak, so two events on
    /// the same day do not swap places between fetches. The `start_date` index
    /// serves the upper bound; the `COALESCE` half is a scan of what remains,
    /// which is the right trade at projection scale and the thing to revisit
    /// first if it ever is not.
    func events(from: Date, to: Date) throws -> [EventProjectionPayload] {
        try database.query(
            """
            SELECT \(Self.eventColumns) FROM timeline_event
            WHERE start_date <= ? AND COALESCE(end_date, start_date) >= ?
            ORDER BY start_date ASC, event_id ASC;
            """,
            [.date(to), .date(from)],
            Self.decodeEvent(from:))
    }

    // MARK: - Eras

    func upsertEra(_ era: EraProjectionPayload) throws {
        let sql = """
            INSERT INTO timeline_era (\(Self.eraColumns))
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(era_id) DO UPDATE SET
                name = excluded.name,
                subtitle = excluded.subtitle,
                start_date = excluded.start_date,
                end_date = excluded.end_date,
                color_code = excluded.color_code,
                category_id = excluded.category_id,
                category_name = excluded.category_name,
                display_order = excluded.display_order,
                updated_at = excluded.updated_at;
            """
        try database.run(sql, [
            .text(era.eraId),
            .text(era.name),
            .nullableText(era.subtitle),
            .date(era.startDate),
            .nullableDate(era.endDate),
            .text(era.colorCode),
            .nullableText(era.categoryId),
            .nullableText(era.categoryName),
            .integer(Int64(era.displayOrder)),
            .date(era.updatedAtUtc)
        ])
    }

    func deleteEra(eraId: String) throws {
        try database.run("DELETE FROM timeline_era WHERE era_id = ?;", [.text(eraId)])
    }

    func era(eraId: String) throws -> EraProjectionPayload? {
        try database.query(
            "SELECT \(Self.eraColumns) FROM timeline_era WHERE era_id = ?;",
            [.text(eraId)],
            Self.decodeEra(from:)
        ).first
    }

    /// `displayOrder` first, because it is the order the user arranged on
    /// Windows; start date and id only break ties, so the list is stable when
    /// every era shares the default order of 0.
    func allEras() throws -> [EraProjectionPayload] {
        try database.query(
            """
            SELECT \(Self.eraColumns) FROM timeline_era
            ORDER BY display_order ASC, start_date ASC, era_id ASC;
            """,
            [],
            Self.decodeEra(from:))
    }

    // MARK: - People

    func upsertPerson(_ person: PersonProjectionPayload) throws {
        let sql = """
            INSERT INTO timeline_person (\(Self.personColumns))
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(person_id) DO UPDATE SET
                name = excluded.name,
                nickname = excluded.nickname,
                relationship = excluded.relationship,
                merged_into_id = excluded.merged_into_id,
                is_favorite = excluded.is_favorite,
                event_count = excluded.event_count,
                updated_at = excluded.updated_at;
            """
        try database.run(sql, [
            .text(person.personId),
            .text(person.name),
            .nullableText(person.nickname),
            .nullableText(person.relationship),
            .nullableText(person.mergedIntoId),
            .integer(person.isFavorite ? 1 : 0),
            .integer(Int64(person.eventCount)),
            .date(person.updatedAtUtc)
        ])
    }

    func deletePerson(personId: String) throws {
        try database.run("DELETE FROM timeline_person WHERE person_id = ?;", [.text(personId)])
    }

    func person(personId: String) throws -> PersonProjectionPayload? {
        try database.query(
            "SELECT \(Self.personColumns) FROM timeline_person WHERE person_id = ?;",
            [.text(personId)],
            Self.decodePerson(from:)
        ).first
    }

    /// `COLLATE NOCASE` so "alice" and "Alice" sort together. It is ASCII-only
    /// folding, which is all SQLite offers without a custom collation; a hub that
    /// needs locale-correct ordering re-sorts in Swift, and this at least stops
    /// the list looking randomly shuffled by capitalisation.
    func allPeople() throws -> [PersonProjectionPayload] {
        try database.query(
            """
            SELECT \(Self.personColumns) FROM timeline_person
            ORDER BY name COLLATE NOCASE ASC, person_id ASC;
            """,
            [],
            Self.decodePerson(from:))
    }

    // MARK: - Pending events

    func upsertPendingEvent(_ pendingEvent: PendingEventProjectionPayload) throws {
        let sql = """
            INSERT INTO timeline_pending_event (\(Self.pendingEventColumns))
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(pending_event_id) DO UPDATE SET
                capture_id = excluded.capture_id,
                title = excluded.title,
                description = excluded.description,
                start_date = excluded.start_date,
                end_date = excluded.end_date,
                date_precision = excluded.date_precision,
                display_date = excluded.display_date,
                category = excluded.category,
                confidence_score = excluded.confidence_score,
                tags_json = excluded.tags_json,
                people_names_json = excluded.people_names_json,
                transcript_preview = excluded.transcript_preview,
                updated_at = excluded.updated_at;
            """
        try database.run(sql, [
            .text(pendingEvent.pendingEventId),
            .nullableText(pendingEvent.captureId),
            .text(pendingEvent.title),
            .nullableText(pendingEvent.description),
            .date(pendingEvent.startDate),
            .nullableDate(pendingEvent.endDate),
            .text(pendingEvent.datePrecision),
            .nullableText(pendingEvent.displayDate),
            .nullableText(pendingEvent.category),
            .real(pendingEvent.confidenceScore),
            .text(Self.encodeList(pendingEvent.tags)),
            .text(Self.encodeList(pendingEvent.peopleNames)),
            .nullableText(pendingEvent.transcriptPreview),
            .date(pendingEvent.updatedAtUtc)
        ])
    }

    func deletePendingEvent(pendingEventId: String) throws {
        try database.run(
            "DELETE FROM timeline_pending_event WHERE pending_event_id = ?;",
            [.text(pendingEventId)])
    }

    func pendingEvent(pendingEventId: String) throws -> PendingEventProjectionPayload? {
        try database.query(
            "SELECT \(Self.pendingEventColumns) FROM timeline_pending_event WHERE pending_event_id = ?;",
            [.text(pendingEventId)],
            Self.decodePendingEvent(from:)
        ).first
    }

    func allPendingEvents() throws -> [PendingEventProjectionPayload] {
        try database.query(
            """
            SELECT \(Self.pendingEventColumns) FROM timeline_pending_event
            ORDER BY updated_at DESC, pending_event_id DESC;
            """,
            [],
            Self.decodePendingEvent(from:))
    }

    // MARK: - Lifecycle

    func deleteAll() throws {
        try database.exec("""
            DELETE FROM timeline_event;
            DELETE FROM timeline_era;
            DELETE FROM timeline_person;
            DELETE FROM timeline_pending_event;
            """)
    }

    // MARK: - Value mapping

    private static func nullableReal(_ value: Double?) -> SQLiteValue {
        value.map(SQLiteValue.real) ?? .null
    }

    /// Denormalised list to its JSON text column. A failure here is not
    /// reachable — `[String]` always encodes — so it degrades to an empty array
    /// rather than making every caller handle an error that cannot happen.
    ///
    /// The coder is built per call rather than shared. These run *outside* the
    /// database lock, so a shared `JSONEncoder` would be touched concurrently
    /// for no gain worth the argument: `[String]` is the whole payload, and the
    /// allocation is noise next to the statement that follows it.
    private static func encodeList(_ values: [String]) -> String {
        guard let data = try? JSONEncoder().encode(values),
              let json = String(data: data, encoding: .utf8) else {
            return "[]"
        }
        return json
    }

    /// Inverse of `encodeList`. Unreadable JSON yields an empty list rather than
    /// failing the fetch: the column is a cache of something Windows can
    /// republish, and losing an event's whole row because one tag list was
    /// mangled would be a worse outcome than losing its tags. Row mapping is
    /// non-throwing throughout for the same reason (see `SQLiteCaptureStore`).
    private static func decodeList(_ json: String) -> [String] {
        guard let data = json.data(using: .utf8),
              let values = try? JSONDecoder().decode([String].self, from: data) else {
            return []
        }
        return values
    }

    // MARK: - Row mapping

    /// Decodes a row selected with `eventColumns`.
    private static func decodeEvent(from row: SQLiteRowReader) -> EventProjectionPayload {
        EventProjectionPayload(
            eventId: row.text(0),
            title: row.text(1),
            description: row.optionalText(2),
            startDate: row.date(3),
            endDate: row.optionalDate(4),
            datePrecision: row.text(5),
            displayDate: row.optionalText(6),
            earliestPossible: row.optionalDate(7),
            latestPossible: row.optionalDate(8),
            category: row.optionalText(9),
            eraId: row.optionalText(10),
            tags: decodeList(row.text(11)),
            personIds: decodeList(row.text(12)),
            locations: decodeList(row.text(13)),
            latitude: row.isNull(14) ? nil : row.double(14),
            longitude: row.isNull(15) ? nil : row.double(15),
            mediaCount: row.int(16),
            updatedAtUtc: row.date(17)
        )
    }

    /// Decodes a row selected with `eraColumns`.
    private static func decodeEra(from row: SQLiteRowReader) -> EraProjectionPayload {
        EraProjectionPayload(
            eraId: row.text(0),
            name: row.text(1),
            subtitle: row.optionalText(2),
            startDate: row.date(3),
            endDate: row.optionalDate(4),
            colorCode: row.text(5),
            categoryId: row.optionalText(6),
            categoryName: row.optionalText(7),
            displayOrder: row.int(8),
            updatedAtUtc: row.date(9)
        )
    }

    /// Decodes a row selected with `personColumns`.
    private static func decodePerson(from row: SQLiteRowReader) -> PersonProjectionPayload {
        PersonProjectionPayload(
            personId: row.text(0),
            name: row.text(1),
            nickname: row.optionalText(2),
            relationship: row.optionalText(3),
            mergedIntoId: row.optionalText(4),
            isFavorite: row.int(5) != 0,
            eventCount: row.int(6),
            updatedAtUtc: row.date(7)
        )
    }

    /// Decodes a row selected with `pendingEventColumns`.
    private static func decodePendingEvent(from row: SQLiteRowReader) -> PendingEventProjectionPayload {
        PendingEventProjectionPayload(
            pendingEventId: row.text(0),
            captureId: row.optionalText(1),
            title: row.text(2),
            description: row.optionalText(3),
            startDate: row.date(4),
            endDate: row.optionalDate(5),
            datePrecision: row.text(6),
            displayDate: row.optionalText(7),
            category: row.optionalText(8),
            confidenceScore: row.double(9),
            tags: decodeList(row.text(10)),
            peopleNames: decodeList(row.text(11)),
            transcriptPreview: row.optionalText(12),
            updatedAtUtc: row.date(13)
        )
    }
}
