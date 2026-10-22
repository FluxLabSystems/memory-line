using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository interface for EraCategory entity.
/// </summary>
public interface IEraCategoryRepository : IRepository<EraCategory>
{
    /// <summary>
    /// Gets category by name.
    /// </summary>
    Task<EraCategory?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all categories ordered by sort order.
    /// </summary>
    Task<IEnumerable<EraCategory>> GetOrderedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all visible categories.
    /// </summary>
    Task<IEnumerable<EraCategory>> GetVisibleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures default categories exist in the database.
    /// </summary>
    Task EnsureDefaultCategoriesAsync(CancellationToken cancellationToken = default);
}
