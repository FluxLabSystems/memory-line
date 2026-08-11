import XCTest
@testable import MemoryLineCompanion

@MainActor
final class StatusSyncCoordinatorTests: XCTestCase {
    private var databaseURL: URL!
    private var database: SQLiteDatabase!
    private var captures: SQLiteCaptureStore!
    private var settings: SQLiteSettingsStore!
    private var statuses: SQLiteCaptureStatusStore!
    private var projections: SQLiteTimelineProjectionStore!
    private var api: StubSyncAPI!
    private var notifier: SpyStatusNotifier!
    private var coordinator: StatusSyncCoordinator!

    override func setUpWithError() throws {
        databaseURL = TestSupport.temporaryDatabaseURL()
        database = try AppDatabase.open(at: databaseURL)
        captures = SQLiteCaptureStore(database: database)
        settings = SQLiteSettingsStore(database: database)
        statuses = try SQLiteCaptureStatusStore(database: database)
        projections = try SQLiteTimelineProjectionStore(database: database)
        api = StubSyncAPI()
        notifier = SpyStatusNotifier()
        coordinator = StatusSyncCoordinator(
            store: captures,
            settings: settings,
            api: api,
            statusStore: statuses,
            projectionStore: projections,
            notifier: notifier)
        StatusSyncTestData.markPaired(settings)
    }

    override func tearDownWithError() throws {
        coordinator = nil
        projections = nil
        statuses = nil
        settings = nil
        captures = nil
        database = nil
        TestSupport.removeDatabaseFiles(at: databaseURL)
    }

    // MARK: - Status mapping

    func testStatusMapsOntoTheLifecycle() {
        let table: [(status: String, current: CaptureLifecycleState, expected: CaptureLifecycleState?)] = [
            (SyncCaptureStatus.received, .uploaded, .processingRemote),
            (SyncCaptureStatus.processing, .uploaded, .processingRemote),
            (SyncCaptureStatus.reviewReady, .processingRemote, .reviewReady),
            (SyncCaptureStatus.completed, .reviewReady, .completed),
            // Windows may skip straight past a stage the phone never saw.
            (SyncCaptureStatus.completed, .uploaded, .completed),
            (SyncCaptureStatus.reviewReady, .uploaded, .reviewReady),
            // A Windows-side PROCESSING failure never moves the local state:
            // `.failedRecoverable` is the UPLOAD-failure state and is
            // upload-pending, so taking it here would re-queue the audio.
            (SyncCaptureStatus.failed, .uploaded, nil),
            (SyncCaptureStatus.failed, .processingRemote, nil),
            (SyncCaptureStatus.failed, .reviewReady, nil),
            (SyncCaptureStatus.failed, .completed, nil),
            (SyncCaptureStatus.failed, .failedRecoverable, nil),
            // Windows holding the capture proves the upload landed, so a
            // capture parked by a failed upload attempt may move forward.
            (SyncCaptureStatus.reviewReady, .failedRecoverable, .reviewReady),
            (SyncCaptureStatus.processing, .failedRecoverable, .processingRemote),
        ]

        for row in table {
            XCTAssertEqual(
                StatusSyncCoordinator.resolvedState(for: row.status, current: row.current),
                row.expected,
                "\(row.status) from \(row.current.rawValue)")
        }
    }

    /// A late status must never clobber a capture the phone still owns.
    func testNeverDowngradesACaptureThatIsStillLocal() {
        let localStates: [CaptureLifecycleState] = [.recording, .savedLocal, .queuedUpload, .uploading, .cancelled]
        let wireStatuses = [
            SyncCaptureStatus.received,
            SyncCaptureStatus.processing,
            SyncCaptureStatus.reviewReady,
            SyncCaptureStatus.completed,
            SyncCaptureStatus.failed,
        ]

        for state in localStates {
            for status in wireStatuses {
                XCTAssertNil(
                    StatusSyncCoordinator.resolvedState(for: status, current: state),
                    "\(status) must not touch \(state.rawValue)")
            }
        }
    }

    func testNeverMovesACaptureBackwardsThroughTheLifecycle() {
        let backwards: [(status: String, current: CaptureLifecycleState)] = [
            (SyncCaptureStatus.received, .processingRemote),
            (SyncCaptureStatus.processing, .processingRemote),
            (SyncCaptureStatus.processing, .reviewReady),
            (SyncCaptureStatus.processing, .completed),
            (SyncCaptureStatus.reviewReady, .reviewReady),
            (SyncCaptureStatus.reviewReady, .completed),
            (SyncCaptureStatus.completed, .completed),
        ]

        for row in backwards {
            XCTAssertNil(
                StatusSyncCoordinator.resolvedState(for: row.status, current: row.current),
                "\(row.status) from \(row.current.rawValue)")
        }
    }

    /// `local_only` and `uploading` echo what the phone already knows, and a
    /// status from a newer Windows build must be inert rather than harmful.
    func testEchoedAndUnknownStatusesChangeNothing() {
        for status in [SyncCaptureStatus.localOnly, SyncCaptureStatus.uploading, "quantum_indexing", ""] {
            for state in CaptureLifecycleState.allCases {
                XCTAssertNil(
                    StatusSyncCoordinator.resolvedState(for: status, current: state),
                    "\(status) from \(state.rawValue)")
            }
        }
    }

    // MARK: - Applying a pull

    func testAppliesStatusAndStoresTheDetail() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 12, payload: StatusSyncTestData.payload(
                captureId: "cap-1",
                status: SyncCaptureStatus.reviewReady,
                processingStage: "review_ready",
                transcriptAvailable: true,
                transcriptPreview: "We stopped at the diner",
                transcriptCharCount: 4213,
                pendingEventCount: 3,
                approvedEventCount: 0))
        ], nextCursor: 12)]

        await coordinator.pullNow()

        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .reviewReady)
        let status = try XCTUnwrap(statuses.status(captureId: "cap-1"))
        XCTAssertEqual(status.status, SyncCaptureStatus.reviewReady)
        XCTAssertEqual(status.processingStage, "review_ready")
        XCTAssertEqual(status.transcriptPreview, "We stopped at the diner")
        XCTAssertEqual(status.transcriptCharCount, 4213)
        XCTAssertEqual(status.pendingEventCount, 3)
        XCTAssertEqual(status.approvedEventCount, 0)
        XCTAssertNil(coordinator.lastPullError)
        XCTAssertNotNil(coordinator.lastPulledAt)
    }

    /// A Windows-side processing failure is recorded where the History screens
    /// read it, and is left out of the local upload state entirely: the audio
    /// uploaded fine, so re-queueing it would re-send every part forever.
    func testRemoteProcessingFailureIsRecordedWithoutTouchingTheUploadState() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .processingRemote))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 4, payload: StatusSyncTestData.payload(
                captureId: "cap-1",
                status: SyncCaptureStatus.failed,
                processingStage: "failed_retryable",
                failureReason: "The transcription service was unreachable.",
                failureRetryable: true))
        ], nextCursor: 4)]

        await coordinator.pullNow()

        // The capture is untouched — same state, no invented local error.
        let capture = try XCTUnwrap(captures.capture(id: "cap-1"))
        XCTAssertEqual(capture.state, .processingRemote)
        XCTAssertNil(capture.lastError)
        XCTAssertFalse(capture.state.isUploadPending)

        // Nothing was pushed back into the upload queue.
        XCTAssertEqual(try captures.pendingUploadCount(), 0)
        XCTAssertTrue(try captures.pendingUploads().isEmpty)

        // And the failure is still readable from the status store, which is
        // where CaptureLifecycleSummary sources `.processingFailed`.
        let status = try XCTUnwrap(statuses.status(captureId: "cap-1"))
        XCTAssertEqual(status.status, SyncCaptureStatus.failed)
        XCTAssertEqual(status.processingStage, "failed_retryable")
        XCTAssertEqual(status.failureReason, "The transcription service was unreachable.")
        XCTAssertEqual(status.failureRetryable, true)

        // The user is still told about it.
        XCTAssertEqual(notifier.posted, [
            .failed(captureId: "cap-1", reason: "The transcription service was unreachable.")
        ])
    }

    /// The upload leg keeps its own failure state: a capture parked there by a
    /// failed upload must stay upload-pending, and a processing failure on top
    /// of it must not change that either way.
    func testUploadFailureStaysUploadPendingAcrossARemoteFailure() async throws {
        var capture = TestSupport.makeCapture(id: "cap-1", state: .failedRecoverable)
        capture.lastError = "Upload was interrupted."
        try captures.insert(capture)
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 5, payload: StatusSyncTestData.payload(
                captureId: "cap-1",
                status: SyncCaptureStatus.failed,
                failureReason: "Audio could not be decoded."))
        ], nextCursor: 5)]

        await coordinator.pullNow()

        let updated = try XCTUnwrap(captures.capture(id: "cap-1"))
        XCTAssertEqual(updated.state, .failedRecoverable)
        XCTAssertEqual(updated.lastError, "Upload was interrupted.")
        XCTAssertEqual(try captures.pendingUploadCount(), 1)
    }

    /// A failure must not pin the rank floor: Windows retrying and getting
    /// further has to move the capture on.
    func testAdvanceAfterARemoteFailureStillApplies() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .processingRemote))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 1, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.failed,
                failureReason: "Processing failed on the Windows machine.")),
            try StatusSyncTestData.change(changeId: 2, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.reviewReady, pendingEventCount: 2)),
            try StatusSyncTestData.change(changeId: 3, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.completed, approvedEventCount: 2)),
        ], nextCursor: 3)]

        await coordinator.pullNow()

        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .completed)
        XCTAssertEqual(try XCTUnwrap(statuses.status(captureId: "cap-1")).status, SyncCaptureStatus.completed)
        XCTAssertEqual(notifier.posted, [
            .failed(captureId: "cap-1", reason: "Processing failed on the Windows machine."),
            .reviewReady(captureId: "cap-1"),
            .completed(captureId: "cap-1"),
        ])
    }

    /// The failure notification is deduped from the stored status, since no
    /// state change is left to recognize the replay by.
    func testReplayedRemoteFailureNotifiesOnlyOnce() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        let change = try StatusSyncTestData.change(
            changeId: 1,
            payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.failed,
                failureReason: "Audio could not be decoded."))
        api.pullResults = [
            StatusSyncTestData.page([change], nextCursor: 1),
            StatusSyncTestData.page([change], nextCursor: 1),
        ]

        await coordinator.pullNow()
        await coordinator.pullNow()

        XCTAssertEqual(notifier.posted, [
            .failed(captureId: "cap-1", reason: "Audio could not be decoded.")
        ])
        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .uploaded)
    }

    /// A failure for a capture the phone still owns is recorded but not
    /// announced — it has not been handed over as far as this device knows.
    func testRemoteFailureForALocalCaptureIsRecordedSilently() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .queuedUpload))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 6, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.failed,
                failureReason: "Audio could not be decoded."))
        ], nextCursor: 6)]

        await coordinator.pullNow()

        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .queuedUpload)
        XCTAssertEqual(try XCTUnwrap(statuses.status(captureId: "cap-1")).status, SyncCaptureStatus.failed)
        XCTAssertTrue(notifier.posted.isEmpty)
    }

    /// Moving past a failure clears the stale error the UI would otherwise
    /// keep showing.
    func testAdvancingPastAFailureClearsTheLastError() async throws {
        var capture = TestSupport.makeCapture(id: "cap-1", state: .failedRecoverable)
        capture.lastError = "Upload was interrupted."
        try captures.insert(capture)
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 7, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.completed))
        ], nextCursor: 7)]

        await coordinator.pullNow()

        let updated = try XCTUnwrap(captures.capture(id: "cap-1"))
        XCTAssertEqual(updated.state, .completed)
        XCTAssertNil(updated.lastError)
    }

    /// A capture deleted on the phone is not an error: no crash, no orphan
    /// status row, and the cursor still advances past the change.
    func testUnknownCaptureIdIsASilentNoOp() async throws {
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 9, payload: StatusSyncTestData.payload(
                captureId: "deleted-on-phone", status: SyncCaptureStatus.completed))
        ], nextCursor: 9)]

        await coordinator.pullNow()

        XCTAssertNil(try statuses.status(captureId: "deleted-on-phone"))
        XCTAssertEqual(try statuses.allStatuses().count, 0)
        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "9")
        XCTAssertNil(coordinator.lastPullError)
        XCTAssertTrue(notifier.posted.isEmpty)
    }

    /// The change log carries every entity type; the ones this build does not
    /// consume must still carry the cursor forward.
    func testOtherEntityTypesOnlyAdvanceTheCursor() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(
                changeId: 20,
                payload: StatusSyncTestData.payload(captureId: "cap-1", status: SyncCaptureStatus.completed),
                entityType: SyncChangeEntityType.capture)
        ], nextCursor: 20)]

        await coordinator.pullNow()

        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .uploaded)
        XCTAssertNil(try statuses.status(captureId: "cap-1"))
        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "20")
    }

    /// A payload this build cannot read must not wedge the cursor forever.
    func testUndecodablePayloadIsSkippedAndTheCursorAdvances() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        let broken = SyncChangeDto(
            changeId: 30,
            entityType: SyncChangeEntityType.captureStatus,
            entityId: "cap-1",
            operation: "upsert",
            revision: 1,
            changedAtUtc: Date(timeIntervalSince1970: 1_754_500_000),
            sourceDeviceId: "windows-1",
            payloadJson: "{\"captureId\":\"cap-1\"")
        api.pullResults = [StatusSyncTestData.page([broken], nextCursor: 30)]

        await coordinator.pullNow()

        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .uploaded)
        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "30")
        XCTAssertNil(coordinator.lastPullError)
    }

    // MARK: - Cursor and ack

    func testCursorAdvancesPersistsAndIsAckedAfterApply() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 41, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.processing))
        ], nextCursor: 41)]

        await coordinator.pullNow()

        XCTAssertEqual(api.pullCursors, [0])
        XCTAssertEqual(api.ackedCursors, [41])
        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "41")
        // The ack only happens once the page is durably applied.
        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .processingRemote)
    }

    func testNextPassResumesFromThePersistedCursor() async throws {
        settings.set("128", for: SyncCursorKey.pullCursor)

        await coordinator.pullNow()

        XCTAssertEqual(api.pullCursors, [128])
    }

    func testEmptyPageIsNotAcked() async {
        await coordinator.pullNow()

        XCTAssertEqual(api.pullCursors, [0])
        XCTAssertTrue(api.ackedCursors.isEmpty)
        XCTAssertNil(settings.string(SyncCursorKey.pullCursor))
        XCTAssertNil(coordinator.lastPullError)
    }

    func testPagesUntilHasMoreClears() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        try captures.insert(TestSupport.makeCapture(id: "cap-2", state: .uploaded))
        api.pullResults = [
            StatusSyncTestData.page([
                try StatusSyncTestData.change(changeId: 1, payload: StatusSyncTestData.payload(
                    captureId: "cap-1", status: SyncCaptureStatus.processing))
            ], nextCursor: 1, hasMore: true),
            StatusSyncTestData.page([
                try StatusSyncTestData.change(changeId: 2, payload: StatusSyncTestData.payload(
                    captureId: "cap-2", status: SyncCaptureStatus.completed))
            ], nextCursor: 2, hasMore: false),
        ]

        await coordinator.pullNow()

        XCTAssertEqual(api.pullCursors, [0, 1])
        XCTAssertEqual(api.ackedCursors, [1, 2])
        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .processingRemote)
        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-2")).state, .completed)
        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "2")
    }

    func testRetryableFailureStopsAndKeepsTheCursor() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.pullResults = [
            StatusSyncTestData.page([
                try StatusSyncTestData.change(changeId: 5, payload: StatusSyncTestData.payload(
                    captureId: "cap-1", status: SyncCaptureStatus.processing))
            ], nextCursor: 5, hasMore: true),
            .failure(SyncAPIError(
                statusCode: 503, apiError: nil, retryable: true, message: "The server is unavailable.")),
        ]

        await coordinator.pullNow()

        XCTAssertEqual(api.pullCursors, [0, 5])
        XCTAssertEqual(api.ackedCursors, [5])
        // The first page stuck; only the failing page is replayed next time.
        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "5")
        XCTAssertEqual(coordinator.lastPullError, "The server is unavailable.")
        XCTAssertNil(coordinator.lastPulledAt)
    }

    func testFirstPageFailureLeavesTheCursorUntouched() async {
        api.pullResults = [.failure(SyncAPIError(
            statusCode: nil, apiError: nil, retryable: true, message: "The network connection was lost."))]

        await coordinator.pullNow()

        XCTAssertNil(settings.string(SyncCursorKey.pullCursor))
        XCTAssertTrue(api.ackedCursors.isEmpty)
        XCTAssertEqual(coordinator.lastPullError, "The network connection was lost.")
    }

    func testRevokedDeviceReportsARepairableError() async {
        api.pullResults = [.failure(SyncAPIError(
            statusCode: 401, apiError: nil, retryable: false, message: "Unauthorized."))]

        await coordinator.pullNow()

        XCTAssertEqual(
            coordinator.lastPullError,
            "This device is no longer paired. Re-pair in Settings to resume sync.")
    }

    /// A failed ack costs only the server's retention bookkeeping — the cursor
    /// is already durable locally, so the page must not be replayed.
    func testFailedAckDoesNotRewindTheCursor() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.ackError = SyncAPIError(
            statusCode: 503, apiError: nil, retryable: true, message: "The server is unavailable.")
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 6, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.completed))
        ], nextCursor: 6)]

        await coordinator.pullNow()

        XCTAssertEqual(settings.string(SyncCursorKey.pullCursor), "6")
        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .completed)
        XCTAssertNil(coordinator.lastPullError)
    }

    /// Local persistence failure must hold the cursor so the page replays.
    func testLocalPersistenceFailureHoldsTheCursor() async throws {
        let failingCoordinator = StatusSyncCoordinator(
            store: captures,
            settings: settings,
            api: api,
            statusStore: FailingCaptureStatusStore(),
            projectionStore: projections,
            notifier: notifier)
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 8, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.completed))
        ], nextCursor: 8)]

        await failingCoordinator.pullNow()

        XCTAssertNil(settings.string(SyncCursorKey.pullCursor))
        XCTAssertTrue(api.ackedCursors.isEmpty)
        XCTAssertEqual(try XCTUnwrap(captures.capture(id: "cap-1")).state, .uploaded)
        XCTAssertEqual(failingCoordinator.lastPullError, "Could not save the latest capture status.")
    }

    // MARK: - Notifications

    func testNotifiesOnlyOnMeaningfulTransitions() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-processing", state: .uploaded))
        try captures.insert(TestSupport.makeCapture(id: "cap-review", state: .uploaded))
        try captures.insert(TestSupport.makeCapture(id: "cap-done", state: .uploaded))
        try captures.insert(TestSupport.makeCapture(id: "cap-failed", state: .uploaded))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 1, payload: StatusSyncTestData.payload(
                captureId: "cap-processing", status: SyncCaptureStatus.processing)),
            try StatusSyncTestData.change(changeId: 2, payload: StatusSyncTestData.payload(
                captureId: "cap-review", status: SyncCaptureStatus.reviewReady)),
            try StatusSyncTestData.change(changeId: 3, payload: StatusSyncTestData.payload(
                captureId: "cap-done", status: SyncCaptureStatus.completed)),
            try StatusSyncTestData.change(changeId: 4, payload: StatusSyncTestData.payload(
                captureId: "cap-failed", status: SyncCaptureStatus.failed,
                failureReason: "Audio could not be decoded.")),
        ], nextCursor: 4)]

        await coordinator.pullNow()

        XCTAssertEqual(notifier.posted, [
            .reviewReady(captureId: "cap-review"),
            .completed(captureId: "cap-done"),
            .failed(captureId: "cap-failed", reason: "Audio could not be decoded."),
        ])
    }

    /// A replayed change is not news: the state does not move, so nothing is
    /// posted a second time.
    func testReplayedChangeDoesNotNotifyAgain() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        let change = try StatusSyncTestData.change(
            changeId: 1,
            payload: StatusSyncTestData.payload(captureId: "cap-1", status: SyncCaptureStatus.reviewReady))
        api.pullResults = [
            StatusSyncTestData.page([change], nextCursor: 1),
            StatusSyncTestData.page([change], nextCursor: 1),
        ]

        await coordinator.pullNow()
        await coordinator.pullNow()

        XCTAssertEqual(notifier.posted, [.reviewReady(captureId: "cap-1")])
    }

    func testAuthorizationIsRequestedFromThePullPath() async {
        await coordinator.pullNow()

        XCTAssertEqual(notifier.authorizationRequests, 1)
    }

    // MARK: - Guards

    func testUnpairedDeviceNeverPulls() async {
        settings.set(nil, for: AppSettingsKey.deviceId)

        await coordinator.pullNow()

        XCTAssertTrue(api.pullCursors.isEmpty)
        // Nothing was attempted, so the notification prompt stays away too.
        XCTAssertEqual(notifier.authorizationRequests, 0)
    }

    func testSyncDisabledNeverPulls() async {
        settings.set(false, for: AppSettingsKey.syncEnabled)

        await coordinator.pullNow()

        XCTAssertTrue(api.pullCursors.isEmpty)
    }

    // MARK: - Timeline projection

    /// The pass makes two disjoint sweeps over one page. This is the test that
    /// would have caught the phone's original behaviour: pulling the timeline
    /// feed, dropping every event, and reporting a healthy sync.
    func testAppliesTimelineProjectionAlongsideCaptureStatus() async throws {
        try captures.insert(TestSupport.makeCapture(id: "cap-1", state: .uploaded))
        api.pullResults = [StatusSyncTestData.page([
            try StatusSyncTestData.change(changeId: 1, payload: StatusSyncTestData.payload(
                captureId: "cap-1", status: SyncCaptureStatus.completed)),
            try eventChange(changeId: 2, eventId: "evt-1", title: "Moved to Halifax")
        ], nextCursor: 2)]

        await coordinator.pullNow()

        XCTAssertNil(coordinator.lastPullError)
        XCTAssertEqual(try statuses.status(captureId: "cap-1")?.status, SyncCaptureStatus.completed)

        let stored = try projections.event(eventId: "evt-1")
        XCTAssertEqual(stored?.title, "Moved to Halifax")
        XCTAssertEqual(stored?.tags, ["move"], "denormalised lists must survive the round trip")
        XCTAssertEqual(stored?.displayDate, "September 2001",
                       "the precision-honest string is Windows' to format, not ours to re-derive")
    }

    /// A verdict some device authored comes back down the shared feed. Applying
    /// it as a projection would fabricate a review-queue entry out of an answer
    /// to one.
    func testPendingEventDecisionIsNotAppliedAsAProjection() async throws {
        var decision = try eventChange(changeId: 1, eventId: "pending-1", title: "ignored")
        decision.entityType = SyncChangeEntityType.pendingEventDecision
        api.pullResults = [StatusSyncTestData.page([decision], nextCursor: 1)]

        await coordinator.pullNow()

        XCTAssertEqual(try projections.allPendingEvents().count, 0)
        XCTAssertEqual(api.ackedCursors, [1], "ignoring a change still consumes it")
    }

    /// A page whose projection cannot be written must hold the cursor, exactly
    /// as a failing status write does — otherwise the events in it are skipped
    /// permanently while sync reports success.
    func testProjectionPersistenceFailureHoldsTheCursor() async throws {
        let failing = StatusSyncCoordinator(
            store: captures,
            settings: settings,
            api: api,
            statusStore: statuses,
            projectionStore: FailingTimelineProjectionStore(),
            notifier: notifier)
        api.pullResults = [StatusSyncTestData.page([
            try eventChange(changeId: 4, eventId: "evt-1", title: "Moved to Halifax")
        ], nextCursor: 4)]

        await failing.pullNow()

        XCTAssertNil(settings.string(SyncCursorKey.pullCursor))
        XCTAssertTrue(api.ackedCursors.isEmpty)
    }

    private func eventChange(
        changeId: Int64,
        eventId: String,
        title: String,
        updatedAt: Date = Date(timeIntervalSince1970: 1_754_500_500.5)
    ) throws -> SyncChangeDto {
        let payload = EventProjectionPayload(
            eventId: eventId,
            title: title,
            startDate: Date(timeIntervalSince1970: 1_000_000_000),
            datePrecision: "day",
            displayDate: "September 2001",
            tags: ["move"],
            updatedAtUtc: updatedAt)
        let data = try SyncJSON.encoder.encode(payload)
        return SyncChangeDto(
            changeId: changeId,
            entityType: SyncChangeEntityType.event,
            entityId: eventId,
            operation: SyncOperation.upsert,
            revision: 1,
            changedAtUtc: Date(timeIntervalSince1970: 1_754_500_500),
            sourceDeviceId: "windows-1",
            payloadJson: String(decoding: data, as: UTF8.self))
    }
}

/// `TimelineProjectionStore` whose writes always fail, standing in for a full
/// disk or a corrupt database. Reads return nil rather than throwing so the
/// applier reaches the write it is supposed to fail on.
private final class FailingTimelineProjectionStore: TimelineProjectionStore {
    private var error: SQLiteError { SQLiteError(code: 13, message: "database or disk is full") }

    func upsertEvent(_ event: EventProjectionPayload) throws { throw error }
    func deleteEvent(eventId: String) throws { throw error }
    func event(eventId: String) throws -> EventProjectionPayload? { nil }
    func events(from: Date, to: Date) throws -> [EventProjectionPayload] { [] }

    func upsertEra(_ era: EraProjectionPayload) throws { throw error }
    func deleteEra(eraId: String) throws { throw error }
    func era(eraId: String) throws -> EraProjectionPayload? { nil }
    func allEras() throws -> [EraProjectionPayload] { [] }

    func upsertPerson(_ person: PersonProjectionPayload) throws { throw error }
    func deletePerson(personId: String) throws { throw error }
    func person(personId: String) throws -> PersonProjectionPayload? { nil }
    func allPeople() throws -> [PersonProjectionPayload] { [] }

    func upsertPendingEvent(_ pendingEvent: PendingEventProjectionPayload) throws { throw error }
    func deletePendingEvent(pendingEventId: String) throws { throw error }
    func pendingEvent(pendingEventId: String) throws -> PendingEventProjectionPayload? { nil }
    func allPendingEvents() throws -> [PendingEventProjectionPayload] { [] }

    func deleteAll() throws { throw error }
}

/// `CaptureStatusStore` whose writes always fail, standing in for a full disk
/// or a corrupt database.
private final class FailingCaptureStatusStore: CaptureStatusStore {
    private var error: SQLiteError { SQLiteError(code: 13, message: "database or disk is full") }

    func upsert(_ record: CaptureStatusRecord) throws { throw error }
    func status(captureId: String) throws -> CaptureStatusRecord? { throw error }
    func allStatuses() throws -> [CaptureStatusRecord] { throw error }
    func delete(captureId: String) throws { throw error }
}
