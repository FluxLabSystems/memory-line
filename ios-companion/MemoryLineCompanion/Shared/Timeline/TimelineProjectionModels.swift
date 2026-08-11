import Foundation

/// Swift mirrors of the read-only timeline projections in
/// `shared-contracts/dotnet/MemoryTimeline.SyncContracts/TimelineProjectionContracts.cs`.
///
/// These are the `payloadJson` bodies of `event`, `era`, `person` and
/// `pending_event` sync changes. They decode through `SyncJSON.decoder`
/// (`Shared/Networking/DTOs.swift`): the wire is camelCase with no key strategy,
/// so the property names below *are* the wire names, and the timestamps are
/// ISO 8601 UTC with a variable number of fractional-second digits, which only
/// that decoder's custom date strategy accepts. Decoding one of these with a
/// stock `JSONDecoder` fails on every date in the payload.
///
/// **Direction.** Windows publishes, this device consumes. There is no merge policy
/// in this file and none is needed — a companion rendering a timeline from these
/// payloads holds a copy, not shared ownership. The only reconciliation is
/// last-write-wins on `updatedAtUtc`, which `TimelineProjectionApplier` applies.
///
/// **Optionality is the contract.** Every C# `?` is a Swift optional here and
/// every non-nullable C# property is non-optional, literally and deliberately.
/// The consequence is that a payload missing a required key fails to decode
/// rather than materialising a default: an event silently drawn at the Unix
/// epoch because `startDate` was absent is a far worse outcome than a change the
/// applier skips and logs by id, and Windows can always republish. The same
/// reasoning applies to the list fields, which are non-nullable C# collections
/// defaulting to empty — `System.Text.Json` writes them as `[]`, so an absent
/// key is a publisher bug, not a shorthand for "no tags".
///
/// **Privacy (§14.5).** These carry what a user reads on a timeline: titles,
/// dates, categories, names, and a bounded transcript excerpt. Nothing in this
/// file is ever logged; the applier and the store log ids and counts only.
///
/// **Not mirrored here:** `PendingEventDecisionPayload`. It is the one payload
/// that travels *toward* Windows, so it belongs with the outbox that authors it
/// rather than with the projections a companion consumes. The macOS app declares
/// it beside its review queue; anything that grows a second review surface should
/// move that declaration here rather than write a second one.

/// One archived event, denormalised for drawing.
///
/// `tags`, `personIds` and `locations` arrive inline rather than as references
/// to junction rows. That is the contract's own choice, argued in
/// `TimelineProjectionContracts.cs`: a consumer of a read-only projection wants
/// to draw a bubble, not reconstruct a relational graph, and syncing junction
/// tables would triple the change volume to reproduce a join the consumer
/// performs immediately anyway. `TimelineProjectionStore` keeps them in the
/// shape they arrive in; the storage note there explains why.
struct EventProjectionPayload: Codable, Equatable, Sendable {
    var eventId: String

    var title: String

    /// Trimmed to a readable length by the publisher — never the archive's full text.
    var description: String?

    var startDate: Date

    var endDate: Date?

    /// How much of `startDate` to believe: exact, day, month, season, year,
    /// decade, or unknown. Carried rather than derived, because getting this
    /// wrong is how a companion ends up inventing a precise day for a memory the
    /// user only dated to a summer. Stored as received so an unrecognised future
    /// value survives instead of collapsing to a default.
    var datePrecision: String = "day"

    /// Precision-honest display string ("Summer 2003"), formatted by Windows so
    /// every client renders exactly what the owning app would rather than
    /// reimplementing the rules and drifting.
    var displayDate: String?

    var earliestPossible: Date?

    var latestPossible: Date?

    var category: String?

    var eraId: String?

    /// Tag names, already resolved.
    var tags: [String] = []

    /// Linked person ids; names come from `PersonProjectionPayload`.
    var personIds: [String] = []

    /// Location display names, already resolved.
    var locations: [String] = []

    /// Optional coordinates for the map, when the event has a located place.
    var latitude: Double?

    var longitude: Double?

    /// How many media items are attached, so a badge can be drawn without
    /// fetching any of them.
    var mediaCount: Int = 0

    /// When Windows produced this projection. The last-write-wins key — not
    /// `changeId` and not `revision`, both of which are the feed's bookkeeping
    /// rather than the archive's.
    var updatedAtUtc: Date
}

/// A life period, projected for timeline backgrounds and the eras list.
struct EraProjectionPayload: Codable, Equatable, Sendable {
    var eraId: String

    var name: String

    var subtitle: String?

    var startDate: Date

    /// Nil for an ongoing era.
    var endDate: Date?

    /// `"#RRGGBB"`. Colours cross the wire as strings; the client builds its own
    /// colour value, which is also why this file needs no AppKit.
    var colorCode: String = "#808080"

    var categoryId: String?

    var categoryName: String?

    var displayOrder: Int = 0

    var updatedAtUtc: Date
}

/// A person from the contact book, projected for the People hub and for
/// resolving `EventProjectionPayload.personIds`.
struct PersonProjectionPayload: Codable, Equatable, Sendable {
    var personId: String

    var name: String

    var nickname: String?

    var relationship: String?

    /// When set, this person was merged into another and this id is a tombstone.
    /// The UI must follow it rather than show a duplicate; old events keep
    /// pointing at the merged-away id on purpose, which is precisely why the
    /// store keeps the row instead of dropping it.
    var mergedIntoId: String?

    var isFavorite: Bool = false

    /// Events this person appears in, so the hub can sort without scanning the
    /// event table.
    var eventCount: Int = 0

    var updatedAtUtc: Date
}

/// An extracted event awaiting review, projected so a companion can show the
/// queue. The verdict itself is a different entity travelling the other way —
/// see `SyncChangeEntityType.pendingEventDecision`.
struct PendingEventProjectionPayload: Codable, Equatable, Sendable {
    var pendingEventId: String

    /// Capture this was extracted from, so the UI can link back to it.
    var captureId: String?

    var title: String

    var description: String?

    var startDate: Date

    var endDate: Date?

    var datePrecision: String = "day"

    var displayDate: String?

    var category: String?

    /// Extraction confidence, 0..1, so the reviewer can triage.
    var confidenceScore: Double = 0

    var tags: [String] = []

    /// People the extraction found, by name — they may not exist in the contact
    /// book yet, which is why these are names and not ids.
    var peopleNames: [String] = []

    /// Bounded excerpt of the source transcript (§14.5), never the whole thing,
    /// and never logged.
    var transcriptPreview: String?

    var updatedAtUtc: Date
}

// The `entityType` and `operation` constants this feature reads used to live
// here, in the macOS target, with a note to move them into `DTOs.swift` the day
// the phone grew a timeline. They now live there — see `SyncChangeEntityType`
// and `SyncOperation` — because this file compiles into both apps and a second
// declaration would be a redeclaration rather than a convenience.
