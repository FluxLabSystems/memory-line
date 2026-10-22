namespace MemoryTimeline.SyncContracts;

/// <summary>
/// Wire contracts for capture metadata (design §11.3). Field set mirrors the
/// OpenAPI CaptureCreateRequest in shared-contracts/openapi/memory-line-sync-v1.yaml.
/// </summary>
public class CaptureCreateRequest
{
    public string CaptureId { get; set; } = string.Empty;

    public string SourceDeviceId { get; set; } = string.Empty;

    /// <summary>memory | idea | trip_log | reminder | question | unclassified.</summary>
    public string CaptureType { get; set; } = "unclassified";

    public DateTime CapturedAtUtc { get; set; }

    public string? TimezoneId { get; set; }

    public int? LocalOffsetMinutes { get; set; }

    public string? TripId { get; set; }

    public string? TitleHint { get; set; }

    public string? UserNote { get; set; }

    public LocationContext? LocationContext { get; set; }

    public int ClientSchemaVersion { get; set; } = 1;
}

public class LocationContext
{
    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double? HorizontalAccuracyM { get; set; }

    public double? HeadingDegrees { get; set; }

    public double? SpeedMps { get; set; }
}

public class CaptureResponse
{
    public string CaptureId { get; set; } = string.Empty;

    public string SourceDeviceId { get; set; } = string.Empty;

    public string SourcePlatform { get; set; } = string.Empty;

    public string CaptureType { get; set; } = "unclassified";

    public DateTime CapturedAtUtc { get; set; }

    public string? TimezoneId { get; set; }

    public int? LocalOffsetMinutes { get; set; }

    public string? TripId { get; set; }

    public string? TitleHint { get; set; }

    public string? UserNote { get; set; }

    /// <summary>
    /// The location snapshot the capture was created with (contract v1.5),
    /// echoed as received; null when the client attached none.
    /// </summary>
    public LocationContext? Location { get; set; }

    /// <summary>Server-side capture status, see <see cref="SyncCaptureStatus"/>.</summary>
    public string Status { get; set; } = SyncCaptureStatus.Received;

    public long ServerRevision { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<ArtifactSummary> Artifacts { get; set; } = new();
}

/// <summary>Capture status values shared across clients (design §6.1).</summary>
public static class SyncCaptureStatus
{
    public const string LocalOnly = "local_only";
    public const string Uploading = "uploading";
    public const string Received = "received";
    public const string Processing = "processing";
    public const string ReviewReady = "review_ready";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
