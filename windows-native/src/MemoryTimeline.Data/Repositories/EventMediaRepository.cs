using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for <see cref="EventMedia"/>.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class EventMediaRepository : IEventMediaRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EventMediaRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<List<EventMedia>> GetForEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventMedia
            .AsNoTracking() // Read-only optimization
            .Where(m => m.EventId == eventId)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<EventMedia?> GetByIdAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventMedia
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MediaId == mediaId, cancellationToken);
    }

    public async Task<EventMedia?> GetByHashForEventAsync(string eventId, string contentHash, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventMedia
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EventId == eventId && m.ContentHash == contentHash, cancellationToken);
    }

    public async Task<EventMedia> AddAsync(EventMedia media, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.EventMedia.AddAsync(media, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return media;
    }

    public async Task UpdateAsync(EventMedia media, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached (loaded by a different, already-disposed context):
        // Update attaches it and marks it Modified.
        context.EventMedia.Update(media);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(EventMedia media, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.EventMedia.Remove(media);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Dictionary<string, int>> GetCountsForEventsAsync(IEnumerable<string> eventIds, CancellationToken cancellationToken = default)
    {
        var ids = eventIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var groups = await context.EventMedia
            .AsNoTracking()
            .Where(m => ids.Contains(m.EventId))
            .GroupBy(m => m.EventId)
            .Select(g => new { EventId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return groups.ToDictionary(g => g.EventId, g => g.Count);
    }

    public async Task<int> MaxSortOrderAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventMedia
            .AsNoTracking()
            .Where(m => m.EventId == eventId)
            .MaxAsync(m => (int?)m.SortOrder, cancellationToken) ?? -1;
    }
}
