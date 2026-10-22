import SwiftUI

/// The Ask pane: type a question, read the answer your Windows PC sends back.
///
/// A thin client over `MacAskCoordinator`, and deliberately so. The archive,
/// the retrieval index and the prompts live on Windows (`MemoryTimeline.Core`);
/// this pane submits a turn to the sync service and renders whatever state the
/// service holds for it. While a turn is `pending` that state is "your PC has
/// not picked this up yet", and the pane says exactly that rather than
/// animating a reply it cannot produce. Every answer carries its grounding, and
/// anything short of `archive` is labelled so general knowledge is never passed
/// off as the user's own history (`AssistantContracts.cs`).
struct AskView: View {
    @Environment(MacAppEnvironment.self) private var environment

    @State private var question = ""

    var body: some View {
        VStack(spacing: 0) {
            content
            Divider()
            composer
        }
        .navigationTitle("Ask")
        // Resume polling for any turn still open when the pane comes back, and
        // stop when nobody is looking: the answer is read from the service,
        // not pushed, so a hidden pane polling is pure cost.
        .task { environment.ask.startPolling() }
        .onDisappear { environment.ask.stopPolling() }
    }

    @ViewBuilder
    private var content: some View {
        if !environment.ask.canAsk {
            ContentUnavailableView {
                Label("Ask your timeline", systemImage: "bubble.left.and.text.bubble.right")
            } description: {
                Text("Pair this Mac with your sync server in Settings to ask questions. Your Windows PC answers them from your archive.")
            }
        } else if environment.ask.turns.isEmpty {
            ContentUnavailableView {
                Label("Ask your timeline", systemImage: "bubble.left.and.text.bubble.right")
            } description: {
                Text("Questions go to your Windows PC over the sync service and are answered from the archive there. This Mac shows the answer; nothing is searched or generated here.")
            }
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 16) {
                    ForEach(environment.ask.turns, id: \.turnId) { turn in
                        TurnCard(turn: turn) {
                            Task { await environment.ask.cancel(turnId: turn.turnId) }
                        }
                    }
                }
                .padding(16)
            }
        }
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: 6) {
            if case .failed(let message) = environment.ask.state {
                Label(message, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }

            HStack(alignment: .bottom, spacing: 8) {
                TextField("Ask about your timeline\u{2026}", text: $question, axis: .vertical)
                    .lineLimit(1...4)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(ask)
                    .disabled(!environment.ask.canAsk)

                Button("Ask", action: ask)
                    .buttonStyle(.borderedProminent)
                    .disabled(!canSubmit)
            }
        }
        .padding(12)
    }

    private var canSubmit: Bool {
        environment.ask.canAsk
            && environment.ask.state != .asking
            && !question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func ask() {
        guard canSubmit else { return }
        let text = question
        question = ""
        Task {
            await environment.ask.ask(text)
            // A failed submission hands the words back rather than losing them.
            if case .failed = environment.ask.state, question.isEmpty {
                question = text
            }
        }
    }
}

/// One question and whatever the service holds for it so far.
private struct TurnCard: View {
    let turn: AssistantTurnResponse
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(turn.question)
                .font(.headline)
                .textSelection(.enabled)

            switch turn.status {
            case AssistantTurnStatus.completed:
                answer
            case AssistantTurnStatus.failed:
                Label(turn.failureReason ?? "Your PC could not answer this question.",
                      systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange)
            case AssistantTurnStatus.cancelled:
                Text(turn.failureReason ?? "Cancelled.")
                    .foregroundStyle(.secondary)
            default:
                waiting
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
    }

    /// Honest about where the question is: on the feed until the PC's next
    /// sync, then being answered. A PC that is asleep, or that has no responder
    /// running, leaves the turn pending — the service never answers on its
    /// behalf — so the text says who is being waited for.
    private var waiting: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            Text(turn.status == AssistantTurnStatus.pending
                 ? "Waiting for your Windows PC to pick this up. It sees the question on its next sync and answers from the archive there."
                 : "Your Windows PC is answering\u{2026}")
                .font(.callout)
                .foregroundStyle(.secondary)
            Spacer()
            Button("Cancel", action: cancel)
                .controlSize(.small)
        }
    }

    @ViewBuilder
    private var answer: some View {
        if let result = turn.result {
            Text(result.answer)
                .textSelection(.enabled)

            if result.grounding != AssistantGrounding.archive {
                Label(groundingNote(result.grounding), systemImage: "info.circle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            let citations = result.citations ?? []
            if citations.isEmpty {
                Text("No events from your archive were cited for this answer.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Based on")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    ForEach(Array(citations.enumerated()), id: \.offset) { _, citation in
                        HStack(spacing: 6) {
                            Text(citation.title)
                                .font(.caption)
                            if let date = citation.displayDate {
                                Text(date)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
        } else {
            Text("Completed without an answer.")
                .foregroundStyle(.secondary)
        }
    }

    private func groundingNote(_ grounding: String) -> String {
        grounding == AssistantGrounding.clientContext
            ? "Based only on context this Mac sent, not the full archive."
            : "General knowledge \u{2014} not drawn from your archive."
    }
}
