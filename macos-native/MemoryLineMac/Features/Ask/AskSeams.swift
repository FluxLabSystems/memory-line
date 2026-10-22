import Foundation

/// What the Mac needs from the sync client to hold an assistant conversation.
///
/// A narrow protocol rather than a use of `SyncAPI`, for the reason
/// `MacDecisionPushing` gives: `SyncAPI` is the surface every fake in both test
/// suites conforms to, and the phone has no Ask screen, so the four assistant
/// calls stay concrete on `SyncAPIClient` and are named here as exactly the
/// capability this feature uses.
protocol MacAssistantAsking: AnyObject, Sendable {
    func createAssistantSession(_ request: AssistantSessionCreateRequest) async throws -> AssistantSessionResponse
    func submitAssistantTurn(sessionId: String, _ request: AssistantTurnCreateRequest) async throws -> AssistantTurnResponse
    func assistantTurn(turnId: String) async throws -> AssistantTurnResponse
    func cancelAssistantTurn(turnId: String) async throws -> AssistantTurnResponse
}

/// `SyncAPIClient` already has matching methods, so this conformance is
/// declaration only. It lives in the macOS target rather than beside the
/// client because the client is shared source.
extension SyncAPIClient: MacAssistantAsking {}
