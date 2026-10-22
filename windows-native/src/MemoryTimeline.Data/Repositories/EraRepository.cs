using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;
using System.Linq.Expressions;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for Era entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class EraRepository : IEraRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EraRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IEnumerable<Era>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .OrderBy(e => e.StartDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<Era?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .FirstOrDefaultAsync(e => e.EraId == id, cancellationToken);
    }

    public async Task<Era?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .FirstOrDefaultAsync(e => e.Name.ToLower() == name.ToLower(), cancellationToken);
    }

    public async Task<IEnumerable<Era>> GetOrderedByDateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .OrderBy(e => e.StartDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<Era?> GetByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .Where(e => e.StartDate <= date && (e.EndDate == null || e.EndDate >= date))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IEnumerable<Era>> FindAsync(Expression<Func<Era, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .Where(predicate)
            .OrderBy(e => e.StartDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<Era> AddAsync(Era entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Eras.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task AddRangeAsync(IEnumerable<Era> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Eras.AddRangeAsync(entities, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Era entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.Eras.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Era entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.Eras.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<Era> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Eras.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Expression<Func<Era, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(Expression<Func<Era, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (predicate == null)
            return await context.Eras.CountAsync(cancellationToken);

        return await context.Eras.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<Era>> GetByCategoryIdAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .Where(e => e.CategoryId == categoryId)
            .OrderBy(e => e.StartDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Era>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .Where(e => e.StartDate <= endDate && (e.EndDate == null || e.EndDate >= startDate))
            .OrderBy(e => e.StartDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<IGrouping<EraCategory?, Era>>> GetGroupedByCategoryAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var eras = await context.Eras
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EraTags)
            .OrderBy(e => e.Category != null ? e.Category.SortOrder : int.MaxValue)
            .ThenBy(e => e.StartDate)
            .ToListAsync(cancellationToken);

        return eras.GroupBy(e => e.Category);
    }
}
