using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository for the local sync outbox. LocalOutboxPublisher drains
/// undelivered records in outbox_id order each sync cycle and marks them
/// delivered; the projection and status publishers read the latest record
/// per entity to avoid republishing an unchanged projection.
/// </summary>
public interface ISyncOutboxRepository
{
    Task<SyncOutbox> AddAsync(SyncOutbox entry, CancellationToken cancellationToken = default);

    /// <summary>Undelivered records in strict outbox_id (creation) order.</summary>
    Task<IReadOnlyList<SyncOutbox>> GetPendingAsync(int limit = 100, CancellationToken cancellationToken = default);

    Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Most recently created record for one entity, delivered or not. Publishers
    /// that project mutable state (e.g. capture processing status) use it to
    /// suppress re-publishing a projection that has not changed.
    /// </summary>
    Task<SyncOutbox?> GetLatestForEntityAsync(string entityType, string entityId, CancellationToken cancellationToken = default);

    Task MarkDeliveredAsync(long outboxId, CancellationToken cancellationToken = default);

    /// <summary>Records a failed delivery attempt without consuming the record.</summary>
    Task RecordFailedAttemptAsync(long outboxId, string error, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repository implementation for the sync outbox.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class SyncOutboxRepository : ISyncOutboxRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public SyncOutboxRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<SyncOutbox> AddAsync(SyncOutbox entry, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.SyncOutboxEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<SyncOutbox>> GetPendingAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SyncOutboxEntries
            .AsNoTracking()
            .Where(o => o.DeliveredAt == null)
            .OrderBy(o => o.OutboxId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SyncOutboxEntries.CountAsync(o => o.DeliveredAt == null, cancellationToken);
    }

    public async Task<SyncOutbox?> GetLatestForEntityAsync(string entityType, string entityId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Covered by IX_sync_outbox_entity_type_entity_id.
        return await context.SyncOutboxEntries
            .AsNoTracking()
            .Where(o => o.EntityType == entityType && o.EntityId == entityId)
            .OrderByDescending(o => o.OutboxId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task MarkDeliveredAsync(long outboxId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await context.SyncOutboxEntries.FirstOrDefaultAsync(o => o.OutboxId == outboxId, cancellationToken);
        if (entry == null || entry.DeliveredAt != null)
        {
            return;
        }

        entry.DeliveredAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFailedAttemptAsync(long outboxId, string error, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await context.SyncOutboxEntries.FirstOrDefaultAsync(o => o.OutboxId == outboxId, cancellationToken);
        if (entry == null)
        {
            return;
        }

        entry.AttemptCount++;
        entry.LastError = error;
        await context.SaveChangesAsync(cancellationToken);
    }
}
