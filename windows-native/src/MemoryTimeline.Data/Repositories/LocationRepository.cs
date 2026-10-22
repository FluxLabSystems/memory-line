using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for Location entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class LocationRepository : ILocationRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public LocationRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Location?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.Include(l => l.EventLocations).FirstOrDefaultAsync(l => l.LocationId == id, cancellationToken);
    }

    public async Task<IEnumerable<Location>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync(cancellationToken);
    }

    public async Task<Location> AddAsync(Location entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Locations.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(Location entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.Locations.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Location entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.Locations.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<Location> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Locations.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<Location> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Locations.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<Location, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<Location, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.Locations.CountAsync(cancellationToken)
            : await context.Locations.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<Location>> FindAsync(System.Linq.Expressions.Expression<Func<Location, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<Location?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.Include(l => l.EventLocations).FirstOrDefaultAsync(l => l.Name == name, cancellationToken);
    }

    public async Task<IEnumerable<Location>> GetOrderedByNameAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Location>> SearchByNameAsync(string searchTerm, CancellationToken cancellationToken = default)
    {
        // User text: % and _ in the term are escaped and read literally.
        var pattern = SqlLikePattern.Contains(searchTerm);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking().Where(l => EF.Functions.Like(l.Name, pattern, SqlLikePattern.Escape)).OrderBy(l => l.Name).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Location>> GetLocationsForEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking().Where(l => l.EventLocations.Any(el => el.EventId == eventId)).OrderBy(l => l.Name).ToListAsync(cancellationToken);
    }

    public async Task<int> GetEventCountForLocationAsync(string locationId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventLocations.CountAsync(el => el.LocationId == locationId, cancellationToken);
    }

    public async Task SetCoordinatesAsync(string locationId, double latitude, double longitude, string? source, CancellationToken cancellationToken = default)
    {
        if (double.IsNaN(latitude) || latitude < -90.0 || latitude > 90.0)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude,
                "Latitude must be between -90 and 90 decimal degrees.");
        }

        if (double.IsNaN(longitude) || longitude < -180.0 || longitude > 180.0)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude,
                "Longitude must be between -180 and 180 decimal degrees.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var location = await context.Locations.FirstOrDefaultAsync(l => l.LocationId == locationId, cancellationToken)
            ?? throw new InvalidOperationException($"Location not found: {locationId}");

        location.Latitude = latitude;
        location.Longitude = longitude;
        // Only a geocoding lookup stamps GeocodedAt; EXIF/manual coordinates
        // leave it null (and clear a stale stamp - the coordinates no longer
        // come from a geocoder).
        location.GeocodedAt = string.Equals(source, "geocoded", StringComparison.OrdinalIgnoreCase)
            ? DateTime.UtcNow
            : null;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<Location>> GetLocatedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking()
            .Where(l => l.Latitude != null && l.Longitude != null)
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Location>> GetUnlocatedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Locations.AsNoTracking()
            .Where(l => l.Latitude == null || l.Longitude == null)
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<EventLocation>> GetEventLocationLinksAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventLocations.AsNoTracking()
            .Include(el => el.Event)
            .ToListAsync(cancellationToken);
    }
}
