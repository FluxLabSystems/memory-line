using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for EventEmbedding entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class EventEmbeddingRepository : IEventEmbeddingRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EventEmbeddingRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<EventEmbedding?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.Include(e => e.Event).FirstOrDefaultAsync(e => e.EmbeddingId == id, cancellationToken);
    }

    public async Task<IEnumerable<EventEmbedding>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<EventEmbedding> AddAsync(EventEmbedding entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.EventEmbeddings.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(EventEmbedding entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.EventEmbeddings.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(EventEmbedding entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.EventEmbeddings.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<EventEmbedding> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.EventEmbeddings.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<EventEmbedding> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.EventEmbeddings.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<EventEmbedding, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<EventEmbedding, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.EventEmbeddings.CountAsync(cancellationToken)
            : await context.EventEmbeddings.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<EventEmbedding>> FindAsync(System.Linq.Expressions.Expression<Func<EventEmbedding, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<EventEmbedding?> GetByEventIdAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
    }

    public async Task<IEnumerable<EventEmbedding>> GetByProviderAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.AsNoTracking().Where(e => e.EmbeddingProvider == provider).ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<EventEmbedding>> FindSimilarAsync(double[] queryVector, int topK = 10, double minimumSimilarity = 0.7, CancellationToken cancellationToken = default)
    {
        // This is a simplified implementation - in production, use vector similarity search
        // For SQLite, we'd need to implement cosine similarity in-memory
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var allEmbeddings = await context.EventEmbeddings.AsNoTracking().Include(e => e.Event).ToListAsync(cancellationToken);

        var similarities = allEmbeddings.Select(e => new
        {
            Embedding = e,
            Similarity = CalculateCosineSimilarity(queryVector, DeserializeVector(e.EmbeddingVector))
        })
        .Where(x => x.Similarity >= minimumSimilarity)
        .OrderByDescending(x => x.Similarity)
        .Take(topK)
        .Select(x => x.Embedding)
        .ToList();

        return similarities;
    }

    public async Task<int> GetCountByProviderAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EventEmbeddings.CountAsync(e => e.EmbeddingProvider == provider, cancellationToken);
    }

    private static double[] DeserializeVector(string vectorString)
    {
        return System.Text.Json.JsonSerializer.Deserialize<double[]>(vectorString) ?? Array.Empty<double>();
    }

    private static double CalculateCosineSimilarity(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        // Guard against zero-magnitude vectors (would produce NaN/Infinity).
        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        if (denominator == 0) return 0;

        return dotProduct / denominator;
    }
}
