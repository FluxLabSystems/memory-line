import XCTest
@testable import MemoryLineCompanion

final class CaptureStoreTests: XCTestCase {
    private var databaseURL: URL!
    private var database: SQLiteDatabase!
    private var store: SQLiteCaptureStore!

    override func setUpWithError() throws {
        databaseURL = TestSupport.temporaryDatabaseURL()
        database = try AppDatabase.open(at: databaseURL)
        store = SQLiteCaptureStore(database: database)
    }

    override func tearDownWithError() throws {
        store = nil
        database = nil
        TestSupport.removeDatabaseFiles(at: databaseURL)
    }

    // MARK: - Round trip

    func testInsertFetchRoundTripPreservesEveryField() throws {
        var capture = TestSupport.makeCapture(
            id: "cap-roundtrip",
            type: .tripLog,
            state: .uploaded,
            capturedAt: Date(timeIntervalSince1970: 1_754_500_000.123),
            createdAt: Date(timeIntervalSince1970: 1_754_500_001.456))
        capture.titleHint = "Diner off route 66"
        capture.userNote = "Ask about the pie"
        capture.byteLength = 123_456_789
        capture.sha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        capture.durationSeconds = 61.25
        capture.uploadAttempts = 3
        capture.lastError = "HTTP 503"
        capture.artifactId = "artifact-9"
        capture.uploadedAt = Date(timeIntervalSince1970: 1_754_500_100.789)

        try store.insert(capture)
        let fetched = try XCTUnwrap(store.capture(id: "cap-roundtrip"))

        XCTAssertEqual(fetched.id, capture.id)
        XCTAssertEqual(fetched.type, capture.type)
        XCTAssertEqual(fetched.state, capture.state)
        assertSameInstant(fetched.capturedAt, capture.capturedAt)
        XCTAssertEqual(fetched.timezoneId, capture.timezoneId)
        XCTAssertEqual(fetched.localOffsetMinutes, capture.localOffsetMinutes)
        XCTAssertEqual(fetched.titleHint, capture.titleHint)
        XCTAssertEqual(fetched.userNote, capture.userNote)
        XCTAssertEqual(fetched.fileName, capture.fileName)
        XCTAssertEqual(fetched.byteLength, capture.byteLength)
        XCTAssertEqual(fetched.sha256, capture.sha256)
        XCTAssertEqual(fetched.durationSeconds, capture.durationSeconds, accuracy: 0.0001)
        assertSameInstant(fetched.createdAt, capture.createdAt)
        XCTAssertEqual(fetched.uploadAttempts, capture.uploadAttempts)
        XCTAssertEqual(fetched.lastError, capture.lastError)
        XCTAssertEqual(fetched.artifactId, capture.artifactId)
        assertSameInstant(try XCTUnwrap(fetched.uploadedAt), try XCTUnwrap(capture.uploadedAt))
    }

    func testNilOptionalFieldsRoundTripAsNil() throws {
        let capture = TestSupport.makeCapture(id: "cap-nils")
        try store.insert(capture)
        let fetched = try XCTUnwrap(store.capture(id: "cap-nils"))
        XCTAssertNil(fetched.titleHint)
        XCTAssertNil(fetched.userNote)
        XCTAssertNil(fetched.lastError)
        XCTAssertNil(fetched.artifactId)
        XCTAssertNil(fetched.uploadedAt)
    }

    func testUpdatePersistsChanges() throws {
        var capture = TestSupport.makeCapture(id: "cap-update", state: .savedLocal)
        try store.insert(capture)

        capture.state = .failedRecoverable
        capture.uploadAttempts = 2
        capture.lastError = "network unreachable"
        capture.byteLength = 42_000
        capture.sha256 = "deadbeef"
        capture.durationSeconds = 12.5
        capture.artifactId = "artifact-1"
        try store.update(capture)

        let fetched = try XCTUnwrap(store.capture(id: "cap-update"))
        XCTAssertEqual(fetched.state, .failedRecoverable)
        XCTAssertEqual(fetched.uploadAttempts, 2)
        XCTAssertEqual(fetched.lastError, "network unreachable")
        XCTAssertEqual(fetched.byteLength, 42_000)
        XCTAssertEqual(fetched.sha256, "deadbeef")
        XCTAssertEqual(fetched.durationSeconds, 12.5, accuracy: 0.0001)
        XCTAssertEqual(fetched.artifactId, "artifact-1")
    }

    func testFetchMissingCaptureReturnsNil() throws {
        XCTAssertNil(try store.capture(id: "no-such-id"))
    }

    // MARK: - Queries

    func testAllCapturesNewestFirst() throws {
        let base = Date(timeIntervalSince1970: 1_754_500_000)
        try store.insert(TestSupport.makeCapture(id: "old", createdAt: base))
        try store.insert(TestSupport.makeCapture(id: "newest", createdAt: base.addingTimeInterval(20)))
        try store.insert(TestSupport.makeCapture(id: "middle", createdAt: base.addingTimeInterval(10)))

        XCTAssertEqual(try store.allCaptures().map(\.id), ["newest", "middle", "old"])
    }

    func testPendingUploadsFiltersStatesAndOrdersOldestFirst() throws {
        let base = Date(timeIntervalSince1970: 1_754_500_000)
        try store.insert(TestSupport.makeCapture(id: "done", state: .uploaded, createdAt: base))
        try store.insert(TestSupport.makeCapture(id: "live", state: .recording, createdAt: base.addingTimeInterval(5)))
        try store.insert(TestSupport.makeCapture(id: "queued", state: .queuedUpload, createdAt: base.addingTimeInterval(10)))
        try store.insert(TestSupport.makeCapture(id: "gone", state: .cancelled, createdAt: base.addingTimeInterval(15)))
        try store.insert(TestSupport.makeCapture(id: "failed", state: .failedRecoverable, createdAt: base.addingTimeInterval(20)))
        try store.insert(TestSupport.makeCapture(id: "inflight", state: .uploading, createdAt: base.addingTimeInterval(25)))
        try store.insert(TestSupport.makeCapture(id: "saved", state: .savedLocal, createdAt: base.addingTimeInterval(30)))
        try store.insert(TestSupport.makeCapture(id: "remote", state: .processingRemote, createdAt: base.addingTimeInterval(35)))

        // Only saved_local / queued_upload / failed_recoverable, oldest first.
        XCTAssertEqual(try store.pendingUploads().map(\.id), ["queued", "failed", "saved"])
        XCTAssertEqual(try store.pendingUploadCount(), 3)
    }

    func testAbandonedRecordingsReturnsOnlyRecordingState() throws {
        let base = Date(timeIntervalSince1970: 1_754_500_000)
        try store.insert(TestSupport.makeCapture(id: "finished", state: .savedLocal, createdAt: base))
        try store.insert(TestSupport.makeCapture(id: "crashed-late", state: .recording, createdAt: base.addingTimeInterval(10)))
        try store.insert(TestSupport.makeCapture(id: "crashed-early", state: .recording, createdAt: base.addingTimeInterval(5)))

        XCTAssertEqual(try store.abandonedRecordings().map(\.id), ["crashed-early", "crashed-late"])
    }

    func testPendingUploadCountEmptyDatabaseIsZero() throws {
        XCTAssertEqual(try store.pendingUploadCount(), 0)
    }

    func testDeleteRemovesCapture() throws {
        try store.insert(TestSupport.makeCapture(id: "cap-delete"))
        try store.delete(id: "cap-delete")
        XCTAssertNil(try store.capture(id: "cap-delete"))
        XCTAssertTrue(try store.allCaptures().isEmpty)
    }

    func testInsertDuplicateIdThrows() throws {
        try store.insert(TestSupport.makeCapture(id: "cap-dup"))
        XCTAssertThrowsError(try store.insert(TestSupport.makeCapture(id: "cap-dup"))) { error in
            XCTAssertTrue(error is SQLiteError)
        }
    }

    // MARK: - Location side table (design §14.4)

    func testLocationRoundTripsThroughTheSideTableOnEveryReadPath() throws {
        var capture = TestSupport.makeCapture(id: "cap-located")
        capture.location = LocationContext(
            latitude: 39.7392, longitude: -104.9903,
            horizontalAccuracyM: 12.5, headingDegrees: 270, speedMps: 27.3)
        try store.insert(capture)

        let fetched = try XCTUnwrap(store.capture(id: "cap-located"))
        XCTAssertEqual(fetched.location, capture.location)
        XCTAssertEqual(try store.allCaptures().first?.location, capture.location)
        XCTAssertEqual(try store.pendingUploads().first?.location, capture.location, "the upload path reads it too")
        // The capture columns decode exactly as before the join.
        XCTAssertEqual(fetched.id, "cap-located")
        XCTAssertEqual(fetched.state, .savedLocal)
        XCTAssertEqual(fetched.fileName, "cap-located.m4a")
    }

    func testCoarseLocationKeepsNilOptionals_AndUpdateReplacesOrClearsIt() throws {
        var capture = TestSupport.makeCapture(id: "cap-coarse")
        capture.location = LocationContext(
            latitude: 39.74, longitude: -104.99, horizontalAccuracyM: 1_000, headingDegrees: nil, speedMps: nil)
        try store.insert(capture)
        XCTAssertEqual(try store.capture(id: "cap-coarse")?.location, capture.location)

        capture.location = LocationContext(
            latitude: 40, longitude: -105, horizontalAccuracyM: nil, headingDegrees: nil, speedMps: nil)
        try store.update(capture)
        XCTAssertEqual(try store.capture(id: "cap-coarse")?.location?.latitude, 40)
        XCTAssertNil(try store.capture(id: "cap-coarse")?.location?.horizontalAccuracyM)

        capture.location = nil
        try store.update(capture)
        XCTAssertNil(try store.capture(id: "cap-coarse")?.location, "clearing the snapshot removes the side row")
    }

    func testCaptureWithoutLocationReadsAsNil_AndDeleteRemovesTheSideRow() throws {
        try store.insert(TestSupport.makeCapture(id: "cap-nowhere"))
        XCTAssertNil(try store.capture(id: "cap-nowhere")?.location)

        var located = TestSupport.makeCapture(id: "cap-gone")
        located.location = LocationContext(
            latitude: 1, longitude: 2, horizontalAccuracyM: nil, headingDegrees: nil, speedMps: nil)
        try store.insert(located)
        try store.delete(id: "cap-gone")
        XCTAssertEqual(
            try database.scalarInt64("SELECT COUNT(*) FROM capture_location WHERE capture_id = 'cap-gone';"), 0)

        // Re-using the ID without a location must not resurrect the old snapshot.
        try store.insert(TestSupport.makeCapture(id: "cap-gone"))
        XCTAssertNil(try store.capture(id: "cap-gone")?.location)
    }

    func testLocationSchemaIsIdempotentAcrossStoreInstances() throws {
        // A second store over the same connection re-applies the DDL; the
        // table and its rows survive, and user_version is untouched.
        var capture = TestSupport.makeCapture(id: "cap-again")
        capture.location = LocationContext(
            latitude: 3, longitude: 4, horizontalAccuracyM: 5, headingDegrees: nil, speedMps: nil)
        try store.insert(capture)
        let second = SQLiteCaptureStore(database: database)
        XCTAssertEqual(try second.capture(id: "cap-again")?.location, capture.location)
        XCTAssertEqual(try database.scalarInt64("PRAGMA user_version;"), Int64(AppDatabase.migrations.count))
    }

    // MARK: - Helpers

    private func assertSameInstant(
        _ actual: Date, _ expected: Date,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        XCTAssertEqual(
            actual.timeIntervalSince1970, expected.timeIntervalSince1970,
            accuracy: 0.01, file: file, line: line)
    }
}
