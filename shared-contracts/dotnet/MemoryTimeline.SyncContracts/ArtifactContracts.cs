namespace MemoryTimeline.SyncContracts;

/// <summary>
/// Wire contracts for artifact upload/download (design §11.2). Uploads are
/// multipart: initiate → PUT parts (raw bytes) → complete (server validates
/// part count, total byte length, and SHA-256 before accepting).
/// </summary>
public class ArtifactInitiateRequest
{
    /// <summary>Client-generated artifact ID; the server mints one when absent.</summary>
    public string? ArtifactId { get; set; }

    /// <summary>audio_original | audio_normalized | transcript | waveform | image | metadata.</summary>
    public string ArtifactType { get; set; } = "audio_original";

    public string? MediaType { get; set; }

    public string? OriginalFileName { get; set; }

    public long ExpectedByteLength { get; set; }

    /// <summary>Lowercase hex SHA-256 of the complete artifact bytes.</summary>
    public string ExpectedSha256 { get; set; } = string.Empty;
}

public class ArtifactInitiateResponse
{
    public string ArtifactId { get; set; } = string.Empty;

    /// <summary>Maximum accepted bytes per uploaded part.</summary>
    public long PartSizeBytes { get; set; }

    public int ExpectedPartCount { get; set; }
}

public class ArtifactCompleteRequest
{
    public int PartCount { get; set; }

    public long TotalByteLength { get; set; }

    /// <summary>Lowercase hex SHA-256; must match the initiate declaration and the assembled bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

public class ArtifactCompleteResponse
{
    public string ArtifactId { get; set; } = string.Empty;

    public string State { get; set; } = ArtifactState.Complete;

    public long ByteLength { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

public class ArtifactSummary
{
    public string ArtifactId { get; set; } = string.Empty;

    public string ArtifactType { get; set; } = "audio_original";

    public string? MediaType { get; set; }

    public string? OriginalFileName { get; set; }

    public long? ByteLength { get; set; }

    public string? Sha256 { get; set; }

    public string State { get; set; } = ArtifactState.Pending;
}

/// <summary>Server-side artifact lifecycle states.</summary>
public static class ArtifactState
{
    public const string Pending = "pending";
    public const string Uploading = "uploading";
    public const string Complete = "complete";
    public const string Failed = "failed";

    /// <summary>
    /// The bytes were removed by the service's retention sweep after Windows
    /// acknowledged archival (design §22 decision 7); the summary keeps the
    /// measured length and hash. A download answers 410 artifact_purged.
    /// Added in v1.4 — older clients see an unknown state and must treat it
    /// as "not downloadable", which is what their complete-only checks do.
    /// </summary>
    public const string Purged = "purged";
}
