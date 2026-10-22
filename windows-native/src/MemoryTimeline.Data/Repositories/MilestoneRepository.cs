using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;
using System.Linq.Expressions;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for Milestone entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class MilestoneRepository : IMilestoneRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public MilestoneRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IEnumerable<Milestone>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .AsNoTracking()
            .Include(m => m.LinkedEra)
            .OrderBy(m => m.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<Milestone?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .Include(m => m.LinkedEra)
            .FirstOrDefaultAsync(m => m.MilestoneId == id, cancellationToken);
    }

    public async Task<IEnumerable<Milestone>> GetOrderedByDateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .AsNoTracking()
            .Include(m => m.LinkedEra)
            .OrderBy(m => m.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Milestone>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .AsNoTracking()
            .Include(m => m.LinkedEra)
            .Where(m => m.Date >= startDate && m.Date <= endDate)
            .OrderBy(m => m.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Milestone>> GetByEraIdAsync(string eraId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .AsNoTracking()
            .Include(m => m.LinkedEra)
            .Where(m => m.LinkedEraId == eraId)
            .OrderBy(m => m.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Milestone>> GetByTypeAsync(MilestoneType type, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .AsNoTracking()
            .Include(m => m.LinkedEra)
            .Where(m => m.Type == type)
            .OrderBy(m => m.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Milestone>> FindAsync(Expression<Func<Milestone, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones
            .AsNoTracking()
            .Include(m => m.LinkedEra)
            .Where(predicate)
            .OrderBy(m => m.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<Milestone> AddAsync(Milestone entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Milestones.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task AddRangeAsync(IEnumerable<Milestone> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Milestones.AddRangeAsync(entities, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Milestone entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.Milestones.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Milestone entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.Milestones.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<Milestone> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Milestones.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Expression<Func<Milestone, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Milestones.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(Expression<Func<Milestone, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (predicate == null)
            return await context.Milestones.CountAsync(cancellationToken);

        return await context.Milestones.CountAsync(predicate, cancellationToken);
    }
}
