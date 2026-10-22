using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MemoryTimeline.Data.Models;

/// <summary>
/// Represents a capture source in the processing queue. Historically this was
/// always an audio recording; it now also covers pasted/imported text sources
/// (see <see cref="SourceType"/>) which skip speech-to-text entirely.
/// </summary>
[Table("recording_queue")]
public class RecordingQueue
{
    [Key]
    [Column("queue_id")]
    public string QueueId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path to the recorded audio file. CONVENTION: non-audio sources
    /// (<see cref="SourceType"/> != <see cref="QueueSourceType.Audio"/>) store
    /// <see cref="string.Empty"/> here — the column is NOT NULL on existing
    /// databases and SQLite's ALTER TABLE ADD COLUMN cannot relax that, so the
    /// [Required] attribute stays. Every downstream consumer (playback, file
    /// operations, duration display) must guard against an empty path before
    /// touching the file system.
    /// </summary>
    [Required]
    [Column("audio_file_path")]
    public string AudioFilePath { get; set; } = string.Empty;

    /// <summary>
    /// What kind of source this queue row represents. Audio rows carry a WAV
    /// file to transcribe; Text/Imported rows carry their content inline in
    /// <see cref="Transcript"/> and never run speech-to-text.
    /// </summary>
    [Column("source_type")]
    public QueueSourceType SourceType { get; set; } = QueueSourceType.Audio;

    /// <summary>
    /// Optional user-facing label for the source, e.g. "Journal 2014-03".
    /// </summary>
    [Column("source_label")]
    [MaxLength(200)]
    public string? SourceLabel { get; set; }

    /// <summary>
    /// The source text. For Text/Imported sources this holds the pasted or
    /// imported content itself. For Audio sources it caches the Whisper
    /// transcript after the first successful transcription so retries (e.g.
    /// after a flaky LLM call) reuse it instead of re-running speech-to-text.
    /// </summary>
    [Column("transcript")]
    public string? Transcript { get; set; }

    [Required]
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = QueueStatus.Pending;

    [Column("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [Column("file_size_bytes")]
    public long? FileSizeBytes { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("processed_at")]
    public DateTime? ProcessedAt { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    // --- Device-neutral provenance (iOS companion sync groundwork, design §7.1) ---
    // AudioFilePath above remains the RESOLVED LOCAL CACHE PATH used for processing;
    // the identity of a recording is its capture, not a Windows path.

    /// <summary>Globally unique capture ID this queue item was created from. Unique when set.</summary>
    [Column("source_capture_id")]
    public string? SourceCaptureId { get; set; }

    [Column("source_device_id")]
    public string? SourceDeviceId { get; set; }

    [Required]
    [Column("source_platform")]
    [MaxLength(20)]
    public string SourcePlatform { get; set; } = CapturePlatform.Windows;

    /// <summary>Artifact whose bytes AudioFilePath is a local materialization of.</summary>
    [Column("audio_artifact_id")]
    public string? AudioArtifactId { get; set; }

    [Column("original_file_name")]
    public string? OriginalFileName { get; set; }

    [Column("content_sha256")]
    [MaxLength(64)]
    public string? ContentSha256 { get; set; }

    [Column("sync_state")]
    [MaxLength(30)]
    public string? SyncState { get; set; }

    [Column("received_at")]
    public DateTime? ReceivedAt { get; set; }

    /// <summary>
    /// Fine-grained processing stage (design §7.3). The four-state Status column
    /// stays externally compatible; this adds mobile-grade progress reporting.
    /// </summary>
    [Column("processing_stage")]
    [MaxLength(30)]
    public string? ProcessingStage { get; set; }

    // Navigation properties
    public virtual ICollection<PendingEvent> PendingEvents { get; set; } = new List<PendingEvent>();
}

/// <summary>
/// Discriminates what kind of capture a <see cref="RecordingQueue"/> row is.
/// Stored as INTEGER by EF Core's default enum-to-int conversion.
/// </summary>
public enum QueueSourceType
{
    /// <summary>A recorded audio file that needs speech-to-text.</summary>
    Audio = 0,

    /// <summary>Pasted text (journal entry, email, chat export) — no audio.</summary>
    Text = 1,

    /// <summary>Text imported from an external file/tool — no audio.</summary>
    Imported = 2
}

/// <summary>
/// Queue status enumeration.
/// </summary>
public static class QueueStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static readonly string[] AllStatuses =
    {
        Pending, Processing, Completed, Failed
    };
}

/// <summary>
/// Fine-grained queue processing stages (design §7.3). Orthogonal to
/// <see cref="QueueStatus"/>: status remains the coarse externally-compatible
/// state machine, stage describes where inside it an item currently is.
/// </summary>
public static class QueueProcessingStage
{
    public const string PendingDownload = "pending_download";
    public const string Verifying = "verifying";
    public const string ReadyForTranscription = "ready_for_transcription";
    public const string Transcribing = "transcribing";
    public const string Extracting = "extracting";
    public const string ReviewReady = "review_ready";
    public const string Completed = "completed";
    public const string FailedRetryable = "failed_retryable";
    public const string FailedConfiguration = "failed_configuration";
    public const string FailedPermanent = "failed_permanent";
}

/// <summary>
/// Synchronization state of a queue item relative to the sync service: where
/// the capture came from and whether its origin has been told it arrived.
/// Written by capture ingestion (the first two) and by the outbox publisher
/// (the third); read through the queue DTO.
/// </summary>
public static class QueueSyncState
{
    /// <summary>
    /// Created on this machine. Its capture record is still pushed to the
    /// service, but no other device is waiting to hear that it arrived, so
    /// the item never leaves this state.
    /// </summary>
    public const string LocalOnly = "local_only";
    /// <summary>
    /// Ingested from a remote capture; the capture entry this machine wrote
    /// to sync_outbox has not been accepted by the service yet.
    /// </summary>
    public const string Received = "received";
    /// <summary>
    /// The service accepted that outbox entry (LocalOutboxPublisher), so the
    /// receipt is durable upstream and the capturing device can be told.
    /// </summary>
    public const string Acknowledged = "acknowledged";
}
