using System.Linq.Expressions;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Generic repository interface for data access operations.
///
/// Every async operation takes a trailing optional <see cref="CancellationToken"/>
/// that the implementation hands to EF Core (context creation, queries,
/// SaveChanges), so a caller that is itself cancellable can stop a query
/// instead of waiting for it; callers that are not pass nothing. A Moq setup
/// on any of these methods must spell the parameter out
/// (<c>It.IsAny&lt;CancellationToken&gt;()</c>): an expression tree cannot
/// omit an optional argument.
/// </summary>
/// <typeparam name="T">Entity type</typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>
    /// Gets all entities.
    /// </summary>
    Task<IEnumerable<T>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entity by ID.
    /// </summary>
    Task<T?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds entities matching the predicate.
    /// </summary>
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new entity.
    /// </summary>
    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds multiple entities.
    /// </summary>
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing entity.
    /// </summary>
    Task UpdateAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity.
    /// </summary>
    Task DeleteAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple entities.
    /// </summary>
    Task DeleteRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if any entity matches the predicate.
    /// </summary>
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets count of entities matching the predicate.
    /// </summary>
    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default);
}
