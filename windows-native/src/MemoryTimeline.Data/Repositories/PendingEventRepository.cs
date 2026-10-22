using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for PendingEvent entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class PendingEventRepository : IPendingEventRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public PendingEventRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<PendingEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.Include(p => p.RecordingQueue).FirstOrDefaultAsync(p => p.PendingId == id, cancellationToken);
    }

    public async Task<IEnumerable<PendingEvent>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<PendingEvent> AddAsync(PendingEvent entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.PendingEvents.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(PendingEvent entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.PendingEvents.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(PendingEvent entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.PendingEvents.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<PendingEvent> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.PendingEvents.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<PendingEvent> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.PendingEvents.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<PendingEvent, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<PendingEvent, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.PendingEvents.CountAsync(cancellationToken)
            : await context.PendingEvents.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<PendingEvent>> FindAsync(System.Linq.Expressions.Expression<Func<PendingEvent, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<PendingEvent>> GetByQueueIdAsync(string queueId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.AsNoTracking().Where(p => p.QueueId == queueId).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<PendingEvent>> GetByStatusAsync(PendingStatus status, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.AsNoTracking().Where(p => p.Status == status.ToStringValue()).OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<int> GetCountByStatusAsync(PendingStatus status, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.CountAsync(p => p.Status == status.ToStringValue(), cancellationToken);
    }

    public async Task<IEnumerable<PendingEvent>> GetRecentAsync(int count = 10, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingEvents.AsNoTracking().OrderByDescending(p => p.CreatedAt).Take(count).ToListAsync(cancellationToken);
    }
}
