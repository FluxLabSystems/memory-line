import SwiftUI

/// Lists the captures held in the local database, newest first, with the
/// Windows-authored processing status alongside each one.
///
/// This is the walking skeleton: it exercises the whole shared stack —
/// `SQLiteCaptureStore` for the rows, `SQLiteCaptureStatusStore` for the
/// Phase 3 status projection — without needing any macOS-specific capture or
/// upload code to exist yet. Recording on the Mac comes later (port plan §4.1).
struct LibraryView: View {
    @Environment(MacAppEnvironment.self) private var environment

    @State private var captures: [CaptureRecord] = []
    @State private var statuses: [String: CaptureStatusRecord] = [:]
    @State private var loadError: String?

    var body: some View {
        Group {
            if let loadError {
                ContentUnavailableView {
                    Label("Could not read the capture database", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(loadError)
                } actions: {
                    Button("Try Again") { load() }
                }
            } else if captures.isEmpty {
                // Says what is actually true. This used to promise that
                // "captures recorded on a paired device will appear here once
                // they sync", which is not something this screen can deliver:
                // MacSyncCoordinator applies `capture_status` and the timeline
                // projection, and ignores `capture` and `capture_artifact`
                // entirely. Recordings made on the phone are not in this list
                // and will not arrive until the Mac can download their audio.
                ContentUnavailableView {
                    Label("No recordings yet", systemImage: "waveform")
                } description: {
                    Text("Recordings you make on this Mac appear here. Captures from your iPhone go straight to your PC — this Mac does not download their audio.")
                }
            } else {
                List(captures) { capture in
                    CaptureRow(capture: capture, status: statuses[capture.id])
                }
            }
        }
        .navigationTitle("Library")
        .safeAreaInset(edge: .bottom) { uploadStatusBar }
        .toolbar {
            ToolbarItem {
                Button {
                    Task {
                        await environment.sync.pullNow()
                        load()
                    }
                } label: {
                    Label("Sync Now", systemImage: "arrow.triangle.2.circlepath")
                }
                .disabled(!environment.sync.canSync || environment.sync.state == .syncing)
                .help(environment.sync.canSync
                      ? "Pull the latest capture status from the sync server"
                      : "Pair this Mac in Settings to sync")
            }
            ToolbarItem {
                Button {
                    load()
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
            }
        }
        .task {
            load()
            // Start the loop here rather than at launch so an unpaired Mac
            // never spins a ticker it cannot use; startPeriodicSync replaces
            // any existing one, so re-entering the view is harmless.
            if environment.sync.canSync {
                environment.sync.startPeriodicSync()
            }
        }
        .onChange(of: environment.sync.lastPulledAt) { _, _ in
            // A completed pull may have written new status rows.
            load()
        }
    }

    /// Upload state, which nothing rendered until now.
    ///
    /// `MacUploadCoordinator` has published `state`, `pendingCount`,
    /// `isDraining` and `lastUploadedAt` since it was written, and no view read
    /// any of them — its own doc comment describes an "Upload now" button that
    /// did not exist. The consequence was worse than a missing feature: the
    /// coordinator has a `.failed` state, so a recording that could not reach
    /// the server failed *silently* on this Mac. A capture app that loses
    /// audio without saying so is the one failure that must never be quiet.
    ///
    /// Hidden entirely when there is nothing to say — no pending work, no
    /// failure — so it does not become permanent chrome.
    @ViewBuilder
    private var uploadStatusBar: some View {
        if environment.uploads.pendingCount > 0 || isUploadFailed {
            HStack(spacing: 8) {
                if case .failed(let message) = environment.uploads.state {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    Text(message)
                } else if environment.uploads.state == .uploading {
                    ProgressView().controlSize(.small)
                    Text(pendingLabel)
                } else {
                    Image(systemName: "arrow.up.circle")
                        .foregroundStyle(.secondary)
                    Text(pendingLabel)
                }

                Spacer()

                Button("Upload Now") {
                    Task { await environment.uploads.drainPendingUploads() }
                }
                .controlSize(.small)
                // Disabled for the whole pass, not just while bytes move: there
                // is a stretch after the drain takes its guard and before
                // `state` becomes `.uploading`, and a second press in that
                // window would be a no-op the user reads as a broken button.
                .disabled(environment.uploads.isDraining || !environment.sync.canSync)
            }
            .font(.caption)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.regularMaterial)
        }
    }

    private var isUploadFailed: Bool {
        if case .failed = environment.uploads.state { return true }
        return false
    }

    private var pendingLabel: String {
        let count = environment.uploads.pendingCount
        return count == 1 ? "1 recording waiting to upload" : "\(count) recordings waiting to upload"
    }

    private func load() {
        do {
            captures = try environment.captures.allCaptures()
            // One query for every status rather than one per capture; rows for
            // captures this Mac has not seen are simply unused.
            statuses = Dictionary(
                try environment.statuses.allStatuses().map { ($0.captureId, $0) },
                uniquingKeysWith: { first, _ in first })
            loadError = nil
        } catch {
            loadError = String(describing: error)
        }
    }
}

private struct CaptureRow: View {
    let capture: CaptureRecord
    let status: CaptureStatusRecord?

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: capture.type.symbolName)
                .font(.title3)
                .foregroundStyle(.secondary)
                .frame(width: 24)

            VStack(alignment: .leading, spacing: 2) {
                Text(capture.titleHint ?? capture.capturedAt.formatted(date: .abbreviated, time: .shortened))
                    .font(.body)
                Text(capture.capturedAt.formatted(date: .abbreviated, time: .shortened))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            if let status {
                // Prefer the finer-grained Windows queue stage when it sent one.
                Text(status.processingStage ?? status.status)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Text(capture.state.rawValue)
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
    }
}
