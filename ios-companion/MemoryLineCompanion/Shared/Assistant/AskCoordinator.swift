import Foundation
import Observation
import os

/// Holds one assistant conversation for an Ask screen and keeps it current.
/// Shared source: the Mac's Ask pane and the phone's Ask tab are the same
/// coordinator over the same client, differing only in the `surface` label
/// they open a session with.
///
/// **This device asks; it never answers.** A question goes to the sync
/// service as an assistant turn, the service publishes it on the change feed,
/// and the owner's Windows PC — the only machine holding the archive and the
/// retrieval index — answers it back over the same feed through its
/// `AssistantTurnResponder` (design §19 Phase 4, `AssistantContracts.cs`).
/// Nothing here searches or generates: the screen shows what the service
/// holds for each turn, and while that is `pending` the honest thing to show
/// is that the PC has not picked the question up yet. A turn for a responder
/// mode the service has nothing deployed for comes back already `failed`,
/// with the reason, and is shown as such at once.
///
/// **Why poll rather than read the feed.** Both apps already pull the feed,
/// but the answer to this device's own question is also readable from
/// `GET /assistant/turns/{turnId}`, and that is the simpler contract for a
/// screen the user is looking at: a few-second poll while a turn is open,
/// nothing otherwise. The feed path remains available if this ever needs to
/// survive the screen being closed.
///
/// **What is kept, and where.** Turns live in memory for the session; the
/// service reaps a conversation idle for a day, and a relaunched app starts a
/// fresh one. There is no list endpoint and no local table on purpose:
/// questions and answers are personal memory content (§14.5), and a
/// transcript on disk is a second copy of the archive's words that nothing
/// here needs.
@MainActor
@Observable
final class AskCoordinator {
    enum State: Equatable {
        case idle
        case asking
        /// Display-safe text — never transcript content or credentials (design §14.5).
        case failed(String)
    }

    private let settings: any SettingsStore
    private let api: any AssistantAsking
    /// Free-text label sent when a session is opened ("mac", "ios");
    /// diagnostics only, never used for routing (`AssistantContracts.cs`).
    private let surface: String
    private let logger = AppLog.logger(category: "ask")

    private(set) var state: State = .idle
    private(set) var session: AssistantSessionResponse?
    /// Turns of the current conversation, oldest first.
    private(set) var turns: [AssistantTurnResponse] = []

    @ObservationIgnored private var poller: Task<Void, Never>?

    init(settings: any SettingsStore, api: any AssistantAsking, surface: String) {
        self.settings = settings
        self.api = api
        self.surface = surface
    }

    /// True once this device is paired — the precondition for asking anything.
    var canAsk: Bool {
        settings.string(AppSettingsKey.deviceId)?.isEmpty == false
            && settings.string(AppSettingsKey.serverURL)?.isEmpty == false
    }

    /// True while any turn is still waiting on a responder.
    var hasOpenTurn: Bool {
        turns.contains { !AssistantTurnStatus.isTerminal($0.status) }
    }

    /// Submits a question and starts polling for its answer.
    func ask(_ question: String) async {
        let trimmed = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, state != .asking, canAsk else { return }

        state = .asking
        do {
            let turn: AssistantTurnResponse
            do {
                turn = try await submit(trimmed)
            } catch let error as SyncAPIError where error.statusCode == 404 && session != nil {
                // The service reaps a conversation idle for a day. Open a
                // fresh one and ask once more; the question is the same.
                logger.notice("assistant session no longer exists; opening a new one")
                session = nil
                turn = try await submit(trimmed)
            }
            turns.append(turn)
            state = .idle
            logger.info("turn \(turn.turnId, privacy: .public) submitted as sequence \(turn.sequence) status \(turn.status, privacy: .public)")
            startPolling()
        } catch let error as SyncAPIError {
            state = .failed(error.needsReauthentication
                ? "This device is no longer paired. Re-pair in Settings to ask."
                : error.message)
            logger.warning("ask failed status=\(error.statusCode ?? -1)")
        } catch {
            state = .failed("Could not reach the sync server.")
            logger.error("ask failed: \(String(describing: type(of: error)), privacy: .public)")
        }
    }

    /// Cancels an open turn. Terminal on the service: a responder mid-answer
    /// learns to stop, and a late answer is discarded rather than shown.
    func cancel(turnId: String) async {
        do {
            replace(try await api.cancelAssistantTurn(turnId: turnId))
            logger.info("turn \(turnId, privacy: .public) cancelled")
        } catch let error as SyncAPIError {
            state = .failed(error.message)
        } catch {
            state = .failed("Could not reach the sync server.")
        }
    }

    /// Re-reads every open turn once.
    func refresh() async {
        for turn in turns where !AssistantTurnStatus.isTerminal(turn.status) {
            do {
                replace(try await api.assistantTurn(turnId: turn.turnId))
            } catch let error as SyncAPIError where error.statusCode == 404 {
                // Reaped on the service — nothing will answer it now, and
                // saying so beats a spinner that never stops.
                var gone = turn
                gone.status = AssistantTurnStatus.cancelled
                gone.failureReason = "This question expired on the sync service before your PC answered it."
                replace(gone)
                logger.notice("turn \(turn.turnId, privacy: .public) no longer exists on the service")
            } catch {
                // Transient; the next poll tries again.
                logger.debug("turn refresh failed: \(String(describing: type(of: error)), privacy: .public)")
            }
        }
    }

    /// Polls open turns every `interval` until none is open. Calling it again
    /// replaces the running poller rather than stacking a second one; with no
    /// open turn it does nothing, so a screen showing finished answers costs
    /// no requests.
    func startPolling(interval: Duration = .seconds(3)) {
        poller?.cancel()
        guard hasOpenTurn else { return }
        poller = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: interval)
                } catch {
                    return // cancelled
                }
                guard let self else { return }
                await self.refresh()
                if !self.hasOpenTurn { return }
            }
        }
    }

    func stopPolling() {
        poller?.cancel()
        poller = nil
    }

    /// Forgets the conversation. Called on unpair: the session belongs to a
    /// device registration that no longer exists, and its turns are the
    /// archive's words, which an unpaired device has no business keeping.
    func reset() {
        stopPolling()
        session = nil
        turns = []
        state = .idle
    }

    private func submit(_ question: String) async throws -> AssistantTurnResponse {
        let session = try await currentSession()
        return try await api.submitAssistantTurn(
            sessionId: session.sessionId,
            AssistantTurnCreateRequest(turnId: UUID().uuidString.lowercased(), question: question))
    }

    private func currentSession() async throws -> AssistantSessionResponse {
        if let session { return session }
        let created = try await api.createAssistantSession(AssistantSessionCreateRequest(surface: surface))
        session = created
        logger.info("assistant session \(created.sessionId, privacy: .public) opened")
        return created
    }

    private func replace(_ turn: AssistantTurnResponse) {
        if let index = turns.firstIndex(where: { $0.turnId == turn.turnId }) {
            turns[index] = turn
        } else {
            turns.append(turn)
        }
    }
}
