using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for CrossReference entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class CrossReferenceRepository : ICrossReferenceRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public CrossReferenceRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<CrossReference?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.Include(cr => cr.Event1).Include(cr => cr.Event2).FirstOrDefaultAsync(cr => cr.ReferenceId == id, cancellationToken);
    }

    public async Task<IEnumerable<CrossReference>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.AsNoTracking().OrderByDescending(cr => cr.ConfidenceScore).ToListAsync(cancellationToken);
    }

    public async Task<CrossReference> AddAsync(CrossReference entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.CrossReferences.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(CrossReference entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.CrossReferences.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(CrossReference entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.CrossReferences.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<CrossReference> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.CrossReferences.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<CrossReference> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.CrossReferences.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<CrossReference, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<CrossReference, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.CrossReferences.CountAsync(cancellationToken)
            : await context.CrossReferences.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<CrossReference>> FindAsync(System.Linq.Expressions.Expression<Func<CrossReference, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<CrossReference>> GetReferencesForEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.AsNoTracking().Where(cr => cr.EventId1 == eventId || cr.EventId2 == eventId)
            .Include(cr => cr.Event1).Include(cr => cr.Event2).OrderByDescending(cr => cr.ConfidenceScore).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<CrossReference>> GetByRelationshipTypeAsync(RelationshipType relationshipType, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.AsNoTracking().Where(cr => cr.RelationshipType == relationshipType.ToStringValue())
            .OrderByDescending(cr => cr.ConfidenceScore).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<CrossReference>> GetHighConfidenceReferencesAsync(double minimumConfidence = 0.7, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.AsNoTracking().Where(cr => cr.ConfidenceScore >= minimumConfidence)
            .OrderByDescending(cr => cr.ConfidenceScore).ToListAsync(cancellationToken);
    }

    public async Task<CrossReference?> GetReferenceAsync(string eventId1, string eventId2, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.FirstOrDefaultAsync(cr =>
            (cr.EventId1 == eventId1 && cr.EventId2 == eventId2) || (cr.EventId1 == eventId2 && cr.EventId2 == eventId1), cancellationToken);
    }

    public async Task<int> GetReferenceCountForEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CrossReferences.CountAsync(cr => cr.EventId1 == eventId || cr.EventId2 == eventId, cancellationToken);
    }
}
