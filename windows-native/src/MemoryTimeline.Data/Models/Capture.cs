using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MemoryTimeline.Data.Models;

/// <summary>
/// Device-neutral unit of user input (iOS companion sync groundwork).
/// A capture is the identity of a recording independent of which device made it
/// and where its bytes currently live; artifacts carry the actual content.
/// See docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md §6.1.
/// </summary>
[Table("captures")]
public class Capture
{
    [Key]
    [Column("capture_id")]
    public string CaptureId { get; set; } = Guid.NewGuid().ToString();

    [Column("source_device_id")]
    public string? SourceDeviceId { get; set; }

    [Required]
    [Column("source_platform")]
    [MaxLength(20)]
    public string SourcePlatform { get; set; } = CapturePlatform.Windows;

    [Required]
    [Column("capture_type")]
    [MaxLength(20)]
    public string CaptureType { get; set; } = CaptureTypes.Unclassified;

    [Column("created_at_utc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [Column("captured_at_utc")]
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    [Column("timezone_id")]
    public string? TimezoneId { get; set; }

    [Column("local_offset_minutes")]
    public int? LocalOffsetMinutes { get; set; }

    [Required]
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = CaptureStatus.Received;

    [Column("title_hint")]
    public string? TitleHint { get; set; }

    [Column("user_note")]
    public string? UserNote { get; set; }

    [Column("trip_id")]
    public string? TripId { get; set; }

    [Column("client_schema_version")]
    public int ClientSchemaVersion { get; set; } = 1;

    [Column("server_revision")]
    public long ServerRevision { get; set; }

    [Column("deleted_at_utc")]
    public DateTime? DeletedAtUtc { get; set; }

    // --- Location snapshot (design §6.1 location context, contract v1.5) ---
    // The single fix the capturing device attached at capture time, kept on
    // the capture it belongs to. Deliberately NOT a `locations` row: a
    // coordinate is not a named place until the user or an opt-in geocoder
    // makes it one (geocoding is off by default), and the snapshot never
    // leaves this machine through the sync outbox (privacy rule §14.5).
    // Null on captures made here and on remote captures sent without one.

    /// <summary>Latitude in decimal degrees, [-90, 90].</summary>
    [Column("location_latitude")]
    public double? LocationLatitude { get; set; }

    /// <summary>Longitude in decimal degrees, [-180, 180].</summary>
    [Column("location_longitude")]
    public double? LocationLongitude { get; set; }

    /// <summary>Horizontal accuracy of the fix in metres, when the device reported one.</summary>
    [Column("location_accuracy_m")]
    public double? LocationAccuracyM { get; set; }

    // Navigation properties
    public virtual ICollection<CaptureArtifact> Artifacts { get; set; } = new List<CaptureArtifact>();
}

/// <summary>
/// Platform a capture originated on. Mirrors the sourcePlatform / platform
/// enums in shared-contracts/openapi/memory-line-sync-v1.yaml, the
/// capture-envelope JSON schema and the service's DeviceService
/// .AllowedPlatforms; a new value goes into all of them together.
/// </summary>
public static class CapturePlatform
{
    public const string Windows = "windows";
    public const string Ios = "ios";
    /// <summary>The macOS app (contract v1.5); before that it registered as <see cref="Other"/>.</summary>
    public const string Macos = "macos";
    public const string Other = "other";
}

/// <summary>
/// Capture classification chosen by the user (or unclassified by default).
/// </summary>
public static class CaptureTypes
{
    public const string Memory = "memory";
    public const string Idea = "idea";
    public const string TripLog = "trip_log";
    public const string Reminder = "reminder";
    public const string Question = "question";
    public const string Unclassified = "unclassified";
}

/// <summary>
/// Capture lifecycle status as seen by this (Windows) client.
/// </summary>
public static class CaptureStatus
{
    public const string LocalOnly = "local_only";
    public const string Uploading = "uploading";
    public const string Received = "received";
    public const string Processing = "processing";
    public const string ReviewReady = "review_ready";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
