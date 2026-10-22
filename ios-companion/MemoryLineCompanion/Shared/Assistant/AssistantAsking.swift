import Foundation

/// What an Ask screen needs from the sync client to hold an assistant
/// conversation — the four assistant calls, and nothing else.
///
/// A narrow protocol rather than a use of `SyncAPI`, for the reason the Mac's
/// `MacDecisionPushing` gives: `SyncAPI` is the surface every fake in both
/// test suites conforms to, and a requirement added there obliges every one
/// of them to implement a call most of them never make. The assistant calls
/// stay concrete on `SyncAPIClient` and are named here as exactly the
/// capability `AskCoordinator` uses, so a test double is four methods.
protocol AssistantAsking: AnyObject, Sendable {
    func createAssistantSession(_ request: AssistantSessionCreateRequest) async throws -> AssistantSessionResponse
    func submitAssistantTurn(sessionId: String, _ request: AssistantTurnCreateRequest) async throws -> AssistantTurnResponse
    func assistantTurn(turnId: String) async throws -> AssistantTurnResponse
    func cancelAssistantTurn(turnId: String) async throws -> AssistantTurnResponse
}

/// `SyncAPIClient` already has matching methods, so this conformance is
/// declaration only. It lives beside the protocol, in shared source, because
/// both apps compose the same coordinator over the same client.
extension SyncAPIClient: AssistantAsking {}
