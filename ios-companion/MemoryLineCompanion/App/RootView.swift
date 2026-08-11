import AVFAudio
import SwiftUI

/// Root tab layout: Capture | History | Timeline | Settings. Presents the
/// first-run onboarding sheet while the microphone permission is still
/// undetermined so permission education happens before driving-mode use
/// (design §8.6).
///
/// Capture stays first because it is what the app is for, and the ordering is
/// what a thumb reaches for at a traffic light. History is this phone's own
/// recordings; Timeline is the read-only copy of the Windows archive. Keeping
/// them as separate tabs rather than merging them is deliberate: one is a list
/// of things this device made and still owns, the other a copy of someone
/// else's writes, and a single list would blur which is which exactly where a
/// user might expect to be able to edit.
@MainActor
struct RootView: View {
    @Environment(AppEnvironment.self) private var env
    @State private var isOnboardingPresented = false

    var body: some View {
        TabView {
            CaptureView()
                .tabItem { Label("Capture", systemImage: "record.circle.fill") }
            HistoryView()
                .tabItem { Label("History", systemImage: "clock.arrow.circlepath") }
            ArchiveTimelineView()
                .tabItem { Label("Timeline", systemImage: "calendar.day.timeline.left") }
            SettingsView()
                .tabItem { Label("Settings", systemImage: "gearshape") }
        }
        .onAppear {
            if AVAudioApplication.shared.recordPermission == .undetermined {
                isOnboardingPresented = true
            }
        }
        .sheet(isPresented: $isOnboardingPresented) {
            OnboardingView()
        }
    }
}

/// First-run education sheet: explains one-touch capture, requests the
/// microphone permission up front (so Lock Screen/Siri/Action Button capture
/// starts instantly later), and points at Settings → Pairing.
@MainActor
struct OnboardingView: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.dismiss) private var dismiss
    @State private var isRequesting = false
    @State private var wasDenied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 28) {
            VStack(alignment: .leading, spacing: 8) {
                Image(systemName: "waveform.circle.fill")
                    .font(.system(size: 44))
                    .foregroundStyle(.tint)
                Text("One-touch memory capture")
                    .font(.title.bold())
                Text("Made for the road — set it up once, before you drive.")
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 18) {
                onboardingRow(
                    systemImage: "record.circle",
                    title: "Record instantly",
                    detail: "One tap in the app, on the Lock Screen widget, the Action Button, or \u{201C}Hey Siri\u{201D} starts a recording.")
                onboardingRow(
                    systemImage: "internaldrive",
                    title: "Saved on your iPhone first",
                    detail: "Every recording is stored locally before anything else — no signal needed on the road.")
                onboardingRow(
                    systemImage: "arrow.triangle.2.circlepath",
                    title: "Uploads when it can",
                    detail: "Pair with your own sync server in Settings \u{2192} Pairing and recordings reach your computer automatically.")
            }

            Spacer()

            if wasDenied {
                Text("Microphone access is off, so recording won't work yet. You can enable it any time in the iOS Settings app under Privacy & Security \u{2192} Microphone.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }

            VStack(spacing: 10) {
                Button {
                    requestMicrophoneAccess()
                } label: {
                    Group {
                        if isRequesting {
                            ProgressView()
                        } else {
                            Text(wasDenied ? "Done" : "Enable Microphone")
                                .fontWeight(.semibold)
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 6)
                }
                .buttonStyle(.borderedProminent)
                .disabled(isRequesting)

                if !wasDenied {
                    Button("Not Now") { dismiss() }
                        .buttonStyle(.borderless)
                }
            }
        }
        .padding(24)
    }

    private func onboardingRow(systemImage: String, title: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: systemImage)
                .font(.title2)
                .foregroundStyle(.tint)
                .frame(width: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(detail)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func requestMicrophoneAccess() {
        if wasDenied {
            dismiss()
            return
        }
        isRequesting = true
        Task {
            let granted = await env.recorder.requestPermission()
            isRequesting = false
            if granted {
                dismiss()
            } else {
                wasDenied = true
            }
        }
    }
}
