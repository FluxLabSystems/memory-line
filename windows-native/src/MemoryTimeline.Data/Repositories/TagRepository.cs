using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for Tag entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class TagRepository : ITagRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public TagRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    #region IRepository<Tag> Implementation

    public async Task<Tag?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .Include(t => t.EventTags)
            .FirstOrDefaultAsync(t => t.TagId == id, cancellationToken);
    }

    public async Task<IEnumerable<Tag>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .AsNoTracking()
            .OrderBy(t => t.TagName)
            .ToListAsync(cancellationToken);
    }

    public async Task<Tag> AddAsync(Tag entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Tags.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(Tag entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.Tags.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Tag entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.Tags.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<Tag> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Tags.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<Tag> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Tags.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<Tag, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<Tag, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.Tags.CountAsync(cancellationToken)
            : await context.Tags.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<Tag>> FindAsync(System.Linq.Expressions.Expression<Func<Tag, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    #endregion

    #region ITagRepository Implementation

    public async Task<Tag?> GetByNameAsync(string tagName, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .Include(t => t.EventTags)
            .FirstOrDefaultAsync(t => t.TagName == tagName, cancellationToken);
    }

    public async Task<IEnumerable<Tag>> GetOrderedByNameAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .AsNoTracking()
            .OrderBy(t => t.TagName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Tag>> GetFrequentlyUsedAsync(int minimumUsageCount = 5, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .AsNoTracking()
            .Where(t => t.EventTags.Count >= minimumUsageCount)
            .OrderByDescending(t => t.EventTags.Count)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Tag>> SearchByNameAsync(string searchTerm, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .AsNoTracking()
            .Where(t => EF.Functions.Like(t.TagName, $"%{searchTerm}%"))
            .OrderBy(t => t.TagName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Tag>> GetTagsForEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tags
            .AsNoTracking()
            .Where(t => t.EventTags.Any(et => et.EventId == eventId))
            .OrderBy(t => t.TagName)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetEventCountForTagAsync(string tagId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventTags
            .CountAsync(et => et.TagId == tagId, cancellationToken);
    }

    #endregion
}
