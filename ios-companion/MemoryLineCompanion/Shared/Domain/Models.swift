import Foundation

/// Capture lifecycle states (design §8.4). Raw values are the snake_case strings
/// persisted in SQLite; `processing_remote`/`review_ready`/`completed` arrive from
/// the Windows side in Phase 3 status sync and exist now so the schema is stable.
enum CaptureLifecycleState: String, Codable, CaseIterable, Sendable {
    case recording
    case savedLocal = "saved_local"
    case queuedUpload = "queued_upload"
    case uploading
    case uploaded
    case processingRemote = "processing_remote"
    case reviewReady = "review_ready"
    case completed
    case failedRecoverable = "failed_recoverable"
    case cancelled

    /// States eligible for the upload pipeline to (re)try.
    var isUploadPending: Bool {
        switch self {
        case .savedLocal, .queuedUpload, .failedRecoverable: return true
        default: return false
        }
    }

    var isDoneUploading: Bool {
        switch self {
        case .uploaded, .processingRemote, .reviewReady, .completed: return true
        default: return false
        }
    }
}

/// Capture classification (design §6.1). Raw values match the wire contract.
enum CaptureType: String, Codable, CaseIterable, Identifiable, Sendable {
    case memory
    case idea
    case tripLog = "trip_log"
    case reminder
    case question
    case unclassified

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .memory: return "Memory"
        case .idea: return "Idea"
        case .tripLog: return "Trip log"
        case .reminder: return "Reminder"
        case .question: return "Question"
        case .unclassified: return "Unclassified"
        }
    }

    var symbolName: String {
        switch self {
        case .memory: return "brain.head.profile"
        case .idea: return "lightbulb"
        case .tripLog: return "car"
        case .reminder: return "bell"
        case .question: return "questionmark.circle"
        case .unclassified: return "waveform"
        }
    }
}

/// One recorded capture and its local pipeline state. Mirrors the `captures`
/// SQLite table one-to-one; every mutation is persisted before the UI reports it
/// (design §16.1).
struct CaptureRecord: Identifiable, Equatable, Sendable {
    /// Globally unique, device-generated capture ID (lowercased UUID string).
    let id: String
    var type: CaptureType
    var state: CaptureLifecycleState
    var capturedAt: Date
    var timezoneId: String
    var localOffsetMinutes: Int
    var titleHint: String?
    var userNote: String?
    /// Audio file name inside the app's Recordings directory (never a full path —
    /// the container path changes between installs).
    var fileName: String
    /// Finalized size in bytes; 0 until the recording is finalized.
    var byteLength: Int64
    /// Lowercase hex SHA-256 of the finalized audio; empty until finalized.
    var sha256: String
    var durationSeconds: Double
    var createdAt: Date
    var uploadAttempts: Int
    var lastError: String?
    /// Artifact ID minted for the upload (stable across retries).
    var artifactId: String?
    var uploadedAt: Date?
    /// The single location snapshot taken at capture start when the location
    /// opt-in is on (design §14.4); nil otherwise, and always nil on the Mac.
    /// Persisted in the `capture_location` side table, not a `captures`
    /// column, so that table's positional column mapping and `user_version`
    /// stay as they were. Defaulted so existing memberwise-init callers compile.
    var location: LocationContext? = nil

    static func newLocal(
        type: CaptureType,
        fileName: String,
        capturedAt: Date = Date(),
        calendar: Calendar = .current,
        timeZone: TimeZone = .current
    ) -> CaptureRecord {
        CaptureRecord(
            id: UUID().uuidString.lowercased(),
            type: type,
            state: .recording,
            capturedAt: capturedAt,
            timezoneId: timeZone.identifier,
            localOffsetMinutes: timeZone.secondsFromGMT(for: capturedAt) / 60,
            titleHint: nil,
            userNote: nil,
            fileName: fileName,
            byteLength: 0,
            sha256: "",
            durationSeconds: 0,
            createdAt: Date(),
            uploadAttempts: 0,
            lastError: nil,
            artifactId: nil,
            uploadedAt: nil
        )
    }
}

/// Setting keys persisted in the local settings table. String-typed unless noted.
enum AppSettingsKey {
    static let serverURL = "server_url"
    static let deviceId = "device_id"
    static let deviceDisplayName = "device_display_name"
    static let syncEnabled = "sync_enabled"                      // bool
    static let autoUpload = "auto_upload"                        // bool, default true
    static let spokenConfirmation = "spoken_confirmation"        // bool, default true
    static let defaultCaptureType = "default_capture_type"
    static let onDeviceTranscription = "on_device_transcription" // bool, Phase 4 placeholder (design §22.4)
    static let captureLocation = "capture_location"              // bool, default false — opt-in snapshot (design §14.4)
    static let captureLocationPrecise = "capture_location_precise" // bool, default false — coarse snapshot unless on
}

/// Contract between the app and the widget extension. The widget process cannot
/// import app code, so BOTH sides hard-code these values; keep them in sync with
/// MemoryLineWidgets/WidgetSharedState.swift.
enum WidgetSharedState {
    static let appGroupId = "group.ca.fluxology.memoryline.ios"
    /// String: "idle" | "recording" | "paused".
    static let recordingStateKey = "widget.recordingState"
    /// Int: captures waiting to reach the sync service.
    static let pendingUploadsKey = "widget.pendingUploads"
    /// Double: epoch seconds of the most recent finalized capture.
    static let lastCaptureAtKey = "widget.lastCaptureAt"
}

/// BGTaskScheduler identifier registered in Info.plist for deferred upload retries.
enum BackgroundTaskId {
    static let uploadRetry = "com.memoryline.companion.upload"
}
