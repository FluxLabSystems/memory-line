using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

public interface IEventEmbeddingRepository : IRepository<EventEmbedding>
{
    Task<EventEmbedding?> GetByEventIdAsync(string eventId, CancellationToken cancellationToken = default);
    Task<IEnumerable<EventEmbedding>> GetByProviderAsync(string provider, CancellationToken cancellationToken = default);
    Task<IEnumerable<EventEmbedding>> FindSimilarAsync(double[] queryVector, int topK = 10, double minimumSimilarity = 0.7, CancellationToken cancellationToken = default);
    Task<int> GetCountByProviderAsync(string provider, CancellationToken cancellationToken = default);
}
