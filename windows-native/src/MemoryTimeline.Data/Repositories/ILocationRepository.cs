using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

public interface ILocationRepository : IRepository<Location>
{
    Task<Location?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<IEnumerable<Location>> GetOrderedByNameAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Location>> SearchByNameAsync(string searchTerm, CancellationToken cancellationToken = default);
    Task<IEnumerable<Location>> GetLocationsForEventAsync(string eventId, CancellationToken cancellationToken = default);
    Task<int> GetEventCountForLocationAsync(string locationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a location's coordinates. Validates latitude [-90, 90] and
    /// longitude [-180, 180] (throws <see cref="ArgumentOutOfRangeException"/>
    /// otherwise) and throws <see cref="InvalidOperationException"/> for an
    /// unknown location. <paramref name="source"/> records where the
    /// coordinates came from: only the "geocoded" source stamps
    /// <see cref="Location.GeocodedAt"/>; "exif" and "manual" leave it null
    /// (and clear any previous stamp - the coordinates no longer come from a
    /// geocoder).
    /// </summary>
    Task SetCoordinatesAsync(string locationId, double latitude, double longitude, string? source, CancellationToken cancellationToken = default);

    /// <summary>Locations that have both coordinates, ordered by name.</summary>
    Task<IEnumerable<Location>> GetLocatedAsync(CancellationToken cancellationToken = default);

    /// <summary>Locations missing one or both coordinates, ordered by name.</summary>
    Task<IEnumerable<Location>> GetUnlocatedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// All event-location junction rows with their <see cref="EventLocation.Event"/>
    /// loaded (no tracking). One query for the map page to group events per
    /// location without an N+1 per-pin lookup.
    /// </summary>
    Task<IEnumerable<EventLocation>> GetEventLocationLinksAsync(CancellationToken cancellationToken = default);
}
