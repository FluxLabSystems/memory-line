using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for Person entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class PersonRepository : IPersonRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public PersonRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    #region IRepository<Person> Implementation

    public async Task<Person?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People
            .Include(p => p.EventPeople)
            .FirstOrDefaultAsync(p => p.PersonId == id, cancellationToken);
    }

    public async Task<IEnumerable<Person>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Person> AddAsync(Person entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.People.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(Person entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.People.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Person entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.People.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<Person> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.People.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<Person> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.People.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<Person, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<Person, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.People.CountAsync(cancellationToken)
            : await context.People.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<Person>> FindAsync(System.Linq.Expressions.Expression<Func<Person, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    #endregion

    #region IPersonRepository Implementation

    public async Task<Person?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Tombstones (merged-away people) keep their names but their event
        // links were repointed at merge time; resolving a name to one would
        // hand callers a zero-link person. Only living people match here.
        return await context.People
            .Include(p => p.EventPeople)
            .FirstOrDefaultAsync(p => p.MergedIntoId == null && p.Name == name, cancellationToken);
    }

    public async Task<IEnumerable<Person>> GetOrderedByNameAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Person>> SearchByNameAsync(string searchTerm, CancellationToken cancellationToken = default)
    {
        // User text: % and _ in the term are escaped and read literally.
        var pattern = SqlLikePattern.Contains(searchTerm);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Tombstones (merged-away people) never surface in name search: they
        // keep their names but hold zero event links after the merge.
        return await context.People
            .AsNoTracking()
            .Where(p => p.MergedIntoId == null && EF.Functions.Like(p.Name, pattern, SqlLikePattern.Escape))
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Person>> GetPeopleForEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People
            .AsNoTracking()
            .Where(p => p.EventPeople.Any(ep => ep.EventId == eventId))
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetEventCountForPersonAsync(string personId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventPeople
            .CountAsync(ep => ep.PersonId == personId, cancellationToken);
    }

    public async Task<IEnumerable<Person>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.People
            .AsNoTracking()
            .Where(p => p.IsFavorite)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Dictionary<string, int>> GetEventCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventPeople
            .AsNoTracking()
            .GroupBy(ep => ep.PersonId)
            .Select(g => new { PersonId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PersonId, x => x.Count, cancellationToken);
    }

    public async Task<Dictionary<string, (DateTime First, DateTime Last)>> GetEventDateRangesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Tuples cannot be materialized server-side; project into an anonymous
        // type (Min/Max translate to SQL) and build the tuple dictionary on the
        // client.
        var ranges = await context.EventPeople
            .AsNoTracking()
            .Join(context.Events,
                ep => ep.EventId,
                e => e.EventId,
                (ep, e) => new { ep.PersonId, e.StartDate })
            .GroupBy(x => x.PersonId)
            .Select(g => new
            {
                PersonId = g.Key,
                First = g.Min(x => x.StartDate),
                Last = g.Max(x => x.StartDate)
            })
            .ToListAsync(cancellationToken);

        return ranges.ToDictionary(r => r.PersonId, r => (r.First, r.Last));
    }

    #endregion
}
