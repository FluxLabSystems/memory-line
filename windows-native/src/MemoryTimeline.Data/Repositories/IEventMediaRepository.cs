using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository interface for <see cref="EventMedia"/> attachments.
/// </summary>
public interface IEventMediaRepository
{
    /// <summary>Gets all media for an event, ordered by <see cref="EventMedia.SortOrder"/>.</summary>
    Task<List<EventMedia>> GetForEventAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>Gets a single media row by id, or null.</summary>
    Task<EventMedia?> GetByIdAsync(string mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the media row with the given content hash attached to the given
    /// event, or null (used to make attaching the same file twice a no-op).
    /// </summary>
    Task<EventMedia?> GetByHashForEventAsync(string eventId, string contentHash, CancellationToken cancellationToken = default);

    /// <summary>Adds a media row.</summary>
    Task<EventMedia> AddAsync(EventMedia media, CancellationToken cancellationToken = default);

    /// <summary>Updates a (detached) media row.</summary>
    Task UpdateAsync(EventMedia media, CancellationToken cancellationToken = default);

    /// <summary>Deletes a (detached) media row.</summary>
    Task DeleteAsync(EventMedia media, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets attachment counts for many events in ONE batched query
    /// (event id -> count; events with no media are absent from the result).
    /// </summary>
    Task<Dictionary<string, int>> GetCountsForEventsAsync(IEnumerable<string> eventIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the highest <see cref="EventMedia.SortOrder"/> among the event's
    /// media, or -1 when the event has none (so next = max + 1 is always valid).
    /// </summary>
    Task<int> MaxSortOrderAsync(string eventId, CancellationToken cancellationToken = default);
}
