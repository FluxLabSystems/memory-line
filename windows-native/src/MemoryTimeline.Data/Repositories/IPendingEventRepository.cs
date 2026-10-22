using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

public interface IPendingEventRepository : IRepository<PendingEvent>
{
    Task<IEnumerable<PendingEvent>> GetByQueueIdAsync(string queueId, CancellationToken cancellationToken = default);
    Task<IEnumerable<PendingEvent>> GetByStatusAsync(PendingStatus status, CancellationToken cancellationToken = default);
    Task<int> GetCountByStatusAsync(PendingStatus status, CancellationToken cancellationToken = default);
    Task<IEnumerable<PendingEvent>> GetRecentAsync(int count = 10, CancellationToken cancellationToken = default);
}
