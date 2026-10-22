import SwiftUI

/// Top-level navigation. The sidebar sections mirror the Windows app's
/// navigation so the two heads stay conceptually aligned.
///
/// Capture and Library are this Mac's own: it records, and it holds the
/// recordings it made. Timeline, Eras, Search, Review and People render the read-only
/// projection Windows publishes — this Mac shows a copy of that archive and
/// never edits it, with one exception: Review sends approve/reject verdicts,
/// the single write the contract admits from a companion. Ask is a thin client
/// over the service's assistant endpoints: the question goes to the Windows PC
/// over the sync service and the answer comes back the same way, with the pane
/// saying plainly while it is still waiting (the shared `AskCoordinator`).
///
/// See `docs/design/MACOS-PORT-PLAN.md` §5 for how each section was filled in.
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
    }

    var body: some View {
        NavigationSplitView {
            List(Section.allCases, selection: $selection) { section in
                NavigationLink(value: section) {
                    Label(section.rawValue, systemImage: section.symbol)
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
            case .ask:
                AskView()
            }
        }
    }
}
