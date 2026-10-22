using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository interface for Milestone entity.
/// </summary>
public interface IMilestoneRepository : IRepository<Milestone>
{
    /// <summary>
    /// Gets milestones ordered by date.
    /// </summary>
    Task<IEnumerable<Milestone>> GetOrderedByDateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets milestones within a date range.
    /// </summary>
    Task<IEnumerable<Milestone>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets milestones linked to a specific era.
    /// </summary>
    Task<IEnumerable<Milestone>> GetByEraIdAsync(string eraId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets milestones by type.
    /// </summary>
    Task<IEnumerable<Milestone>> GetByTypeAsync(MilestoneType type, CancellationToken cancellationToken = default);
}
