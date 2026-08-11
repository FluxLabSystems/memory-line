import Foundation

/// Wire DTOs for the Memory Line Sync API v1. These mirror
/// shared-contracts/dotnet/MemoryTimeline.SyncContracts (the contract source the
/// service and Windows client compile against) and the OpenAPI document
/// shared-contracts/openapi/memory-line-sync-v1.yaml. JSON is camelCase, so the
/// property names below ARE the wire names — no key strategy is applied.

// MARK: - Devices

struct DeviceRegisterRequest: Codable, Sendable {
    var pairingCode: String
    var platform: String = "ios"
    var displayName: String
    var appVersion: String?
    var publicKey: String?
}

struct DeviceRegisterResponse: Codable, Sendable {
    var deviceId: String
    var ownerId: String
    var accessToken: String
    var accessTokenExpiresAtUtc: Date
    var refreshToken: String
}

struct TokenRefreshRequest: Codable, Sendable {
    var refreshToken: String
}

struct TokenRefreshResponse: Codable, Sendable {
    var accessToken: String
    var accessTokenExpiresAtUtc: Date
    var refreshToken: String
}

// MARK: - Captures

struct LocationContext: Codable, Sendable {
    var latitude: Double
    var longitude: Double
    var horizontalAccuracyM: Double?
    var headingDegrees: Double?
    var speedMps: Double?
}

struct CaptureCreateRequest: Codable, Sendable {
    var captureId: String
    var sourceDeviceId: String
    var captureType: String
    var capturedAtUtc: Date
    var timezoneId: String?
    var localOffsetMinutes: Int?
    var tripId: String?
    var titleHint: String?
    var userNote: String?
    var locationContext: LocationContext?
    var clientSchemaVersion: Int = 1
}

struct CaptureResponse: Codable, Sendable {
    var captureId: String
    var sourceDeviceId: String
    var sourcePlatform: String
    var captureType: String
    var capturedAtUtc: Date
    var status: String
    var serverRevision: Int64
    var createdAtUtc: Date
    var artifacts: [ArtifactSummary]?
}

// MARK: - Artifacts

struct ArtifactInitiateRequest: Codable, Sendable {
    var artifactId: String?
    var artifactType: String = "audio_original"
    var mediaType: String?
    var originalFileName: String?
    var expectedByteLength: Int64
    /// Lowercase hex SHA-256 of the complete artifact bytes.
    var expectedSha256: String
}

struct ArtifactInitiateResponse: Codable, Sendable {
    var artifactId: String
    var partSizeBytes: Int64
    var expectedPartCount: Int
}

struct ArtifactCompleteRequest: Codable, Sendable {
    var partCount: Int
    var totalByteLength: Int64
    var sha256: String
}

struct ArtifactCompleteResponse: Codable, Sendable {
    var artifactId: String
    var state: String
    var byteLength: Int64
    var sha256: String
}

struct ArtifactSummary: Codable, Sendable {
    var artifactId: String
    var artifactType: String
    var mediaType: String?
    var originalFileName: String?
    var byteLength: Int64?
    var sha256: String?
    var state: String
}

// MARK: - Sync

struct SyncChangeDto: Codable, Sendable {
    var changeId: Int64
    var entityType: String
    var entityId: String
    var operation: String
    var revision: Int64
    var changedAtUtc: Date
    var sourceDeviceId: String?
    var payloadJson: String?
}

struct SyncPullResponse: Codable, Sendable {
    var changes: [SyncChangeDto]
    var nextCursor: Int64
    var hasMore: Bool
}

struct SyncAckRequest: Codable, Sendable {
    var cursor: Int64
}

// MARK: - Sync push

/// `POST /api/v1/sync/push`. Mirrors `SyncPushRequest` in
/// `shared-contracts/dotnet/MemoryTimeline.SyncContracts/SyncChangeContracts.cs`.
///
/// Declared here in `Shared/` rather than in the macOS target because it is the
/// wire contract, not one client's feature — but note that today only the Mac
/// pushes, and only `pending_event_decision`. The phone reaches the server
/// through the capture and artifact endpoints instead, which is why `SyncAPI`
/// (the protocol both apps' fakes conform to) deliberately does **not** carry
/// `push`: adding a requirement there would oblige every existing conformer,
/// including the iOS test doubles, to implement a call it never makes.
struct SyncPushRequest: Codable, Sendable {
    var entries: [SyncPushEntry]
}

struct SyncPushEntry: Codable, Sendable {
    /// Client-local outbox sequence. This is what makes a push idempotent: the
    /// server keys a receipt on (device, clientSequence), so a retry after a
    /// response that never arrived is recognised as the same entry rather than
    /// applied twice. It must therefore be allocated once, persisted with the
    /// outbox row, and reused unchanged across every retry of that row.
    var clientSequence: Int64
    var entityType: String
    var entityId: String
    var operation: String
    var payloadJson: String?
    var changedAtUtc: Date
}

struct SyncPushResponse: Codable, Sendable {
    var results: [SyncPushEntryResult]
}

struct SyncPushEntryResult: Codable, Sendable {
    var clientSequence: Int64
    var accepted: Bool
    /// True when an earlier push already applied this entry. A duplicate is a
    /// success — it means the server has what the client was trying to send —
    /// so it must retire the outbox row rather than trigger a retry.
    var duplicate: Bool
    var serverChangeId: Int64?
    var error: String?
}

// MARK: - Errors

struct ApiError: Codable, Sendable {
    var code: String
    var message: String
    var retryable: Bool
    var correlationId: String?
}

// MARK: - JSON coding

/// Shared JSON coding for the sync wire format. The service (System.Text.Json)
/// emits ISO 8601 UTC timestamps with a variable number of fractional-second
/// digits, so decoding must tolerate both fractional and whole-second forms.
enum SyncJSON {
    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, coderEncoder in
            var container = coderEncoder.singleValueContainer()
            try container.encode(fractionalFormatter.string(from: date))
        }
        return encoder
    }()

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { coderDecoder in
            let container = try coderDecoder.singleValueContainer()
            let raw = try container.decode(String.self)
            if let date = fractionalFormatter.date(from: raw) ?? wholeSecondFormatter.date(from: raw) {
                return date
            }
            throw DecodingError.dataCorruptedError(
                in: container, debugDescription: "Unrecognized ISO 8601 date: \(raw)")
        }
        return decoder
    }()

    private static let fractionalFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    private static let wholeSecondFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter
    }()
}

// MARK: - Capture processing status (Phase 3)

/// Swift mirror of `CaptureStatusChangePayload` in
/// shared-contracts/dotnet/MemoryTimeline.SyncContracts/CaptureStatusContracts.cs.
/// Carried as the `payloadJson` of a `capture_status` sync change, authored by
/// Windows and consumed here so the phone can show the full lifecycle. Editing
/// and approval remain on Windows, so this is a read-only projection.
struct CaptureStatusChangePayload: Codable, Sendable {
    var captureId: String
    var status: String
    var processingStage: String?
    var updatedAtUtc: Date
    var transcriptAvailable: Bool
    /// Leading excerpt only — the full transcript stays on Windows.
    var transcriptPreview: String?
    var transcriptCharCount: Int?
    var pendingEventCount: Int?
    var approvedEventCount: Int?
    var failureReason: String?
    var failureRetryable: Bool?
}

/// Wire values of `SyncCaptureStatus` (CaptureContracts.cs).
enum SyncCaptureStatus {
    static let localOnly = "local_only"
    static let uploading = "uploading"
    static let received = "received"
    static let processing = "processing"
    static let reviewReady = "review_ready"
    static let completed = "completed"
    static let failed = "failed"
}

/// Entity-type discriminators on `SyncChangeDto.entityType`.
enum SyncChangeEntityType {
    static let capture = "capture"
    static let captureArtifact = "capture_artifact"
    static let captureStatus = "capture_status"

    // The read-only timeline projection Windows publishes. These lived in the
    // macOS target while it was the only app with a timeline, with a note saying
    // to move them here the day the phone grew one. That day arrived; an
    // extension in one app's target would have given the type members the other
    // app did not have.
    static let event = "event"
    static let era = "era"
    static let person = "person"
    static let pendingEvent = "pending_event"

    /// A companion's approve/reject verdict on a pending event — the one entity
    /// a non-Windows device authors. Deliberately absent from
    /// <doc:projectedByWindows>: seeing it on the pull feed means reading back a
    /// decision some device sent, and applying it as a projection would fabricate
    /// a pending event out of the answer to one.
    static let pendingEventDecision = "pending_event_decision"

    /// The entity types `TimelineProjectionApplier` writes to the local store.
    static let projectedByWindows: Set<String> = [event, era, person, pendingEvent]
}

/// `operation` values on a sync change, mirroring `SyncOperation` in
/// `shared-contracts/dotnet/MemoryTimeline.SyncContracts/SyncChangeContracts.cs`.
///
/// Not needed until the projection arrived: `capture_status` changes are only
/// ever upserts, so nothing had to distinguish them.
enum SyncOperation {
    static let upsert = "upsert"
    static let delete = "delete"
}
