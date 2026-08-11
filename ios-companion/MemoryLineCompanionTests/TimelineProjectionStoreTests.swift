import XCTest
@testable import MemoryLineCompanion

/// Covers the macOS timeline projection: what the store persists and gives back,
/// what the range query includes, and how `TimelineProjectionApplier` handles a
/// change feed it does not fully understand.
///
/// The store is real SQLite in a temp directory rather than a double. The whole
/// job of this layer is what SQLite returns after a round trip — column order,
/// nullability, the JSON list columns, the range predicate — so a fake store
/// would assert that the test's own dictionary works. Only the *changes* are
/// fabricated, because the sync client is already covered by
/// `MacSyncCoordinatorTests`.
final class TimelineProjectionStoreTests: XCTestCase {

    // MARK: - Fixture

    /// Per-test fixture. Built inside each test rather than in setUp: the tests
    /// are `@MainActor` like the rest of this suite, and overriding XCTest's
    /// nonisolated `setUpWithError` from an isolated context is a mismatch. The
    /// temp directory is removed by `addTeardownBlock`.
    private struct Fixture {
        let directory: URL
        let database: SQLiteDatabase
        let store: SQLiteTimelineProjectionStore
        let applier: TimelineProjectionApplier
    }

    private func makeFixture() throws -> Fixture {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("timeline-projection-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }

        let database = try AppDatabase.open(at: directory.appendingPathComponent("memoryline.db"))
        let store = try SQLiteTimelineProjectionStore(database: database)
        return Fixture(
            directory: directory,
            database: database,
            store: store,
            applier: TimelineProjectionApplier(store: store))
    }

    // MARK: - Round trips

    @MainActor
    func testEventRoundTripPreservesEveryField() throws {
        let f = try makeFixture()
        let event = EventProjectionPayload(
            eventId: "evt-1",
            title: "Diner outside Amarillo",
            description: "Stopped for pie on the way west",
            startDate: Self.day(0),
            endDate: Self.day(1),
            datePrecision: "exact",
            displayDate: "14 November 2023",
            earliestPossible: Self.day(-1),
            latestPossible: Self.day(2),
            category: "travel",
            eraId: "era-1",
            tags: ["road trip", "food"],
            personIds: ["per-1", "per-2"],
            locations: ["Amarillo, TX"],
            latitude: 35.222,
            longitude: -101.831,
            mediaCount: 3,
            updatedAtUtc: Self.day(3))

        try f.store.upsertEvent(event)

        // Whole-second dates and these coordinates are exactly representable as
        // doubles, so the REAL columns round-trip bit for bit and the whole
        // struct can be compared at once — including the three JSON columns.
        XCTAssertEqual(try f.store.event(eventId: "evt-1"), event)
    }

    /// Every optional is genuinely nullable and an empty list is not a null: a
    /// dateless-precision event with no tags, no people and no place is the
    /// ordinary case for something extracted from a short voice note.
    @MainActor
    func testEventOptionalsAndEmptyListsRoundTrip() throws {
        let f = try makeFixture()
        let event = Self.event(id: "evt-bare", startDate: Self.day(0), updatedAt: Self.day(0))

        try f.store.upsertEvent(event)
        let stored = try XCTUnwrap(f.store.event(eventId: "evt-bare"))

        XCTAssertEqual(stored, event)
        XCTAssertNil(stored.description)
        XCTAssertNil(stored.endDate)
        XCTAssertNil(stored.latitude)
        XCTAssertEqual(stored.tags, [])
        XCTAssertEqual(stored.personIds, [])
        XCTAssertEqual(stored.locations, [])
    }

    @MainActor
    func testUpsertReplacesTheExistingRowRatherThanDuplicatingIt() throws {
        let f = try makeFixture()
        var event = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(0))
        try f.store.upsertEvent(event)

        event.title = "Corrected on Windows"
        event.tags = ["fixed"]
        event.updatedAtUtc = Self.day(1)
        try f.store.upsertEvent(event)

        let all = try f.store.events(from: .distantPast, to: .distantFuture)
        XCTAssertEqual(all.count, 1)
        XCTAssertEqual(all.first, event)
    }

    @MainActor
    func testEraPersonAndPendingEventRoundTrip() throws {
        let f = try makeFixture()
        let era = Self.era(id: "era-1", startDate: Self.day(0), updatedAt: Self.day(0))
        let person = Self.person(id: "per-1", name: "Dana", updatedAt: Self.day(0))
        let pending = Self.pendingEvent(id: "pen-1", startDate: Self.day(0), updatedAt: Self.day(0))

        try f.store.upsertEra(era)
        try f.store.upsertPerson(person)
        try f.store.upsertPendingEvent(pending)

        XCTAssertEqual(try f.store.era(eraId: "era-1"), era)
        XCTAssertEqual(try f.store.person(personId: "per-1"), person)
        XCTAssertEqual(try f.store.pendingEvent(pendingEventId: "pen-1"), pending)
        XCTAssertEqual(try f.store.allEras(), [era])
        XCTAssertEqual(try f.store.allPeople(), [person])
        XCTAssertEqual(try f.store.allPendingEvents(), [pending])
    }

    // MARK: - Range query

    @MainActor
    func testEventsInRangeAreOrderedAndExcludeEventsOutsideIt() throws {
        let f = try makeFixture()
        // Inserted out of order on purpose: the ordering must come from the
        // query, not from insertion order or rowid.
        for (id, offset) in [("d", 30.0), ("b", 10.0), ("a", 0.0), ("c", 20.0)] {
            try f.store.upsertEvent(
                Self.event(id: id, startDate: Self.day(offset), updatedAt: Self.day(offset)))
        }

        let inWindow = try f.store.events(from: Self.day(5), to: Self.day(25))

        XCTAssertEqual(inWindow.map(\.eventId), ["b", "c"])
    }

    /// A multi-year event is part of what the user sees in the middle of it. A
    /// start-only predicate would make it vanish when the window no longer
    /// contains its first day, which is the bug this asserts against.
    @MainActor
    func testSpanningEventIsReturnedWhenTheWindowFallsInsideIt() throws {
        let f = try makeFixture()
        var spanning = Self.event(id: "evt-span", startDate: Self.day(0), updatedAt: Self.day(0))
        spanning.endDate = Self.day(40)
        try f.store.upsertEvent(spanning)
        try f.store.upsertEvent(
            Self.event(id: "evt-far", startDate: Self.day(100), updatedAt: Self.day(100)))

        let inWindow = try f.store.events(from: Self.day(10), to: Self.day(20))

        XCTAssertEqual(inWindow.map(\.eventId), ["evt-span"])
    }

    // MARK: - Applying changes

    @MainActor
    func testAppliesEveryProjectedEntityType() throws {
        let f = try makeFixture()
        let event = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(0))
        let era = Self.era(id: "era-1", startDate: Self.day(0), updatedAt: Self.day(0))
        let person = Self.person(id: "per-1", name: "Dana", updatedAt: Self.day(0))
        let pending = Self.pendingEvent(id: "pen-1", startDate: Self.day(0), updatedAt: Self.day(0))

        let applied = try f.applier.apply([
            upsertChange(event, entityType: SyncChangeEntityType.event, entityId: "evt-1", changeId: 1),
            upsertChange(era, entityType: SyncChangeEntityType.era, entityId: "era-1", changeId: 2),
            upsertChange(person, entityType: SyncChangeEntityType.person, entityId: "per-1", changeId: 3),
            upsertChange(pending, entityType: SyncChangeEntityType.pendingEvent, entityId: "pen-1", changeId: 4)
        ])

        XCTAssertEqual(applied, 4)
        XCTAssertEqual(try f.store.event(eventId: "evt-1"), event)
        XCTAssertEqual(try f.store.era(eraId: "era-1"), era)
        XCTAssertEqual(try f.store.person(personId: "per-1"), person)
        XCTAssertEqual(try f.store.pendingEvent(pendingEventId: "pen-1"), pending)
    }

    /// Pages replay whenever applying one failed, so an older revision arriving
    /// after a newer one is expected traffic, not corruption. Last-write-wins is
    /// on `updatedAtUtc` — note that the stale change here has the *higher*
    /// change id, which is exactly why the change id must not be the key.
    @MainActor
    func testOlderReplayedRevisionDoesNotClobberNewer() throws {
        let f = try makeFixture()
        var newer = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(2))
        newer.title = "Corrected title"
        var older = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(1))
        older.title = "Original title"

        XCTAssertEqual(
            try f.applier.apply([
                upsertChange(newer, entityType: SyncChangeEntityType.event, entityId: "evt-1", changeId: 1)
            ]),
            1)
        XCTAssertEqual(
            try f.applier.apply([
                upsertChange(older, entityType: SyncChangeEntityType.event, entityId: "evt-1", changeId: 2)
            ]),
            0,
            "a stale revision must not count as applied")

        XCTAssertEqual(try f.store.event(eventId: "evt-1")?.title, "Corrected title")
    }

    /// Re-delivering the *same* revision must stay idempotent rather than being
    /// rejected as stale — equal timestamps re-apply.
    @MainActor
    func testReapplyingTheSameRevisionIsIdempotent() throws {
        let f = try makeFixture()
        let event = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(1))
        let change = upsertChange(
            event, entityType: SyncChangeEntityType.event, entityId: "evt-1", changeId: 1)

        XCTAssertEqual(try f.applier.apply([change]), 1)
        XCTAssertEqual(try f.applier.apply([change]), 1)

        XCTAssertEqual(try f.store.events(from: .distantPast, to: .distantFuture), [event])
    }

    @MainActor
    func testDeleteChangeRemovesTheRow() throws {
        let f = try makeFixture()
        let event = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(0))
        _ = try f.applier.apply([
            upsertChange(event, entityType: SyncChangeEntityType.event, entityId: "evt-1", changeId: 1)
        ])
        XCTAssertNotNil(try f.store.event(eventId: "evt-1"))

        let applied = try f.applier.apply([
            deleteChange(entityType: SyncChangeEntityType.event, entityId: "evt-1", changeId: 2)
        ])

        XCTAssertEqual(applied, 1)
        XCTAssertNil(try f.store.event(eventId: "evt-1"))
        XCTAssertEqual(try f.store.events(from: .distantPast, to: .distantFuture), [])
    }

    /// A delete for something this device never held is normal: the projection only
    /// starts at the cursor the device paired on. It must be a silent no-op, and
    /// it still counts as applied — the change was consumed and the end state is
    /// the one Windows asked for.
    @MainActor
    func testDeleteForAnUnknownIdIsANoOp() throws {
        let f = try makeFixture()

        let applied = try f.applier.apply([
            deleteChange(entityType: SyncChangeEntityType.person, entityId: "never-seen", changeId: 1)
        ])

        XCTAssertEqual(applied, 1)
        XCTAssertEqual(try f.store.allPeople(), [])
    }

    /// The feed pins `entityId` to one spelling of the UUID and only requires the
    /// payload's own copy to *denote* the same id (`SyncChangeService`, "Entity
    /// IDs"). SQLite compares TEXT byte for byte, so the row has to be keyed on
    /// `entityId`: key it on the payload's spelling and the tombstone below
    /// deletes nothing, leaving a memory the user removed on Windows on screen
    /// here forever.
    @MainActor
    func testRowsAreKeyedOnEntityIdRatherThanThePayloadsSpelling() throws {
        let f = try makeFixture()
        let canonical = "a1b2c3d4-0000-4000-8000-000000000001"
        let event = Self.event(id: canonical.uppercased(), startDate: Self.day(0), updatedAt: Self.day(0))

        XCTAssertEqual(
            try f.applier.apply([
                upsertChange(
                    event,
                    entityType: SyncChangeEntityType.event,
                    entityId: canonical,
                    changeId: 1)
            ]),
            1)

        XCTAssertEqual(try f.store.event(eventId: canonical)?.eventId, canonical)
        XCTAssertNil(try f.store.event(eventId: canonical.uppercased()),
                     "the payload's own spelling must not become a second row")

        let applied = try f.applier.apply([
            deleteChange(entityType: SyncChangeEntityType.event, entityId: canonical, changeId: 2)
        ])

        XCTAssertEqual(applied, 1)
        XCTAssertEqual(try f.store.events(from: .distantPast, to: .distantFuture), [])
    }

    /// The feed is shared. A page routinely carries entities that belong to other
    /// features, and they must pass through untouched.
    @MainActor
    func testUnknownEntityTypesAreIgnored() throws {
        let f = try makeFixture()
        let event = Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(0))

        let applied = try f.applier.apply([
            upsertChange(event, entityType: SyncChangeEntityType.captureStatus, entityId: "cap-1", changeId: 1),
            upsertChange(event, entityType: "assistant_turn", entityId: "turn-1", changeId: 2),
            deleteChange(entityType: SyncChangeEntityType.capture, entityId: "cap-1", changeId: 3)
        ])

        XCTAssertEqual(applied, 0)
        XCTAssertNil(try f.store.event(eventId: "evt-1"))
    }

    /// `pending_event_decision` is the one entity a companion *authors*. Seeing
    /// it on the pull feed means reading back a verdict, not receiving a
    /// projection — applying it would fabricate a pending event out of a
    /// decision and quietly claim the approval happened here.
    @MainActor
    func testPendingEventDecisionsAreNotApplied() throws {
        let f = try makeFixture()
        let pending = Self.pendingEvent(id: "pen-1", startDate: Self.day(0), updatedAt: Self.day(0))

        let applied = try f.applier.apply([
            upsertChange(
                pending,
                entityType: SyncChangeEntityType.pendingEventDecision,
                entityId: "pen-1",
                changeId: 1)
        ])

        XCTAssertEqual(applied, 0)
        XCTAssertNil(try f.store.pendingEvent(pendingEventId: "pen-1"))
    }

    /// A change this build cannot parse must not stall the pass: throwing here
    /// would make the caller hold the cursor and replay the same page forever.
    /// All three shapes are skipped — malformed JSON, a missing payload, and
    /// well-formed JSON missing a field the contract declares non-nullable —
    /// while the valid change in the same page still lands.
    @MainActor
    func testMalformedPayloadsAreSkippedRatherThanThrowing() throws {
        let f = try makeFixture()
        let good = Self.event(id: "evt-good", startDate: Self.day(0), updatedAt: Self.day(0))

        var brokenJSON = upsertChange(
            good, entityType: SyncChangeEntityType.event, entityId: "evt-broken", changeId: 1)
        brokenJSON.payloadJson = "{ not json"

        var missingPayload = upsertChange(
            good, entityType: SyncChangeEntityType.era, entityId: "era-missing", changeId: 2)
        missingPayload.payloadJson = nil

        var missingField = upsertChange(
            good, entityType: SyncChangeEntityType.event, entityId: "evt-partial", changeId: 3)
        // Valid JSON, but `startDate`, `title` and `updatedAtUtc` are not
        // optional in the contract.
        missingField.payloadJson = #"{"eventId":"evt-partial"}"#

        let applied = try f.applier.apply([
            brokenJSON,
            missingPayload,
            missingField,
            upsertChange(good, entityType: SyncChangeEntityType.event, entityId: "evt-good", changeId: 4)
        ])

        XCTAssertEqual(applied, 1)
        XCTAssertEqual(try f.store.events(from: .distantPast, to: .distantFuture).map(\.eventId), ["evt-good"])
        XCTAssertEqual(try f.store.allEras(), [])
    }

    // MARK: - People

    /// A merged person is a tombstone, not a deletion: old events keep pointing
    /// at the merged-away id, so the row has to stay for those ids to resolve.
    /// Hiding merged people is the *hub's* job, not the store's.
    @MainActor
    func testMergedPeopleAreKeptSoOldEventIdsStillResolve() throws {
        let f = try makeFixture()
        var merged = Self.person(id: "per-old", name: "Dan", updatedAt: Self.day(0))
        merged.mergedIntoId = "per-new"
        let survivor = Self.person(id: "per-new", name: "Dana", updatedAt: Self.day(0))

        try f.store.upsertPerson(merged)
        try f.store.upsertPerson(survivor)

        XCTAssertEqual(try f.store.person(personId: "per-old")?.mergedIntoId, "per-new")
        XCTAssertEqual(try f.store.allPeople().map(\.personId), ["per-old", "per-new"],
                       "ordered by name: Dan before Dana; both rows present")
    }

    // MARK: - Lifecycle

    @MainActor
    func testDeleteAllClearsEveryTable() throws {
        let f = try makeFixture()
        try f.store.upsertEvent(Self.event(id: "evt-1", startDate: Self.day(0), updatedAt: Self.day(0)))
        try f.store.upsertEra(Self.era(id: "era-1", startDate: Self.day(0), updatedAt: Self.day(0)))
        try f.store.upsertPerson(Self.person(id: "per-1", name: "Dana", updatedAt: Self.day(0)))
        try f.store.upsertPendingEvent(
            Self.pendingEvent(id: "pen-1", startDate: Self.day(0), updatedAt: Self.day(0)))

        try f.store.deleteAll()

        XCTAssertEqual(try f.store.events(from: .distantPast, to: .distantFuture), [])
        XCTAssertEqual(try f.store.allEras(), [])
        XCTAssertEqual(try f.store.allPeople(), [])
        XCTAssertEqual(try f.store.allPendingEvents(), [])
    }

    // MARK: - Schema

    /// The projection's DDL is re-runnable and must not touch `user_version`.
    /// If it did, the next `AppDatabase.open` would reject the file as newer
    /// than the app supports — and on a database the iOS companion also opens,
    /// with its own shorter migration array, that would brick the phone.
    @MainActor
    func testSchemaIsIdempotentAndLeavesTheAppSchemaVersionAlone() throws {
        let f = try makeFixture()
        try f.store.upsertEvent(Self.event(id: "survivor", startDate: Self.day(0), updatedAt: Self.day(0)))

        try TimelineProjectionSchema.migrate(f.database)
        // A second store over the same connection re-runs the DDL too.
        _ = try SQLiteTimelineProjectionStore(database: f.database)

        XCTAssertNotNil(try f.store.event(eventId: "survivor"))
        XCTAssertEqual(
            try f.database.scalarInt64("PRAGMA user_version;"),
            Int64(AppDatabase.migrations.count))
        XCTAssertNoThrow(try f.database.migrate(AppDatabase.migrations))
    }

    @MainActor
    func testProjectionSurvivesReopeningTheDatabase() throws {
        let f = try makeFixture()
        try f.store.upsertEvent(Self.event(id: "persisted", startDate: Self.day(0), updatedAt: Self.day(0)))

        let reopened = try AppDatabase.open(at: f.directory.appendingPathComponent("memoryline.db"))
        let reopenedStore = try SQLiteTimelineProjectionStore(database: reopened)

        XCTAssertNotNil(try reopenedStore.event(eventId: "persisted"))
    }

    // MARK: - Change helpers

    private func upsertChange<Payload: Encodable>(
        _ payload: Payload,
        entityType: String,
        entityId: String,
        changeId: Int64
    ) -> SyncChangeDto {
        SyncChangeDto(
            changeId: changeId,
            entityType: entityType,
            entityId: entityId,
            operation: SyncOperation.upsert,
            revision: changeId,
            changedAtUtc: Self.day(0),
            sourceDeviceId: "windows-1",
            payloadJson: String(data: try! SyncJSON.encoder.encode(payload), encoding: .utf8))
    }

    /// A delete carries no payload — the entity id is the only identifier.
    private func deleteChange(entityType: String, entityId: String, changeId: Int64) -> SyncChangeDto {
        SyncChangeDto(
            changeId: changeId,
            entityType: entityType,
            entityId: entityId,
            operation: SyncOperation.delete,
            revision: changeId,
            changedAtUtc: Self.day(0),
            sourceDeviceId: "windows-1",
            payloadJson: nil)
    }

    // MARK: - Payload helpers

    /// Whole-second offsets from a fixed instant. Whole seconds keep every date
    /// exactly representable through JSON and through the REAL columns, so
    /// payloads can be compared for equality without a tolerance.
    private static func day(_ offset: Double) -> Date {
        Date(timeIntervalSince1970: 1_700_000_000 + offset * 86_400)
    }

    /// Every field is passed explicitly, including the nils: these fixtures are
    /// also the check that the memberwise initializers still match the contract's
    /// field order.
    private static func event(id: String, startDate: Date, updatedAt: Date) -> EventProjectionPayload {
        EventProjectionPayload(
            eventId: id,
            title: "A memory",
            description: nil,
            startDate: startDate,
            endDate: nil,
            datePrecision: "day",
            displayDate: nil,
            earliestPossible: nil,
            latestPossible: nil,
            category: nil,
            eraId: nil,
            tags: [],
            personIds: [],
            locations: [],
            latitude: nil,
            longitude: nil,
            mediaCount: 0,
            updatedAtUtc: updatedAt)
    }

    private static func era(id: String, startDate: Date, updatedAt: Date) -> EraProjectionPayload {
        EraProjectionPayload(
            eraId: id,
            name: "University",
            subtitle: nil,
            startDate: startDate,
            endDate: nil,
            colorCode: "#3366CC",
            categoryId: nil,
            categoryName: nil,
            displayOrder: 0,
            updatedAtUtc: updatedAt)
    }

    private static func person(id: String, name: String, updatedAt: Date) -> PersonProjectionPayload {
        PersonProjectionPayload(
            personId: id,
            name: name,
            nickname: nil,
            relationship: nil,
            mergedIntoId: nil,
            isFavorite: false,
            eventCount: 0,
            updatedAtUtc: updatedAt)
    }

    private static func pendingEvent(
        id: String,
        startDate: Date,
        updatedAt: Date
    ) -> PendingEventProjectionPayload {
        PendingEventProjectionPayload(
            pendingEventId: id,
            captureId: "cap-1",
            title: "Extracted memory",
            description: nil,
            startDate: startDate,
            endDate: nil,
            datePrecision: "month",
            displayDate: nil,
            category: nil,
            confidenceScore: 0.82,
            tags: [],
            peopleNames: ["Dana"],
            transcriptPreview: nil,
            updatedAtUtc: updatedAt)
    }
}
