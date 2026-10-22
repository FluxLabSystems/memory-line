using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository interface for Draft entity operations.
/// </summary>
public interface IDraftRepository : IRepository<Draft>
{
    /// <summary>
    /// Gets all drafts of the given type (see <see cref="DraftTypes"/>),
    /// ordered by most recently updated first.
    /// </summary>
    Task<IEnumerable<Draft>> GetByTypeAsync(string draftType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all drafts ordered by most recently updated first.
    /// </summary>
    Task<IEnumerable<Draft>> GetAllOrderedAsync(CancellationToken cancellationToken = default);
}
