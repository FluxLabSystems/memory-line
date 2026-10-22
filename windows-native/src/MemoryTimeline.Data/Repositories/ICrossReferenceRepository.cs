using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

public interface ICrossReferenceRepository : IRepository<CrossReference>
{
    Task<IEnumerable<CrossReference>> GetReferencesForEventAsync(string eventId, CancellationToken cancellationToken = default);
    Task<IEnumerable<CrossReference>> GetByRelationshipTypeAsync(RelationshipType relationshipType, CancellationToken cancellationToken = default);
    Task<IEnumerable<CrossReference>> GetHighConfidenceReferencesAsync(double minimumConfidence = 0.7, CancellationToken cancellationToken = default);
    Task<CrossReference?> GetReferenceAsync(string eventId1, string eventId2, CancellationToken cancellationToken = default);
    Task<int> GetReferenceCountForEventAsync(string eventId, CancellationToken cancellationToken = default);
}
