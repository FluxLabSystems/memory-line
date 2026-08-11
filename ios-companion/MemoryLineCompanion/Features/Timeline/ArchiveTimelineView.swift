import SwiftUI

/// The Windows archive as this phone has been sent it: events grouped by year,
/// searchable, with a detail push.
///
/// **Read-only, by design rather than by omission.** Windows owns the archive
/// and is the only editing surface (design §19). What this renders is a
/// projection Windows publishes and `TimelineProjectionApplier` applies. There
/// is no merge policy here because there is nothing to merge.
///
/// **Not shared with the Mac's screen of the same name**, and deliberately so.
/// The data layer underneath — models, store, applier — is shared source in
/// `Shared/Timeline/`, and that is the part where a second implementation would
/// drift. The views are not: the Mac uses a split view with an inspector pane,
/// the phone a navigation stack that pushes a detail. Sharing those would mean a
/// single view carrying both idioms behind availability checks, which reads
/// worse than two files that each say one thing.
///
/// Two rules this follows that are easy to get wrong, both enforced by the
/// shared layer's own documentation:
///
///  - **`displayDate` is rendered, never re-derived.** Windows formats it with
///    the same code its own timeline uses, honouring a date's precision, so a
///    memory the user placed in a summer stays a summer. Formatting `startDate`
///    locally would invent a precise day for it.
///  - **Grouping uses a UTC calendar** (`TimelineCalendar`), because these are
///    calendar dates pinned to UTC midnight, not instants. The device calendar
///    would file a 14 July memory under 13 July anywhere west of Greenwich.
@MainActor
struct ArchiveTimelineView: View {
    @Environment(AppEnvironment.self) private var env

    @State private var years: [YearGroup] = []
    @State private var eras: [String: EraProjectionPayload] = [:]
    @State private var loadError: String?
    @State private var search = ""

    /// Events sharing a calendar year, newest year first.
    private struct YearGroup: Identifiable {
        let year: Int
        let events: [EventProjectionPayload]
        var id: Int { year }
    }

    var body: some View {
        NavigationStack {
            Group {
                if let loadError {
                    ContentUnavailableView {
                        Label("Could not read the timeline", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(loadError)
                    } actions: {
                        Button("Try Again") { load() }
                    }
                } else if years.isEmpty {
                    emptyState
                } else if visibleYears.isEmpty {
                    ContentUnavailableView.search(text: search)
                } else {
                    List {
                        ForEach(visibleYears) { group in
                            Section(String(group.year)) {
                                ForEach(group.events, id: \.eventId) { event in
                                    NavigationLink {
                                        ArchiveEventDetailView(
                                            event: event, era: event.eraId.flatMap { eras[$0] })
                                    } label: {
                                        ArchiveEventRow(
                                            event: event, era: event.eraId.flatMap { eras[$0] })
                                    }
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("Timeline")
            .searchable(text: $search, prompt: "Search memories")
            .refreshable {
                await env.statusSync.pullNow()
                load()
            }
            .task { load() }
            // A completed pull may have applied new projections.
            .onChange(of: env.statusSync.lastPulledAt) { _, _ in load() }
        }
    }

    /// Empty means one of three quite different things, and saying which is the
    /// difference between a user waiting and a user filing a bug: not paired,
    /// paired but nothing pulled yet, or Windows genuinely has no events.
    private var emptyState: some View {
        ContentUnavailableView {
            Label("No timeline yet", systemImage: "calendar.day.timeline.left")
        } description: {
            if !env.isPaired {
                Text("Pair with your sync server in Settings to see the timeline from your Windows archive.")
            } else if env.statusSync.lastPulledAt == nil {
                Text("Paired, but nothing has synced yet. Pull down to sync.")
            } else {
                Text("Your Windows archive has not published any events. Memories are added and edited on Windows; this phone shows a copy.")
            }
        }
    }

    /// Search filters within the loaded groups rather than re-querying.
    ///
    /// The projection is one person's archive, bounded by a life rather than by
    /// a query, so it is already in memory; and the fields worth searching —
    /// tags, people, locations — live in JSON text columns, where SQL could only
    /// `LIKE` inside them and would match a tag against a location without
    /// knowing it did. `localizedStandardContains` also folds case and
    /// diacritics the way someone typing into a box expects, which SQLite's
    /// `LIKE` does not do outside ASCII.
    ///
    /// Transcripts are not searched and cannot be: they stay on Windows (§14.5).
    /// The no-results state says so rather than letting an empty list read as
    /// "you have no such memory".
    private var visibleYears: [YearGroup] {
        let term = search.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return years }
        return years.compactMap { group in
            let matched = group.events.filter { matches($0, term: term) }
            return matched.isEmpty ? nil : YearGroup(year: group.year, events: matched)
        }
    }

    private func matches(_ event: EventProjectionPayload, term: String) -> Bool {
        event.title.localizedStandardContains(term)
            || event.description?.localizedStandardContains(term) == true
            || event.category?.localizedStandardContains(term) == true
            || event.displayDate?.localizedStandardContains(term) == true
            || event.tags.contains { $0.localizedStandardContains(term) }
            || event.locations.contains { $0.localizedStandardContains(term) }
    }

    private func load() {
        do {
            // The whole projection: a copy of one archive, bounded by a life
            // rather than by a query, so paging it would add a cursor and a
            // scroll-position problem to save an amount of memory that a single
            // capture's audio dwarfs.
            let events = try env.projections.events(from: .distantPast, to: .distantFuture)
            eras = Dictionary(
                try env.projections.allEras().map { ($0.eraId, $0) },
                uniquingKeysWith: { first, _ in first })

            years = Dictionary(grouping: events, by: { TimelineCalendar.year(of: $0.startDate) })
                // Newest first: the store returns ascending because a range query
                // should, but a timeline someone opens is read from the recent
                // end. Within a year the store's order is kept.
                .sorted { $0.key > $1.key }
                .map { YearGroup(year: $0.key, events: $0.value) }
            loadError = nil
        } catch {
            loadError = String(describing: error)
        }
    }
}

// MARK: - Row

private struct ArchiveEventRow: View {
    let event: EventProjectionPayload
    let era: EraProjectionPayload?

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            // The era as a colour spine rather than a section: eras overlap in
            // time and an event may have none, so grouping by era would need an
            // arbitrary rule for both cases. A stripe carries the same
            // information without inventing one.
            RoundedRectangle(cornerRadius: 2)
                .fill(eraColor)
                .frame(width: 3)
                .frame(maxHeight: .infinity)

            VStack(alignment: .leading, spacing: 3) {
                Text(event.title)
                    .font(.body)

                HStack(spacing: 6) {
                    // Windows' own precision-honest string.
                    Text(event.displayDate ?? "Date unknown")
                    if let category = event.category, !category.isEmpty {
                        Text(category)
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)

                if !event.tags.isEmpty {
                    Text(event.tags.joined(separator: " · "))
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: 4)

            // Counts, not content: media lives on Windows and is fetched
            // through the artifact endpoints, and people are ids until the
            // person projection resolves them.
            VStack(alignment: .trailing, spacing: 2) {
                if event.mediaCount > 0 {
                    Label("\(event.mediaCount)", systemImage: "photo")
                }
                if !event.personIds.isEmpty {
                    Label("\(event.personIds.count)", systemImage: "person.2")
                }
            }
            .font(.caption2)
            .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }

    /// The era's colour, or a neutral when the event has no era or the payload
    /// carried something unparseable. Never a guess at what was meant.
    private var eraColor: Color {
        era.flatMap { Color(projectionHex: $0.colorCode) } ?? .secondary.opacity(0.3)
    }
}

// MARK: - Detail

/// Everything the projection carries for one event — which is the whole of what
/// crossed. There is no "fetch the rest from Windows" call, because a projection
/// is not a summary of something richer available on demand.
private struct ArchiveEventDetailView: View {
    let event: EventProjectionPayload
    let era: EraProjectionPayload?

    var body: some View {
        List {
            Section {
                LabeledContent("When", value: event.displayDate ?? "Date unknown")
                if let category = event.category, !category.isEmpty {
                    LabeledContent("Category", value: category)
                }
                if let era {
                    LabeledContent("Era", value: era.name)
                }
                if event.mediaCount > 0 {
                    LabeledContent(
                        "Media",
                        value: event.mediaCount == 1 ? "1 item" : "\(event.mediaCount) items")
                }
            }

            if let description = event.description, !description.isEmpty {
                Section("Description") {
                    Text(description)
                }
            }

            if !event.locations.isEmpty {
                Section("Places") {
                    ForEach(event.locations, id: \.self) { Text($0) }
                }
            }

            if !event.tags.isEmpty {
                Section("Tags") {
                    Text(event.tags.joined(separator: ", "))
                }
            }

            Section {
                Text("Memories are added and edited on Windows. This phone shows a copy.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
        .navigationTitle(event.title)
        .navigationBarTitleDisplayMode(.inline)
    }
}
