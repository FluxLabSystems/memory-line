import Combine
import SwiftUI

/// Detail screen for one capture: metadata, the fused lifecycle timeline, the
/// transcript preview Windows published (a preview only — the full transcript
/// and every edit stay on Windows, design §19 Phase 3), review counts, audio
/// playback with a read-only progress bar, and a retry control for captures
/// that still need to upload.
@MainActor
struct HistoryDetailView: View {
    @Environment(AppEnvironment.self) private var env
    let captureId: String

    @State private var record: CaptureRecord
    /// Windows-authored processing status; nil until the PC reports on this
    /// capture. Read-only projection — nothing here is editable on the phone.
    @State private var status: CaptureStatusRecord?
    /// Events Windows extracted from this recording and has not yet had
    /// reviewed. The status already carries a *count*; this is what they say.
    @State private var pending: [PendingEventProjectionPayload] = []
    @State private var playback = PlaybackController()
    @State private var isRetrying = false

    /// Drives playback progress updates while the screen is visible.
    private let playbackTimer = Timer.publish(every: 0.25, on: .main, in: .common).autoconnect()

    /// Seeded with the row's record so the screen renders immediately;
    /// `reload()` re-reads the database whenever upload or status state moves.
    init(record: CaptureRecord) {
        self.captureId = record.id
        _record = State(initialValue: record)
    }

    var body: some View {
        detailList(record, summary: CaptureLifecycleSummary(record: record, status: status))
            .navigationTitle(record.type.displayName)
            .navigationBarTitleDisplayMode(.inline)
            .onAppear {
                // Playback must never touch the shared audio session while a
                // recording is live (mirrors the onDisappear guard below).
                playback.isRecordingActive = { env.recorder.phase != .idle }
                reload()
            }
            .onChange(of: env.uploads.pendingCount) { reload() }
            .onChange(of: env.uploads.isSyncing) { reload() }
            .onChange(of: env.statusSync.lastPulledAt) { reload() }
            // A pass that failed part-way still applied earlier pages, so the
            // end of every pull is a reload point, not just a successful one.
            .onChange(of: env.statusSync.isPulling) { reload() }
            .onReceive(playbackTimer) { _ in playback.tick() }
            .onDisappear {
                // Don't tear down the shared audio session if a recording is live.
                playback.stop(deactivateSession: env.recorder.phase == .idle)
            }
    }

    private func detailList(_ record: CaptureRecord, summary: CaptureLifecycleSummary) -> some View {
        List {
            statusSection(summary)
            lifecycleSection(record, summary: summary)
            transcriptSection(summary)
            extractedSection()

            Section("Recording") {
                LabeledContent("Type", value: record.type.displayName)
                LabeledContent("Captured", value: record.capturedAt.formatted(date: .abbreviated, time: .shortened))
                LabeledContent("Duration") {
                    Text(Duration.seconds(max(record.durationSeconds, 0)),
                         format: .time(pattern: .minuteSecond))
                }
                if record.byteLength > 0 {
                    LabeledContent("Size") {
                        Text(record.byteLength, format: .byteCount(style: .file))
                    }
                }
            }

            if let note = record.userNote, !note.isEmpty {
                Section("Note") {
                    Text(note)
                }
            }

            if record.state != .recording {
                Section("Playback") {
                    playbackControls(record)
                }
            }

            uploadSection(record, summary: summary)
        }
        .refreshable { await refresh() }
    }

    // MARK: - Status

    private func statusSection(_ summary: CaptureLifecycleSummary) -> some View {
        Section {
            VStack(alignment: .leading, spacing: 6) {
                CaptureLifecycleChip(summary: summary)
                CaptureScopeLabel(scope: summary.scope)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 2)
            if let stageDetail = summary.stageDetail {
                LabeledContent("Stage", value: stageDetail)
            }
            if summary.phase.isFailure, let reason = summary.failureReason, !reason.isEmpty {
                Label(reason, systemImage: "exclamationmark.triangle.fill")
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .accessibilityLabel("Failure reason. \(reason)")
            }
            if let pending = summary.pendingEventsDescription {
                LabeledContent("Waiting for review", value: pending)
            }
            if let approved = summary.approvedEventsDescription {
                LabeledContent("Approved", value: approved)
            }
            if let updatedAt = summary.remoteUpdatedAt {
                LabeledContent(
                    "PC last reported",
                    value: updatedAt.formatted(date: .abbreviated, time: .shortened))
            }
        } header: {
            Text("Status")
        } footer: {
            Text(summary.scope.explanation)
        }
    }

    // MARK: - Lifecycle timeline

    private func lifecycleSection(_ record: CaptureRecord, summary: CaptureLifecycleSummary) -> some View {
        Section {
            CaptureLifecycleTimelineView(steps: summary.steps(for: record))
        } header: {
            Text("Lifecycle")
        } footer: {
            Text("Your PC reports only the stage it is on right now, so stages it already finished have no time here.")
        }
    }

    // MARK: - Transcript preview

    @ViewBuilder
    private func transcriptSection(_ summary: CaptureLifecycleSummary) -> some View {
        if let preview = summary.transcriptPreview, !preview.isEmpty {
            Section {
                Text(preview)
                    .font(.body)
                    .textSelection(.enabled)
                    .accessibilityLabel("Transcript preview. \(preview)")
                if let remaining = summary.transcriptRemainingCharacters, remaining > 0 {
                    Label(
                        "\(remaining) more characters are on your PC",
                        systemImage: "ellipsis.circle")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            } header: {
                Text("Transcript preview")
            } footer: {
                Text(transcriptFooter(summary))
            }
        } else if summary.hasTranscript {
            // Windows has a transcript but published no excerpt with this
            // status — say so rather than implying nothing was transcribed.
            Section {
                Label("Transcript ready on your PC", systemImage: "text.quote")
                    .font(.subheadline)
            } footer: {
                Text("Open Memory Line on Windows to read and edit the transcript.")
            }
        }
    }

    private func transcriptFooter(_ summary: CaptureLifecycleSummary) -> String {
        let boundary = "The full transcript and all editing live on your Windows PC."
        guard
            let total = summary.transcriptCharCount,
            let preview = summary.transcriptPreview,
            total > 0
        else {
            return "Preview only. \(boundary)"
        }
        let shown = min(preview.utf16.count, total)
        guard shown < total else {
            // Short transcripts fit in the preview whole; saying "preview" then
            // would understate what is on screen.
            return "This capture's whole transcript is \(total) characters. Editing it lives on your Windows PC."
        }
        return "Preview only \u{2014} the first \(shown) of \(total) characters. \(boundary)"
    }

    // MARK: - Extracted events

    /// What Windows pulled out of this recording, awaiting review.
    ///
    /// The capture status has carried a pending *count* since Phase 3; this is
    /// the first time the phone can say what those events actually are, because
    /// the timeline projection now reaches it. That closes the loop the
    /// companion exists for: you record something in the car, and later the same
    /// device tells you what was found in it.
    ///
    /// Read-only, and the footer says where deciding happens. Approving or
    /// rejecting from a companion is admitted by the contract — the Mac does it
    /// — but whether a phone should approve memories into an archive has not
    /// been decided, so this offers no buttons rather than buttons that would
    /// quietly do nothing.
    @ViewBuilder
    private func extractedSection() -> some View {
        if !pending.isEmpty {
            Section {
                ForEach(pending, id: \.pendingEventId) { item in
                    VStack(alignment: .leading, spacing: 3) {
                        Text(item.title)
                            .font(.body)
                        HStack(spacing: 6) {
                            // Windows' precision-honest string, never re-derived
                            // here: an extraction that only knew a season must
                            // not be shown as a precise day.
                            Text(item.displayDate ?? "Date unknown")
                            if let category = item.category, !category.isEmpty {
                                Text(category)
                            }
                            Text(item.confidenceScore, format: .percent.precision(.fractionLength(0)))
                        }
                        .font(.caption)
                        .foregroundStyle(.secondary)

                        if !item.peopleNames.isEmpty {
                            // Names, not ids: the extraction found these in the
                            // audio and they may not be in the contact book yet.
                            Text(item.peopleNames.joined(separator: ", "))
                                .font(.caption2)
                                .foregroundStyle(.tertiary)
                        }
                    }
                    .padding(.vertical, 2)
                }
            } header: {
                Text(pending.count == 1 ? "1 event awaiting review" : "\(pending.count) events awaiting review")
            } footer: {
                Text("Found by your PC in this recording. Approving or rejecting happens on Windows.")
            }
        }
    }

    // MARK: - Upload

    @ViewBuilder
    private func uploadSection(_ record: CaptureRecord, summary: CaptureLifecycleSummary) -> some View {
        if record.uploadAttempts > 0 || record.uploadedAt != nil || record.state.isUploadPending {
            Section("Upload from this iPhone") {
                if record.uploadAttempts > 0 {
                    LabeledContent("Upload attempts", value: "\(record.uploadAttempts)")
                }
                if let uploadedAt = record.uploadedAt {
                    LabeledContent("Uploaded", value: uploadedAt.formatted(date: .abbreviated, time: .shortened))
                }
                if let lastError = record.lastError, record.state == .failedRecoverable {
                    Text(lastError)
                        .font(.footnote)
                        .foregroundStyle(.red)
                }
                if record.state.isUploadPending {
                    // A retry is pointless once the PC has the capture — the
                    // pipeline is idempotent, so it would replay to no effect.
                    if summary.scope == .computer {
                        Text("Your PC already has this capture, so it does not need to upload again.")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    } else {
                        retryButton(record)
                    }
                }
            }
        }
    }

    private func playbackControls(_ record: CaptureRecord) -> some View {
        VStack(spacing: 8) {
            HStack(spacing: 14) {
                Button {
                    togglePlayback(record)
                } label: {
                    Image(systemName: playback.isPlaying ? "pause.circle.fill" : "play.circle.fill")
                        .font(.system(size: 40))
                        .foregroundStyle(.tint)
                }
                .buttonStyle(.plain)
                // No playback while the recorder is live — starting it would
                // reconfigure the shared audio session under the recording.
                .disabled(env.recorder.phase != .idle)
                .accessibilityLabel(playback.isPlaying ? "Pause" : "Play")

                VStack(spacing: 4) {
                    ProgressView(
                        value: min(playback.progress, displayDuration(record)),
                        total: max(displayDuration(record), 0.01))
                    HStack {
                        Text(Duration.seconds(max(playback.progress, 0)),
                             format: .time(pattern: .minuteSecond))
                        Spacer()
                        Text(Duration.seconds(max(displayDuration(record), 0)),
                             format: .time(pattern: .minuteSecond))
                    }
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Playback position")
            }
            if let playbackError = playback.errorMessage {
                Text(playbackError)
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(.vertical, 4)
    }

    private func retryButton(_ record: CaptureRecord) -> some View {
        Button {
            isRetrying = true
            Task {
                await env.uploads.retry(captureId: captureId)
                isRetrying = false
                reload()
            }
        } label: {
            HStack {
                Label(
                    record.state == .failedRecoverable ? "Retry upload" : "Upload now",
                    systemImage: "arrow.clockwise")
                Spacer()
                if isRetrying {
                    ProgressView()
                }
            }
        }
        .disabled(isRetrying || env.uploads.isSyncing)
    }

    /// Prefer the player's authoritative duration once a file is loaded.
    private func displayDuration(_ record: CaptureRecord) -> TimeInterval {
        playback.duration > 0 ? playback.duration : max(record.durationSeconds, 0)
    }

    private func togglePlayback(_ record: CaptureRecord) {
        do {
            let fileURL = try AudioStorage.url(forFileName: record.fileName)
            playback.toggle(fileURL: fileURL)
        } catch {
            playback.errorMessage = "Couldn't locate the audio file for this capture."
        }
    }

    /// Pull-to-refresh: ask the service for status Windows published for this
    /// capture (the pull is service-wide; the store is re-read afterwards).
    private func refresh() async {
        await env.statusSync.pullNow()
        reload()
    }

    /// Re-reads this capture and its Windows status; keeps the last known
    /// snapshot if the row vanished (e.g. deleted from another screen) or the
    /// status read fails, so a transient error never blanks the screen.
    private func reload() {
        if let fetched = try? env.captures.capture(id: captureId), fetched != record {
            record = fetched
        }
        if let fetched = try? env.statuses.status(captureId: captureId) {
            status = fetched
        }
        // Filtered in memory rather than queried by capture id: the review queue
        // is bounded by how much Windows has extracted and not yet had reviewed,
        // so it is small, and a second index on the projection would exist to
        // serve one screen.
        pending = ((try? env.projections.allPendingEvents()) ?? [])
            .filter { $0.captureId == captureId }
    }
}
