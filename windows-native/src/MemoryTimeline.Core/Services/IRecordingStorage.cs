using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Owns the recording FILES the pipeline leaves on disk: the WAVs the recorder
/// writes, the copies audio import makes, the artifacts the sync applier
/// downloads, and the normalized WAVs ingestion produces (the three trees in
/// <see cref="RecordingStoragePaths"/>). Two jobs:
/// <list type="bullet">
/// <item><description><see cref="ReleaseAsync"/> — when a queue row is
/// removed, delete the files it owned unless another row still points at
/// them (an approved event's playback path, a pending event, another queue
/// item, another capture's artifact), and drop the capture's artifact rows so
/// no locator dangles. The <c>captures</c> row itself survives: it is the
/// idempotency tombstone that keeps a re-upload of the same capture from
/// resurrecting a recording the user removed (design §12.2).</description></item>
/// <item><description><see cref="CleanupOrphansAsync"/> — Settings "Clear
/// Cache": delete every file under the recording trees that no row
/// references, the same sweep <c>IMediaService.CleanupOrphansAsync</c> runs
/// over the media tree, and which calls this one.</description></item>
/// </list>
/// Files outside the managed trees are never deleted by either job: a path
/// a caller handed to ingestion from somewhere else is the caller's.
/// </summary>
public interface IRecordingStorage
{
    /// <summary>The trees this service owns.</summary>
    RecordingStoragePaths Paths { get; }

    /// <summary>
    /// Releases the files a just-removed queue item owned. Best-effort per
    /// file (a locked file is logged and skipped); throws only on database
    /// failure.
    /// </summary>
    Task<RecordingReleaseResult> ReleaseAsync(RecordingQueue removedItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes files under the recording trees that no row references. Files
    /// created within the last few minutes are left alone — a recording whose
    /// queue row is still on its way looks orphaned for a moment.
    /// </summary>
    Task<RecordingCleanupResult> CleanupOrphansAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IRecordingStorage.ReleaseAsync"/>.</summary>
/// <param name="FilesDeleted">Files removed from disk.</param>
/// <param name="BytesFreed">Their sizes, measured before deletion.</param>
/// <param name="FilesKept">Candidate files left in place because another row references them or they lie outside the managed trees.</param>
/// <param name="ArtifactRowsDeleted">capture_artifacts rows of the item's capture that were removed.</param>
public sealed record RecordingReleaseResult(int FilesDeleted, long BytesFreed, int FilesKept, int ArtifactRowsDeleted);

/// <summary>Outcome of <see cref="IRecordingStorage.CleanupOrphansAsync"/>.</summary>
/// <param name="FilesDeleted">Orphaned recording files removed.</param>
/// <param name="BytesFreed">Their sizes, measured before deletion.</param>
public sealed record RecordingCleanupResult(int FilesDeleted, long BytesFreed);

/// <summary>
/// <see cref="IRecordingStorage"/> over the context factory. Registered as a
/// singleton; stateless over the factory and the paths.
/// </summary>
public class RecordingStorage : IRecordingStorage
{
    /// <summary>
    /// Files created within this window are never deleted by the sweep. The
    /// recorder writes its WAV and ingestion hashes it before the queue row
    /// commits; the sync applier downloads an artifact before ingestion runs.
    /// Same width as the media sweep's window, for the same reason. Internal
    /// so tests can age files past it.
    /// </summary>
    internal static readonly TimeSpan CleanupGraceWindow = TimeSpan.FromMinutes(5);

    private static readonly string[] AudioArtifactTypes =
    {
        CaptureArtifactType.AudioOriginal,
        CaptureArtifactType.AudioNormalized,
    };

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<RecordingStorage> _logger;

    /// <summary>
    /// Creates the service. <paramref name="paths"/> defaults to the production
    /// layout (the container cannot build it, so it arrives as null there);
    /// tests pass one over a temp directory.
    /// </summary>
    public RecordingStorage(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<RecordingStorage> logger,
        RecordingStoragePaths? paths = null)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        Paths = paths ?? RecordingStoragePaths.Default;
    }

    /// <inheritdoc />
    public RecordingStoragePaths Paths { get; }

    /// <inheritdoc />
    public async Task<RecordingReleaseResult> ReleaseAsync(
        RecordingQueue removedItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removedItem);

        var candidates = new HashSet<string>(PathComparer);
        if (!string.IsNullOrWhiteSpace(removedItem.AudioFilePath))
        {
            candidates.Add(removedItem.AudioFilePath);
        }

        var artifactRowsDeleted = 0;
        HashSet<string> referencedElsewhere;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var captureId = removedItem.SourceCaptureId;
            List<CaptureArtifact> ownArtifacts = new();
            if (!string.IsNullOrWhiteSpace(captureId))
            {
                ownArtifacts = await context.CaptureArtifacts
                    .Where(a => a.CaptureId == captureId && AudioArtifactTypes.Contains(a.ArtifactType))
                    .ToListAsync(cancellationToken);
                foreach (var artifact in ownArtifacts)
                {
                    candidates.Add(artifact.StorageLocator);
                }
            }

            if (candidates.Count == 0)
            {
                // A text source, or an item that never had a file.
                return new RecordingReleaseResult(0, 0, 0, 0);
            }

            referencedElsewhere = await ReferencedPathsAsync(
                context, candidates, excludeQueueId: removedItem.QueueId, excludeCaptureId: captureId, cancellationToken);

            if (ownArtifacts.Count > 0)
            {
                // The rows describe files that are about to go (or that only an
                // event still holds); a locator that points at nothing is worse
                // than no row. The captures row stays as the §12.2 tombstone.
                context.CaptureArtifacts.RemoveRange(ownArtifacts);
                artifactRowsDeleted = ownArtifacts.Count;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var deleted = 0;
        var kept = 0;
        long bytes = 0;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Paths.Contains(path))
            {
                _logger.LogDebug("Recording {Path} lies outside the managed trees; left in place", path);
                kept++;
                continue;
            }

            if (referencedElsewhere.Contains(path))
            {
                _logger.LogDebug("Recording {Path} is still referenced by another row; left in place", path);
                kept++;
                continue;
            }

            if (TryDeleteFile(path, out var size))
            {
                deleted++;
                bytes += size;
            }
        }

        _logger.LogInformation(
            "Released recording files for queue item {QueueId}: {Deleted} deleted ({Bytes} bytes), {Kept} kept, {Rows} artifact row(s) removed",
            removedItem.QueueId, deleted, bytes, kept, artifactRowsDeleted);

        return new RecordingReleaseResult(deleted, bytes, kept, artifactRowsDeleted);
    }

    /// <inheritdoc />
    public async Task<RecordingCleanupResult> CleanupOrphansAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot ORDER matters, exactly as in the media sweep: files first,
        // rows second. A file that appears after the snapshot is never
        // considered, and a row that commits before the row query still
        // protects its file.
        var files = new List<string>();
        foreach (var root in Paths.AllRoots)
        {
            if (Directory.Exists(root))
            {
                files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            }
        }

        if (files.Count == 0)
        {
            return new RecordingCleanupResult(0, 0);
        }

        HashSet<string> referenced;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            referenced = await ReferencedPathsAsync(
                context, candidates: null, excludeQueueId: null, excludeCaptureId: null, cancellationToken);
        }

        var graceCutoffUtc = DateTime.UtcNow - CleanupGraceWindow;
        var deleted = 0;
        long bytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.GetCreationTimeUtc(file) > graceCutoffUtc)
            {
                continue;
            }

            if (referenced.Contains(file))
            {
                continue;
            }

            if (TryDeleteFile(file, out var size))
            {
                deleted++;
                bytes += size;
            }
        }

        _logger.LogInformation(
            "Recordings cleanup: deleted {Deleted} orphaned file(s) ({Bytes} bytes) under {Roots}",
            deleted, bytes, string.Join(", ", Paths.AllRoots));

        return new RecordingCleanupResult(deleted, bytes);
    }

    /// <summary>
    /// Every path a row references — queue items, pending events, approved
    /// events (their playback path) and capture artifacts — normalized for
    /// comparison. With <paramref name="candidates"/> the query is narrowed to
    /// those paths; the exclusions drop the rows the caller is releasing.
    /// </summary>
    private static async Task<HashSet<string>> ReferencedPathsAsync(
        AppDbContext context,
        IReadOnlyCollection<string>? candidates,
        string? excludeQueueId,
        string? excludeCaptureId,
        CancellationToken cancellationToken)
    {
        var candidateList = candidates?.ToList();

        var queueQuery = context.RecordingQueues.AsNoTracking()
            .Where(q => q.AudioFilePath != string.Empty);
        if (excludeQueueId != null)
        {
            queueQuery = queueQuery.Where(q => q.QueueId != excludeQueueId);
        }

        if (candidateList != null)
        {
            queueQuery = queueQuery.Where(q => candidateList.Contains(q.AudioFilePath));
        }

        var pendingQuery = context.PendingEvents.AsNoTracking()
            .Where(p => p.AudioFilePath != null && p.AudioFilePath != string.Empty)
            .Select(p => p.AudioFilePath!);
        if (candidateList != null)
        {
            pendingQuery = pendingQuery.Where(path => candidateList.Contains(path));
        }

        var eventQuery = context.Events.AsNoTracking()
            .Where(e => e.AudioFilePath != null && e.AudioFilePath != string.Empty)
            .Select(e => e.AudioFilePath!);
        if (candidateList != null)
        {
            eventQuery = eventQuery.Where(path => candidateList.Contains(path));
        }

        var artifactQuery = context.CaptureArtifacts.AsNoTracking().AsQueryable();
        if (excludeCaptureId != null)
        {
            artifactQuery = artifactQuery.Where(a => a.CaptureId != excludeCaptureId);
        }

        var artifactPaths = artifactQuery.Select(a => a.StorageLocator);
        if (candidateList != null)
        {
            artifactPaths = artifactPaths.Where(path => candidateList.Contains(path));
        }

        var referenced = new HashSet<string>(PathComparer);
        referenced.UnionWith(await queueQuery.Select(q => q.AudioFilePath).ToListAsync(cancellationToken));
        referenced.UnionWith(await pendingQuery.ToListAsync(cancellationToken));
        referenced.UnionWith(await eventQuery.ToListAsync(cancellationToken));
        referenced.UnionWith(await artifactPaths.ToListAsync(cancellationToken));
        return referenced;
    }

    private bool TryDeleteFile(string path, out long size)
    {
        size = 0;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            size = new FileInfo(path).Length;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            // A locked or vanished file must not abort the job.
            _logger.LogWarning(ex, "Could not delete recording file {Path}", path);
            size = 0;
            return false;
        }
    }

    /// <summary>
    /// Paths compared by their full form, case-insensitively on Windows (NTFS
    /// semantics) and exactly elsewhere, so a row written with a different
    /// separator or casing still protects its file.
    /// </summary>
    private static readonly IEqualityComparer<string> PathComparer = new FullPathComparer();

    private sealed class FullPathComparer : IEqualityComparer<string>
    {
        private static readonly StringComparer Inner = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public bool Equals(string? x, string? y) => Inner.Equals(Normalize(x), Normalize(y));

        public int GetHashCode(string obj) => Inner.GetHashCode(Normalize(obj) ?? string.Empty);

        private static string? Normalize(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
