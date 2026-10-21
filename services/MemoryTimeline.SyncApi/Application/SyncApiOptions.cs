namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Configuration for the sync service, bound from the "SyncApi" section.
/// All values have working defaults so the service runs with an empty section.
/// </summary>
public class SyncApiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SyncApi";

    /// <summary>
    /// Directory holding the SQLite database, artifact blobs, signing key, and
    /// pairing code. Relative paths resolve against the working directory.
    /// </summary>
    public string DataDir { get; set; } = "data";

    /// <summary>
    /// Owner pairing code required by device registration. When unset, a code
    /// is generated on first run and persisted to {DataDir}/pairing-code.txt.
    /// </summary>
    public string? PairingCode { get; set; }

    /// <summary>Access token (JWT) lifetime in minutes.</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    /// <summary>Refresh token lifetime in days.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    /// <summary>
    /// Seconds the previous refresh token remains usable after a rotation so a
    /// client that never received the rotation response can recover instead of
    /// being stranded. 0 means strict rotation (the old token dies immediately).
    /// </summary>
    public int RefreshTokenGraceSeconds { get; set; } = 300;

    /// <summary>Maximum accepted bytes per uploaded artifact part (default 8 MiB).</summary>
    public long MaxPartSizeBytes { get; set; } = 8_388_608;

    /// <summary>
    /// Base64 HMAC-SHA256 token signing key (at least 32 bytes). When unset, a
    /// key is generated on first run and persisted to {DataDir}/signing.key.
    /// </summary>
    public string? TokenSigningKeyBase64 { get; set; }

    /// <summary>
    /// Days a completed artifact's bytes are kept after Windows acknowledges
    /// archival (a <c>capture_status</c> of <c>completed</c>) before the
    /// retention sweep purges them (design §22 decision 7). Artifacts of
    /// captures Windows has not acknowledged are never purged by age.
    /// </summary>
    public int ArtifactRetentionDays { get; set; } = 14;

    /// <summary>
    /// Soft cap on the bytes held by complete artifacts. When exceeded, the
    /// sweep purges acknowledged artifacts oldest-first until back under it
    /// ("or sooner under storage pressure", §22 decision 7). 0 disables the
    /// cap; unacknowledged artifacts are never purged for space.
    /// </summary>
    public long ArtifactStorageBudgetBytes { get; set; }

    /// <summary>Minutes between retention sweeps (the first runs at startup).</summary>
    public int RetentionSweepIntervalMinutes { get; set; } = 360;

    /// <summary>
    /// Minutes a temporary part or assembly file may go unwritten before the
    /// sweep treats it as crash-orphaned and deletes it. In-flight requests
    /// keep writing theirs, so a generous window costs nothing.
    /// </summary>
    public int TemporaryFileGraceMinutes { get; set; } = 60;

    /// <summary>
    /// Hours without an accepted turn after which an assistant session is
    /// reaped (its turns deleted, unanswered ones cancelled on the feed). The
    /// design doc names no figure; a day outlasts any single drive.
    /// </summary>
    public int AssistantSessionIdleHours { get; set; } = 24;

    /// <summary>Minutes between assistant session reaps (the first runs at startup).</summary>
    public int AssistantReaperIntervalMinutes { get; set; } = 60;
}
