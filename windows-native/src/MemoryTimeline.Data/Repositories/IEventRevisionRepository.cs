using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository contract for the append-only <see cref="EventRevision"/> history.
/// Revisions are immutable: there is deliberately no Update, and no Delete -
/// history must survive event deletion (see <see cref="EventRevision"/>).
/// </summary>
public interface IEventRevisionRepository
{
    /// <summary>Gets an event's revisions, newest first.</summary>
    Task<List<EventRevision>> GetForEventAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>Gets a single revision by id, or null.</summary>
    Task<EventRevision?> GetByIdAsync(string revisionId, CancellationToken cancellationToken = default);

    /// <summary>Appends a revision.</summary>
    Task<EventRevision> AddAsync(EventRevision revision, CancellationToken cancellationToken = default);

    /// <summary>Counts revisions - all of them, or one event's when <paramref name="eventId"/> is given.</summary>
    Task<int> CountAsync(string? eventId = null, CancellationToken cancellationToken = default);
}
