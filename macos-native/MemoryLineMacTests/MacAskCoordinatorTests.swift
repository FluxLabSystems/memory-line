import XCTest
@testable import MemoryLineMac

/// Covers the Ask conversation as the Mac composes it: the shared
/// `AskCoordinator` over the `AssistantAsking` seam with the "mac" surface.
/// The settings store is real (SQLite in a temp file); only the four assistant
/// calls are faked, because what is tested is the conversation's bookkeeping
/// — one session per conversation, one turn per question, what a poll
/// replaces, how a reaped session or turn is handled — not the HTTP.
final class MacAskCoordinatorTests: XCTestCase {

    // MARK: - Fixture

    private struct Fixture {
        let settings: SQLiteSettingsStore
    }

    private func makeFixture(paired: Bool = true) throws -> Fixture {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("macask-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }

        let database = try AppDatabase.open(at: directory.appendingPathComponent("memoryline.db"))
        let settings = SQLiteSettingsStore(database: database)
        if paired {
            settings.set("https://example.test", for: AppSettingsKey.serverURL)
            settings.set("device-1", for: AppSettingsKey.deviceId)
        }
        return Fixture(settings: settings)
    }

    @MainActor
    private func makeCoordinator(_ f: Fixture, api: FakeAsking) -> AskCoordinator {
        AskCoordinator(settings: f.settings, api: api, surface: "mac")
    }

    // MARK: - Asking

    @MainActor
    func testAskOpensOneSessionWithTheMacSurfaceAndSubmitsTheTurn() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)

        await coordinator.ask("  Where did we stop for lunch?  ")
        coordinator.stopPolling()

        XCTAssertEqual(api.sessions.map(\.surface), ["mac"])
        XCTAssertEqual(api.submitted.count, 1)
        XCTAssertEqual(api.submitted.first?.request.question, "Where did we stop for lunch?", "whitespace is trimmed")
        let turnId = try XCTUnwrap(api.submitted.first?.request.turnId)
        XCTAssertEqual(turnId, turnId.lowercased(), "turn IDs are lowercased UUIDs, the service's idempotency key")
        XCTAssertNotNil(UUID(uuidString: turnId))
        XCTAssertEqual(coordinator.turns.map(\.turnId), [turnId])
        XCTAssertEqual(coordinator.turns.first?.status, AssistantTurnStatus.pending)
        XCTAssertTrue(coordinator.hasOpenTurn)
        XCTAssertEqual(coordinator.state, .idle)

        // A second question rides the same session.
        await coordinator.ask("And dinner?")
        coordinator.stopPolling()
        XCTAssertEqual(api.sessions.count, 1)
        XCTAssertEqual(api.submitted.count, 2)
        XCTAssertEqual(api.submitted.last?.sessionId, "session-1")
        XCTAssertEqual(coordinator.turns.count, 2)
    }

    @MainActor
    func testAskDoesNothingWhileUnpaired_OrWhenTheQuestionIsBlank() async throws {
        let api = FakeAsking()

        let unpairedFixture = try makeFixture(paired: false)
        let unpaired = makeCoordinator(unpairedFixture, api: api)
        XCTAssertFalse(unpaired.canAsk)
        await unpaired.ask("Anything?")
        XCTAssertTrue(api.sessions.isEmpty)
        XCTAssertTrue(unpaired.turns.isEmpty)
        XCTAssertEqual(unpaired.state, .idle)

        let pairedFixture = try makeFixture()
        let paired = makeCoordinator(pairedFixture, api: api)
        await paired.ask("   ")
        XCTAssertTrue(api.sessions.isEmpty, "a blank question is not sent")
    }

    // MARK: - Polling

    @MainActor
    func testRefreshReplacesAnOpenTurnWithWhatTheServiceHolds() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)
        await coordinator.ask("Where did we stop for lunch?")
        coordinator.stopPolling()
        let turnId = try XCTUnwrap(coordinator.turns.first?.turnId)

        api.pollResults[turnId] = .success(FakeAsking.turn(
            id: turnId, sessionId: "session-1", sequence: 1, question: "Where did we stop for lunch?",
            status: AssistantTurnStatus.completed,
            result: AssistantTurnResult(
                answer: "Green River.", grounding: AssistantGrounding.archive, citations: [],
                model: nil, elapsedMs: nil, assistantAudioArtifactId: nil)))

        await coordinator.refresh()

        XCTAssertEqual(api.polled, [turnId])
        XCTAssertEqual(coordinator.turns.count, 1, "a poll replaces the turn, never appends a copy")
        XCTAssertEqual(coordinator.turns.first?.status, AssistantTurnStatus.completed)
        XCTAssertEqual(coordinator.turns.first?.result?.answer, "Green River.")
        XCTAssertFalse(coordinator.hasOpenTurn)

        // Nothing open: a further refresh asks the service nothing.
        await coordinator.refresh()
        XCTAssertEqual(api.polled.count, 1)
    }

    @MainActor
    func testAReapedTurnIsShownAsExpiredRatherThanPolledForever() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)
        await coordinator.ask("Where did we stop for lunch?")
        coordinator.stopPolling()

        // Unscripted: the fake answers 404, as the service does once the session is reaped.
        await coordinator.refresh()

        let turn = try XCTUnwrap(coordinator.turns.first)
        XCTAssertEqual(turn.status, AssistantTurnStatus.cancelled)
        XCTAssertEqual(turn.failureReason, "This question expired on the sync service before your PC answered it.")
        XCTAssertFalse(coordinator.hasOpenTurn)
    }

    @MainActor
    func testATransientPollFailureLeavesTheTurnOpen() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)
        await coordinator.ask("Where did we stop for lunch?")
        coordinator.stopPolling()
        let turnId = try XCTUnwrap(coordinator.turns.first?.turnId)
        api.pollResults[turnId] = .failure(
            SyncAPIError(statusCode: 503, apiError: nil, retryable: true, message: "busy"))

        await coordinator.refresh()

        XCTAssertEqual(coordinator.turns.first?.status, AssistantTurnStatus.pending, "the next poll tries again")
        XCTAssertTrue(coordinator.hasOpenTurn)
        XCTAssertEqual(coordinator.state, .idle, "a transient poll failure is not surfaced as a failed ask")
    }

    // MARK: - Session lifecycle

    @MainActor
    func testAReapedSessionIsReopenedAndTheQuestionResubmittedOnce() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)
        await coordinator.ask("First question")
        coordinator.stopPolling()
        XCTAssertEqual(api.sessions.count, 1)

        // The service reaped the day-old session: the submit answers 404.
        api.submitErrors = [
            SyncAPIError(statusCode: 404, apiError: nil, retryable: false, message: "No such assistant session.")
        ]
        await coordinator.ask("Second question")
        coordinator.stopPolling()

        XCTAssertEqual(api.sessions.count, 2, "a fresh session is opened")
        XCTAssertEqual(api.submitted.count, 3, "the refused submit plus the one that succeeded")
        XCTAssertEqual(api.submitted.last?.sessionId, "session-2")
        XCTAssertEqual(api.submitted.last?.request.question, "Second question")
        XCTAssertEqual(coordinator.turns.count, 2)
        XCTAssertEqual(coordinator.state, .idle)
    }

    @MainActor
    func testTransportAndAuthFailuresBecomeDisplaySafeStates() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)

        api.createError = SyncAPIError(
            statusCode: nil, apiError: nil, retryable: true, message: "The network connection was lost.")
        await coordinator.ask("Where did we stop for lunch?")
        XCTAssertEqual(coordinator.state, .failed("The network connection was lost."))
        XCTAssertTrue(coordinator.turns.isEmpty)

        api.createError = SyncAPIError(statusCode: 401, apiError: nil, retryable: false, message: "token")
        await coordinator.ask("Where did we stop for lunch?")
        XCTAssertEqual(coordinator.state, .failed("This device is no longer paired. Re-pair in Settings to ask."))
    }

    @MainActor
    func testAFailFastAnswerFromTheServiceIsTerminalAtOnce() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        api.submitStatus = AssistantTurnStatus.failed
        let coordinator = makeCoordinator(f, api: api)

        await coordinator.ask("Where did we stop for lunch?")

        XCTAssertEqual(coordinator.turns.first?.status, AssistantTurnStatus.failed)
        XCTAssertFalse(coordinator.hasOpenTurn, "a turn the service failed at birth is never polled")
        coordinator.startPolling()
        XCTAssertTrue(api.polled.isEmpty)
    }

    @MainActor
    func testCancelReplacesTheTurnAndResetForgetsTheConversation() async throws {
        let f = try makeFixture()
        let api = FakeAsking()
        let coordinator = makeCoordinator(f, api: api)
        await coordinator.ask("Where did we stop for lunch?")
        coordinator.stopPolling()
        let turnId = try XCTUnwrap(coordinator.turns.first?.turnId)

        await coordinator.cancel(turnId: turnId)
        XCTAssertEqual(api.cancelled, [turnId])
        XCTAssertEqual(coordinator.turns.first?.status, AssistantTurnStatus.cancelled)
        XCTAssertFalse(coordinator.hasOpenTurn)

        coordinator.reset()
        XCTAssertTrue(coordinator.turns.isEmpty)
        XCTAssertNil(coordinator.session)
        XCTAssertEqual(coordinator.state, .idle)

        // After a reset the next question opens a new session: the old one
        // belonged to a registration that no longer exists.
        await coordinator.ask("Fresh start")
        coordinator.stopPolling()
        XCTAssertEqual(api.sessions.count, 2)
    }
}

/// Scripted `AssistantAsking` double. Records every call; sessions are minted
/// in order ("session-1", "session-2", …), submissions answer with the
/// configured status, polls answer from `pollResults` (404 when unscripted,
/// as the service does for a reaped turn), and cancels come back cancelled.
/// `@unchecked Sendable`: every access happens on the test's main actor.
private final class FakeAsking: AssistantAsking, @unchecked Sendable {
    private(set) var sessions: [AssistantSessionCreateRequest] = []
    private(set) var submitted: [(sessionId: String, request: AssistantTurnCreateRequest)] = []
    private(set) var polled: [String] = []
    private(set) var cancelled: [String] = []

    var createError: SyncAPIError?
    /// Thrown by successive submits, in order; empty means every submit succeeds.
    var submitErrors: [SyncAPIError] = []
    var submitStatus = AssistantTurnStatus.pending
    var pollResults: [String: Result<AssistantTurnResponse, SyncAPIError>] = [:]

    func createAssistantSession(_ request: AssistantSessionCreateRequest) async throws -> AssistantSessionResponse {
        sessions.append(request)
        if let createError { throw createError }
        return AssistantSessionResponse(
            sessionId: "session-\(sessions.count)", deviceId: "device-1",
            preferredResponder: request.preferredResponder,
            createdAtUtc: Date(), lastTurnAtUtc: Date(), turnCount: 0,
            mode: request.mode, tripId: nil)
    }

    func submitAssistantTurn(sessionId: String, _ request: AssistantTurnCreateRequest) async throws -> AssistantTurnResponse {
        submitted.append((sessionId, request))
        if !submitErrors.isEmpty { throw submitErrors.removeFirst() }
        return Self.turn(
            id: request.turnId, sessionId: sessionId, sequence: submitted.count,
            question: request.question, status: submitStatus)
    }

    func assistantTurn(turnId: String) async throws -> AssistantTurnResponse {
        polled.append(turnId)
        guard let result = pollResults[turnId] else {
            throw SyncAPIError(statusCode: 404, apiError: nil, retryable: false, message: "No such assistant turn.")
        }
        return try result.get()
    }

    func cancelAssistantTurn(turnId: String) async throws -> AssistantTurnResponse {
        cancelled.append(turnId)
        return Self.turn(id: turnId, sessionId: "session-1", sequence: 1, question: "", status: AssistantTurnStatus.cancelled)
    }

    static func turn(
        id: String, sessionId: String, sequence: Int, question: String, status: String,
        result: AssistantTurnResult? = nil
    ) -> AssistantTurnResponse {
        AssistantTurnResponse(
            turnId: id, sessionId: sessionId, sequence: sequence, question: question, status: status,
            requestedResponder: AssistantResponder.windows, actualResponder: nil,
            createdAtUtc: Date(), completedAtUtc: AssistantTurnStatus.isTerminal(status) ? Date() : nil,
            result: result, failureReason: nil, failureRetryable: nil)
    }
}
