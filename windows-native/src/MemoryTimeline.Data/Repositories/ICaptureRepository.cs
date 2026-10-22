using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository for captures, capture artifacts, and the atomic ingestion write.
/// </summary>
public interface ICaptureRepository
{
    /// <summary>Gets a capture including its artifacts.</summary>
    Task<Capture?> GetByIdAsync(string captureId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string captureId, CancellationToken cancellationToken = default);

    /// <summary>Finds the queue item created from a device-neutral capture ID.</summary>
    Task<RecordingQueue?> GetQueueItemBySourceCaptureIdAsync(string sourceCaptureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the oldest queue item whose audio content matches the given SHA-256.
    /// Content-hash idempotency (design §7.2, §12.2): duplicate hash means reuse.
    /// </summary>
    Task<RecordingQueue?> GetQueueItemByContentSha256Async(string sha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists a capture, its audio artifact, its queue item, and the
    /// sync outbox record in ONE SaveChanges (one implicit transaction), so local
    /// metadata and outbox insertion cannot diverge (design §16.1).
    /// </summary>
    Task<RecordingQueue> IngestAsync(
        Capture capture,
        CaptureArtifact artifact,
        RecordingQueue queueItem,
        SyncOutbox outboxEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same atomic write for a capture with several artifacts — the original
    /// audio plus the normalized WAV ingestion produced from it. One
    /// SaveChanges, so a normalized artifact can never exist without the
    /// original it was made from, or the queue item that transcribes it.
    /// </summary>
    Task<RecordingQueue> IngestAsync(
        Capture capture,
        IReadOnlyList<CaptureArtifact> artifacts,
        RecordingQueue queueItem,
        SyncOutbox outboxEntry, CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(string captureId, string status, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repository implementation for Capture/CaptureArtifact entities.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class CaptureRepository : ICaptureRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public CaptureRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Capture?> GetByIdAsync(string captureId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Captures
            .Include(c => c.Artifacts)
            .FirstOrDefaultAsync(c => c.CaptureId == captureId, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string captureId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Captures.AnyAsync(c => c.CaptureId == captureId, cancellationToken);
    }

    public async Task<RecordingQueue?> GetQueueItemBySourceCaptureIdAsync(string sourceCaptureId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.SourceCaptureId == sourceCaptureId, cancellationToken);
    }

    public async Task<RecordingQueue?> GetQueueItemByContentSha256Async(string sha256, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .AsNoTracking()
            .Where(q => q.ContentSha256 == sha256)
            .OrderBy(q => q.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<RecordingQueue> IngestAsync(
        Capture capture,
        CaptureArtifact artifact,
        RecordingQueue queueItem,
        SyncOutbox outboxEntry, CancellationToken cancellationToken = default)
        => IngestAsync(capture, new[] { artifact }, queueItem, outboxEntry, cancellationToken);

    public async Task<RecordingQueue> IngestAsync(
        Capture capture,
        IReadOnlyList<CaptureArtifact> artifacts,
        RecordingQueue queueItem,
        SyncOutbox outboxEntry, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        context.Captures.Add(capture);
        context.CaptureArtifacts.AddRange(artifacts);
        context.RecordingQueues.Add(queueItem);
        context.SyncOutboxEntries.Add(outboxEntry);

        // A single SaveChanges is one implicit transaction on SQLite: either all
        // rows land or none do. Do not split into multiple SaveChanges.
        await context.SaveChangesAsync(cancellationToken);

        return queueItem;
    }

    public async Task UpdateStatusAsync(string captureId, string status, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var capture = await context.Captures.FirstOrDefaultAsync(c => c.CaptureId == captureId, cancellationToken);
        if (capture == null)
        {
            return;
        }

        capture.Status = status;
        await context.SaveChangesAsync(cancellationToken);
    }
}
