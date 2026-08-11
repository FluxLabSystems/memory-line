import SwiftUI

/// The life periods the archive is organised into, and the memories inside each.
///
/// Eras are the one projected entity that is *browsed* rather than merely
/// referenced: on the timeline an era is a colour on the side of a row, but it
/// is also how someone navigates a life — "university", "the Halifax years".
/// This is that surface.
///
/// Read-only, like every projection screen. Eras are created and edited on
/// Windows; the ordering, colours and category here are what Windows chose.
struct ErasView: View {
    @Environment(MacAppEnvironment.self) private var environment

    @State private var eras: [EraProjectionPayload] = []
    /// Events grouped by era id, so a row can show a count and a selection can
    /// show the memories without a second query per era.
    @State private var eventsByEra: [String: [EventProjectionPayload]] = [:]
    /// Events with no era at all. Kept separate rather than dropped: an archive
    /// where half the memories are unfiled is a normal archive, and hiding them
    /// behind a screen that claims to organise everything would be a lie.
    @State private var unfiled: [EventProjectionPayload] = []
    @State private var loadError: String?
    @State private var selectedEraId: String?

    var body: some View {
        Group {
            if let loadError {
                ContentUnavailableView {
                    Label("Could not read the eras", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(loadError)
                } actions: {
                    Button("Try Again") { load() }
                }
            } else if eras.isEmpty && unfiled.isEmpty {
                ContentUnavailableView {
                    Label("No eras yet", systemImage: "square.stack.3d.down.right")
                } description: {
                    Text(environment.isPaired
                         ? "Eras from your Windows archive appear here once they sync. They are the life periods your timeline is grouped into."
                         : "Pair this Mac with your sync server in Settings to see the eras in your archive.")
                }
            } else {
                HSplitView {
                    eraList
                        .frame(minWidth: 240, idealWidth: 280)
                    eraDetail
                        .frame(minWidth: 320)
                }
            }
        }
        .navigationTitle("Eras")
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
            }
        }
        .task { load() }
        .onChange(of: environment.sync.lastPulledAt) { _, _ in load() }
    }

    // MARK: - List

    private var eraList: some View {
        List(selection: $selectedEraId) {
            ForEach(eras, id: \.eraId) { era in
                EraRow(era: era, eventCount: eventsByEra[era.eraId]?.count ?? 0)
            }
            if !unfiled.isEmpty {
                // A synthetic row, and the only one whose id is not an era id.
                // Using a sentinel rather than an optional selection keeps the
                // list's selection type a plain String.
                EraRow.unfiled(count: unfiled.count)
                    .tag(Self.unfiledId)
            }
        }
        .listStyle(.sidebar)
    }

    /// Sentinel for the "no era" row. A UUID rather than a readable string so it
    /// can never collide with a real era id arriving from Windows.
    private static let unfiledId = "00000000-unfiled-0000-000000000000"

    // MARK: - Detail

    @ViewBuilder
    private var eraDetail: some View {
        if selectedEraId == Self.unfiledId {
            EraEventList(
                title: "Not in an era",
                subtitle: "Memories Windows has not filed under any life period.",
                accent: .secondary,
                events: unfiled)
        } else if let id = selectedEraId, let era = eras.first(where: { $0.eraId == id }) {
            EraEventList(
                title: era.name,
                subtitle: era.subtitle ?? era.span,
                accent: Color(projectionHex: era.colorCode) ?? .secondary,
                events: eventsByEra[id] ?? [])
        } else {
            ContentUnavailableView(
                "Select an era",
                systemImage: "square.stack.3d.down.right",
                description: Text("Pick a life period to see the memories filed under it."))
        }
    }

    private func load() {
        do {
            // Publisher order, not alphabetical: `displayOrder` is the sequence
            // the user arranged on Windows, and re-sorting it here would quietly
            // override a choice they made.
            eras = try environment.projections.allEras()

            let events = try environment.projections.events(from: .distantPast, to: .distantFuture)
            eventsByEra = Dictionary(grouping: events.filter { $0.eraId != nil }, by: { $0.eraId! })
            unfiled = events.filter { $0.eraId == nil }
            loadError = nil
        } catch {
            loadError = String(describing: error)
        }
    }
}

// MARK: - Rows

private struct EraRow: View {
    let era: EraProjectionPayload?
    let eventCount: Int
    let placeholderName: String?

    init(era: EraProjectionPayload, eventCount: Int) {
        self.era = era
        self.eventCount = eventCount
        self.placeholderName = nil
    }

    private init(placeholderName: String, eventCount: Int) {
        self.era = nil
        self.eventCount = eventCount
        self.placeholderName = placeholderName
    }

    static func unfiled(count: Int) -> EraRow {
        EraRow(placeholderName: "Not in an era", eventCount: count)
    }

    var body: some View {
        HStack(spacing: 10) {
            RoundedRectangle(cornerRadius: 3)
                .fill(swatch)
                .frame(width: 10, height: 22)

            VStack(alignment: .leading, spacing: 1) {
                Text(era?.name ?? placeholderName ?? "")
                if let detail {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Text("\(eventCount)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }

    private var detail: String? {
        guard let era else { return nil }
        if let subtitle = era.subtitle, !subtitle.isEmpty { return subtitle }
        return era.span
    }

    /// Unparseable colours fall back to a neutral rather than a guess. See the
    /// ARGB note on `Color(projectionHex:)`.
    private var swatch: Color {
        guard let era else { return .secondary.opacity(0.35) }
        return Color(projectionHex: era.colorCode) ?? .secondary.opacity(0.35)
    }
}

private struct EraEventList: View {
    let title: String
    let subtitle: String
    let accent: Color
    let events: [EventProjectionPayload]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    RoundedRectangle(cornerRadius: 3)
                        .fill(accent)
                        .frame(width: 6, height: 22)
                    Text(title).font(.title3.weight(.semibold))
                }
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 8)

            Divider()

            if events.isEmpty {
                ContentUnavailableView(
                    "No memories here yet",
                    systemImage: "calendar",
                    description: Text("Nothing in the synced copy is filed under this period."))
            } else {
                List(events, id: \.eventId) { event in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(event.title)
                        HStack(spacing: 6) {
                            // Windows' precision-honest string, as everywhere.
                            Text(event.displayDate ?? "Date unknown")
                            if let category = event.category, !category.isEmpty {
                                Text(category)
                            }
                        }
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 2)
                }
                .listStyle(.inset)
            }
        }
    }
}

/// `private`, not internal, and that is the lesson from c1e966f rather than a
/// style preference. `EraProjectionPayload` lives in `Shared/`, which compiles
/// into both apps; an internal extension written in one target would give the
/// type a member the other app does not have, and would turn into a
/// redeclaration the day the same helper is added to `Shared/` properly. If the
/// phone grows an eras screen, move this there — do not widen it here.
private extension EraProjectionPayload {
    /// "2001 – 2005", or "2001 – now" for an era that has not ended.
    ///
    /// Years only, and from a UTC calendar. An era is a life period, not an
    /// instant: the wire carries its bounds as dates pinned to UTC midnight (see
    /// `TimelineProjectionPublisher.Utc`), so reading them back in the device's
    /// calendar could move a January boundary into the previous year.
    var span: String {
        let start = TimelineCalendar.year(of: startDate)
        guard let endDate else { return "\(start) – now" }
        let end = TimelineCalendar.year(of: endDate)
        return start == end ? "\(start)" : "\(start) – \(end)"
    }
}
