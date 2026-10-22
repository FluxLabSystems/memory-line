using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository interface for RecordingQueue entity.
/// </summary>
public interface IRecordingQueueRepository : IRepository<RecordingQueue>
{
    Task<IEnumerable<RecordingQueue>> GetByStatusAsync(string status, CancellationToken cancellationToken = default);
    Task<RecordingQueue?> GetByIdWithPendingEventsAsync(string queueId, CancellationToken cancellationToken = default);
    Task<int> GetCountByStatusAsync(string status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the queue item ingested from <paramref name="sourceCaptureId"/>
    /// from <see cref="QueueSyncState.Received"/> to
    /// <see cref="QueueSyncState.Acknowledged"/>: the sync service has
    /// accepted this machine's capture record, so the receipt is durable
    /// upstream. Returns true when a row moved; false when there is no such
    /// item or it was not in the received state (a local recording stays
    /// <see cref="QueueSyncState.LocalOnly"/>, an acknowledged one stays
    /// acknowledged), so a replayed delivery is a no-op.
    /// </summary>
    Task<bool> TryAcknowledgeSyncReceiptAsync(string sourceCaptureId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repository implementation for RecordingQueue entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class RecordingQueueRepository : IRecordingQueueRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public RecordingQueueRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<RecordingQueue> AddAsync(RecordingQueue entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.RecordingQueues.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(RecordingQueue entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.RecordingQueues.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(RecordingQueue entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.RecordingQueues.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<RecordingQueue> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.RecordingQueues.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<RecordingQueue> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.RecordingQueues.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<RecordingQueue, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<RecordingQueue, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.RecordingQueues.CountAsync(cancellationToken)
            : await context.RecordingQueues.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<RecordingQueue>> FindAsync(System.Linq.Expressions.Expression<Func<RecordingQueue, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<RecordingQueue?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .FirstOrDefaultAsync(q => q.QueueId == id, cancellationToken);
    }

    public async Task<IEnumerable<RecordingQueue>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .AsNoTracking()
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<RecordingQueue>> GetByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .AsNoTracking()
            .Where(q => q.Status == status)
            .OrderBy(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<RecordingQueue?> GetByIdWithPendingEventsAsync(string queueId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .Include(q => q.PendingEvents)
            .FirstOrDefaultAsync(q => q.QueueId == queueId, cancellationToken);
    }

    public async Task<int> GetCountByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecordingQueues
            .CountAsync(q => q.Status == status, cancellationToken);
    }

    public async Task<bool> TryAcknowledgeSyncReceiptAsync(string sourceCaptureId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCaptureId))
        {
            return false;
        }

        // Read-then-save in one context rather than ExecuteUpdate so the
        // transition also runs on the EF InMemory provider the sync tests use.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await context.RecordingQueues
            .FirstOrDefaultAsync(q => q.SourceCaptureId == sourceCaptureId, cancellationToken);
        if (item == null || item.SyncState != QueueSyncState.Received)
        {
            return false;
        }

        item.SyncState = QueueSyncState.Acknowledged;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
