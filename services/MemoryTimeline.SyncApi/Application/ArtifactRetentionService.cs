using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="IArtifactRetentionService"/> over the sync database and the
/// artifact store. Stateless over the context factory; registered as a
/// singleton.
///
/// <para><b>What "acknowledged archival" means here.</b> Windows publishes a
/// <c>capture_status</c> of <c>completed</c> once a capture's events are on the
/// timeline (design §19 Phase 3). The service keeps the latest status per
/// capture, and the moment it <em>received</em> that status is the clock the
/// retention window runs from — not the capture's own timestamps, which say
/// nothing about whether Windows has the audio. A capture that is still
/// <c>processing</c>, <c>review_ready</c>, or <c>failed</c> keeps its artifact
/// indefinitely: Windows may yet need to download it again, and a failed
/// capture may be retried.</para>
///
/// <para><b>Purge is a state, not a deletion.</b> The artifact row survives
/// with <see cref="ArtifactState.Purged"/> and its measured length and hash
/// intact, so a late download answers 410 <c>artifact_purged</c> (permanent,
/// never retried) instead of a retryable 500 that a client would hammer
/// forever. The row is marked and saved <em>before</em> the blob is deleted, so a
/// crash between the two leaves a blob the next sweep removes rather than a
/// "complete" row whose bytes are gone.</para>
///
/// <para><b>Temporary files.</b> Part uploads and assemblies write through
/// per-request <c>*.tmp</c> files that in-process failures delete themselves.
/// A crash mid-request leaves them behind, so every sweep removes any
/// <c>*.tmp</c> not written to within the configured grace window — a file an
/// in-flight request is still writing has a recent write time and is left
/// alone.</para>
/// </summary>
public sealed class ArtifactRetentionService : IArtifactRetentionService
{
    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly IArtifactStore _store;
    private readonly SyncApiOptions _options;
    private readonly ILogger<ArtifactRetentionService> _logger;

    public ArtifactRetentionService(
        IDbContextFactory<SyncDbContext> contextFactory,
        IArtifactStore store,
        IOptions<SyncApiOptions> options,
        ILogger<ArtifactRetentionService> logger)
    {
        _contextFactory = contextFactory;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RetentionSweepReport> SweepAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var temporaryFilesRemoved = _store.SweepTemporaryFiles(
            nowUtc.AddMinutes(-_options.TemporaryFileGraceMinutes));

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Complete artifacts whose capture Windows has reported as archived,
        // oldest acknowledgement first — the order storage pressure purges in.
        var acknowledged = await (
                from artifact in db.Artifacts
                join status in db.CaptureStatuses on artifact.CaptureId equals status.CaptureId
                where artifact.State == ArtifactState.Complete
                    && status.Status == SyncCaptureStatus.Completed
                select new { Artifact = artifact, AcknowledgedAtUtc = status.ReceivedAtUtc })
            .ToListAsync(cancellationToken);
        acknowledged.Sort((a, b) => a.AcknowledgedAtUtc.CompareTo(b.AcknowledgedAtUtc));

        var purged = new List<ServerArtifact>();
        var cutoffUtc = nowUtc.AddDays(-_options.ArtifactRetentionDays);
        var purgedByAge = 0;
        foreach (var entry in acknowledged)
        {
            if (entry.AcknowledgedAtUtc > cutoffUtc)
            {
                continue;
            }

            MarkPurged(entry.Artifact, nowUtc);
            purged.Add(entry.Artifact);
            purgedByAge++;
        }

        var purgedByPressure = 0;
        if (_options.ArtifactStorageBudgetBytes > 0)
        {
            // Bytes still held after the age purge above (those rows are
            // marked in the tracker but the query reads the store, so subtract
            // them explicitly).
            var held = await db.Artifacts
                .Where(a => a.State == ArtifactState.Complete)
                .SumAsync(a => a.ActualByteLength ?? a.ExpectedByteLength, cancellationToken);
            held -= purged.Sum(BytesHeld);

            foreach (var entry in acknowledged)
            {
                if (held <= _options.ArtifactStorageBudgetBytes)
                {
                    break;
                }

                if (entry.Artifact.State != ArtifactState.Complete)
                {
                    continue; // already purged by age above
                }

                held -= BytesHeld(entry.Artifact);
                MarkPurged(entry.Artifact, nowUtc);
                purged.Add(entry.Artifact);
                purgedByPressure++;
            }

            if (held > _options.ArtifactStorageBudgetBytes)
            {
                // Unacknowledged artifacts are never purged for space: Windows
                // may still need them. Say so rather than silently staying over.
                _logger.LogWarning(
                    "Artifact store holds {HeldBytes} bytes against a budget of {BudgetBytes}; " +
                    "the remainder belongs to captures Windows has not acknowledged and is kept.",
                    held, _options.ArtifactStorageBudgetBytes);
            }
        }

        if (purged.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        // Rows marked purged — now or by a sweep that crashed before this step —
        // must have no blob behind them.
        var purgedIds = await db.Artifacts.AsNoTracking()
            .Where(a => a.State == ArtifactState.Purged)
            .Select(a => a.ArtifactId)
            .ToListAsync(cancellationToken);
        foreach (var artifactId in purgedIds)
        {
            if (Guid.TryParse(artifactId, out var guid))
            {
                _store.DeleteAssembled(guid);
            }
        }

        var report = new RetentionSweepReport(temporaryFilesRemoved, purgedByAge, purgedByPressure);
        if (report.TemporaryFilesRemoved > 0 || report.ArtifactsPurgedByAge > 0 || report.ArtifactsPurgedByPressure > 0)
        {
            _logger.LogInformation(
                "Retention sweep removed {TempFiles} temporary file(s) and purged {ByAge} artifact(s) by age " +
                "and {ByPressure} under storage pressure.",
                report.TemporaryFilesRemoved, report.ArtifactsPurgedByAge, report.ArtifactsPurgedByPressure);
        }

        return report;
    }

    private static long BytesHeld(ServerArtifact artifact) => artifact.ActualByteLength ?? artifact.ExpectedByteLength;

    private void MarkPurged(ServerArtifact artifact, DateTime nowUtc)
    {
        artifact.State = ArtifactState.Purged;
        _logger.LogInformation(
            "Artifact {ArtifactId} on capture {CaptureId} purged ({ByteLength} bytes).",
            artifact.ArtifactId, artifact.CaptureId, BytesHeld(artifact));
    }
}
