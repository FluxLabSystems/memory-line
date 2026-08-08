import SwiftUI

/// Top-level navigation. The sidebar sections mirror the Windows app's
/// navigation so the two heads stay conceptually aligned.
///
/// Capture and Library are this Mac's own: it records, and it holds the
/// recordings it made. Timeline, Eras, Search, Review and People render the read-only
/// projection Windows publishes — this Mac shows a copy of that archive and
/// never edits it, with one exception: Review sends approve/reject verdicts,
/// the single write the contract admits from a companion. Ask is still a
/// placeholder, and renders an honest "not built yet" state naming the section
/// rather than an empty page that looks broken.
///
/// See `docs/design/MACOS-PORT-PLAN.md` §5 for the order these are being filled
/// in and which ones need a decision first.
struct RootView: View {
    @Environment(MacAppEnvironment.self) private var environment
    @State private var selection: Section? = .capture

    enum Section: String, CaseIterable, Identifiable, Hashable {
        case capture = "Capture"
        case library = "Library"
        case timeline = "Timeline"
        case eras = "Eras"
        case search = "Search"
        case review = "Review"
        case people = "People"
        case ask = "Ask"

        var id: String { rawValue }

        var symbol: String {
            switch self {
            case .capture: return "mic.circle"
            case .library: return "waveform"
            case .timeline: return "calendar.day.timeline.left"
            case .eras: return "square.stack.3d.down.right"
            case .search: return "magnifyingglass"
            case .review: return "checkmark.circle"
            case .people: return "person.2"
            case .ask: return "bubble.left.and.text.bubble.right"
            }
        }

        /// Whether the section has a real implementation yet. Only `ask` is
        /// still a placeholder: it needs the Phase 4 assistant contract, whose
        /// responder modes are not built on either side yet.
        var isImplemented: Bool { self != .ask }
    }

    var body: some View {
        NavigationSplitView {
            List(Section.allCases, selection: $selection) { section in
                NavigationLink(value: section) {
                    Label(section.rawValue, systemImage: section.symbol)
                        .foregroundStyle(section.isImplemented ? .primary : .secondary)
                }
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 260)
        } detail: {
            switch selection {
            case .capture, nil:
                CaptureView(
                    recorder: environment.recorder,
                    inputs: environment.inputs,
                    settings: environment.settings,
                    // The seam carries no capture-type channel, so the
                    // composition root is the only place that can tell the
                    // recorder what the picker just chose.
                    onCaptureTypeChanged: { environment.recorder.currentType = $0 })
            case .library:
                LibraryView()
            case .timeline:
                ArchiveTimelineView()
            case .eras:
                ErasView()
            case .search:
                SearchView()
            case .review:
                ReviewView()
            case .people:
                PeopleView()
            case .some(let section):
                NotBuiltYetView(section: section)
            }
        }
    }
}

/// Placeholder for a sidebar section that has not been ported yet. Names the
/// section and points at the plan instead of showing a blank pane.
struct NotBuiltYetView: View {
    let section: RootView.Section

    var body: some View {
        ContentUnavailableView {
            Label(section.rawValue, systemImage: section.symbol)
        } description: {
            Text("Not ported yet. This section exists on Windows; see MACOS-PORT-PLAN.md for where it sits in the order of work.")
        }
    }
}
