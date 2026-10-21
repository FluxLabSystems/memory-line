using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;
using System.Linq.Expressions;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for Event entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class EventRepository : IEventRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EventRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IEnumerable<Event>> GetAllAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .AsNoTracking() // Read-only optimization
            .OrderByDescending(e => e.StartDate)
            .ToListAsync();
    }

    public async Task<Event?> GetByIdAsync(string id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .FirstOrDefaultAsync(e => e.EventId == id);
    }

    public async Task<Event?> GetByIdWithIncludesAsync(string id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .Include(e => e.EventTags)
                .ThenInclude(et => et.Tag)
            .Include(e => e.EventPeople)
                .ThenInclude(ep => ep.Person)
            .Include(e => e.EventLocations)
                .ThenInclude(el => el.Location)
            .Include(e => e.Embedding)
            .FirstOrDefaultAsync(e => e.EventId == id);
    }

    public async Task<IEnumerable<Event>> FindAsync(Expression<Func<Event, bool>> predicate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .AsNoTracking() // Read-only optimization
            .Where(predicate)
            .OrderByDescending(e => e.StartDate)
            .ToListAsync();
    }

    public async Task<Event> AddAsync(Event entity)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        await context.Events.AddAsync(entity);
        await context.SaveChangesAsync();
        return entity;
    }

    public async Task AddRangeAsync(IEnumerable<Event> entities)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        await context.Events.AddRangeAsync(entities);
        await context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Event entity)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        // Entity is detached (loaded by a different, already-disposed context):
        // Update attaches it and marks it Modified.
        context.Events.Update(entity);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Event entity)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        await RemoveCrossReferencesAsync(context, new[] { entity.EventId });
        // Remove attaches the detached entity and marks it Deleted.
        context.Events.Remove(entity);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Stages the deletion of every cross-reference that names any of
    /// <paramref name="eventIds"/> at either end, in the SAME context as the
    /// event delete so one SaveChanges carries both. Both cross_references
    /// foreign keys are ON DELETE RESTRICT and every connection runs with
    /// foreign_keys=ON, so without this an event the Connections page has ever
    /// related to another cannot be deleted at all — SQLite refuses the row
    /// delete and the user sees a failure for a memory they chose to remove.
    /// A cross-reference is a fact about a pair; it ends with either endpoint.
    /// </summary>
    private static async Task RemoveCrossReferencesAsync(AppDbContext context, IReadOnlyCollection<string> eventIds)
    {
        var references = await context.CrossReferences
            .Where(cr => eventIds.Contains(cr.EventId1) || eventIds.Contains(cr.EventId2))
            .ToListAsync();
        if (references.Count > 0)
        {
            context.CrossReferences.RemoveRange(references);
        }
    }

    public async Task DeleteRangeAsync(IEnumerable<Event> entities)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var list = entities.ToList();
        await RemoveCrossReferencesAsync(context, list.Select(e => e.EventId).ToList());
        context.Events.RemoveRange(list);
        await context.SaveChangesAsync();
    }

    public async Task<bool> ExistsAsync(Expression<Func<Event, bool>> predicate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events.AnyAsync(predicate);
    }

    public async Task<int> CountAsync(Expression<Func<Event, bool>>? predicate = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        if (predicate == null)
            return await context.Events.CountAsync();

        return await context.Events.CountAsync(predicate);
    }

    /// <summary>
    /// Widening margins for the precision-window branch of
    /// <see cref="GetByDateRangeAsync"/>: the farthest an anchor date can sit
    /// from an edge of its derived <see cref="DatePrecisionExtensions.GetWindow"/>
    /// window. A Season or Year window stays within one year of the anchor
    /// (a 31 Dec Year anchor is 364 days past its 1 Jan window start; a
    /// Jan/Feb Season anchor reaches ~2 months back into the previous
    /// December), so 366 days covers both with leap slack. A Decade window
    /// spans ten years (a 31 Dec 1999 anchor is ~3651 days past its
    /// 1 Jan 1990 window start), so 3653 days (ten years incl. leap days)
    /// covers the worst case.
    /// </summary>
    private const int SeasonYearWindowMarginDays = 366;
    private const int DecadeWindowMarginDays = 3653;

    public async Task<IEnumerable<Event>> GetByDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        // Overlap predicate (same semantics as EraRepository.GetByDateRangeAsync):
        // an event is in range when it starts before the range ends and ends
        // (or, for point-in-time events, starts) after the range begins.
        // This keeps multi-day events that are already in progress visible.
        //
        // Approximate events additionally match on their PRECISION WINDOW so
        // a "Summer 1998" or Decade event still loads (and can render its
        // uncertainty band) while its window overlaps the range even though
        // the anchor date sits outside it: branch (b) uses the stored
        // earliest/latest bounds when present, branch (c) approximates the
        // derived window by matching the anchor against the range widened by
        // the precision's maximum anchor-to-window-edge distance (the
        // client-side GetWindow cannot run inside a SQL-translated
        // predicate; the margins are documented on the constants above).
        //
        // People and tags are included so timeline DTOs can surface them
        // without a per-event lazy load (tags feed the timeline's tag-lane
        // grouping; one batched query, never per-event).
        var seasonYearStart = AddDaysClamped(startDate, -SeasonYearWindowMarginDays);
        var seasonYearEnd = AddDaysClamped(endDate, SeasonYearWindowMarginDays);
        var decadeStart = AddDaysClamped(startDate, -DecadeWindowMarginDays);
        var decadeEnd = AddDaysClamped(endDate, DecadeWindowMarginDays);

        IQueryable<Event> query = context.Events
            .Include(e => e.Era)
            .Include(e => e.EventPeople)
                .ThenInclude(ep => ep.Person)
            .Include(e => e.EventTags)
                .ThenInclude(et => et.Tag)
            .AsNoTracking() // Read-only optimization
            .Where(e =>
                // (a) the event's own date range overlaps the range
                (e.StartDate <= endDate && (e.EndDate ?? e.StartDate) >= startDate)
                // (b) the stored precision window overlaps the range
                || (e.EarliestPossible != null && e.LatestPossible != null
                    && e.EarliestPossible <= endDate && e.LatestPossible >= startDate)
                // (c) no stored bounds: precision-derived widened margin
                || ((e.EarliestPossible == null || e.LatestPossible == null)
                    && (((e.DatePrecision == DatePrecision.Season || e.DatePrecision == DatePrecision.Year)
                            && e.StartDate >= seasonYearStart && e.StartDate <= seasonYearEnd)
                        || (e.DatePrecision == DatePrecision.Decade
                            && e.StartDate >= decadeStart && e.StartDate <= decadeEnd))))
            .OrderBy(e => e.StartDate);

        // Two collection Includes in one SQL statement multiply into a
        // cartesian product (rows per event = people x tags, duplicating the
        // whole event payload; EF Core 8 logs MultipleCollectionIncludeWarning).
        // Split query issues three simple statements instead - safe on SQLite
        // with the per-operation context. The EF InMemory provider (unit
        // tests) cannot translate AsSplitQuery, hence the relational guard.
        if (context.Database.IsRelational())
        {
            query = query.AsSplitQuery();
        }

        return await query.ToListAsync();
    }

    /// <summary>
    /// <see cref="DateTime.AddDays"/> clamped to the representable calendar
    /// so extreme viewports near year 1 / 9999 cannot throw.
    /// </summary>
    private static DateTime AddDaysClamped(DateTime value, int days)
    {
        var ticks = value.Ticks + TimeSpan.FromDays(days).Ticks;
        if (ticks < DateTime.MinValue.Ticks)
        {
            return DateTime.MinValue;
        }

        if (ticks > DateTime.MaxValue.Ticks)
        {
            return DateTime.MaxValue;
        }

        return new DateTime(ticks, value.Kind);
    }

    public async Task<IEnumerable<Event>> GetByCategoryAsync(string category)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .AsNoTracking() // Read-only optimization
            .Where(e => e.Category == category)
            .OrderByDescending(e => e.StartDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Event>> GetByEraAsync(string eraId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .AsNoTracking() // Read-only optimization
            .Where(e => e.EraId == eraId)
            .OrderBy(e => e.StartDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Event>> SearchAsync(string searchTerm)
    {
        // Note: Full-text search using FTS5 would require raw SQL
        // For now, using basic LIKE search
        var term = $"%{searchTerm}%";
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Events
            .Include(e => e.Era)
            .AsNoTracking() // Read-only optimization
            .Where(e => EF.Functions.Like(e.Title, term) ||
                       EF.Functions.Like(e.Description ?? "", term) ||
                       EF.Functions.Like(e.RawTranscript ?? "", term))
            .OrderByDescending(e => e.StartDate)
            .ToListAsync();
    }

    public async Task<(IEnumerable<Event> Events, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? category = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Events
            .Include(e => e.Era)
            .AsNoTracking() // Read-only optimization
            .AsQueryable();

        if (startDate.HasValue)
            query = query.Where(e => e.StartDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.StartDate <= endDate.Value);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(e => e.Category == category);

        var totalCount = await query.CountAsync();

        var events = await query
            .OrderByDescending(e => e.StartDate)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (events, totalCount);
    }
}
