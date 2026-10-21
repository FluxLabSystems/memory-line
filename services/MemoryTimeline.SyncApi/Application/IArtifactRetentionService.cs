namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Housekeeping over the artifact store and the tables that reference it
/// (design §22 decisions 7 and 9): purges artifact blobs once Windows has
/// acknowledged archival and the retention window has passed — or sooner under
/// storage pressure — and removes crash-orphaned temporary files. Runs on a
/// timer from <c>RetentionSweepHostedService</c>; exposed as a service so a
/// sweep can also be driven explicitly (tests, an operator command).
/// </summary>
public interface IArtifactRetentionService
{
    /// <summary>
    /// Runs one sweep as of <paramref name="nowUtc"/>. The clock is a parameter
    /// rather than read inside so the retention arithmetic can be exercised
    /// without waiting fourteen days.
    /// </summary>
    Task<RetentionSweepReport> SweepAsync(DateTime nowUtc, CancellationToken cancellationToken);
}

/// <summary>What one sweep did, for the log line and for tests.</summary>
public sealed record RetentionSweepReport(
    int TemporaryFilesRemoved,
    int ArtifactsPurgedByAge,
    int ArtifactsPurgedByPressure);
