using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository interface for Person entity operations.
/// </summary>
public interface IPersonRepository : IRepository<Person>
{
    /// <summary>
    /// Gets a person by their name.
    /// </summary>
    Task<Person?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all people ordered by name.
    /// </summary>
    Task<IEnumerable<Person>> GetOrderedByNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches people by partial name match.
    /// </summary>
    Task<IEnumerable<Person>> SearchByNameAsync(string searchTerm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets people associated with a specific event.
    /// </summary>
    Task<IEnumerable<Person>> GetPeopleForEventAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of events associated with a person.
    /// </summary>
    Task<int> GetEventCountForPersonAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all people marked as favorites, ordered by name.
    /// </summary>
    Task<IEnumerable<Person>> GetFavoritesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the event count for every person that has at least one linked
    /// event, keyed by person id.
    /// </summary>
    Task<Dictionary<string, int>> GetEventCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the first and last event start dates for every person that has at
    /// least one linked event, keyed by person id.
    /// </summary>
    Task<Dictionary<string, (DateTime First, DateTime Last)>> GetEventDateRangesAsync(CancellationToken cancellationToken = default);
}
