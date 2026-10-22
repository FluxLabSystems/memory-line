using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for <see cref="RecallPrompt"/>.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class RecallPromptRepository : IRecallPromptRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public RecallPromptRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<List<RecallPrompt>> GetActiveAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecallPrompts
            .AsNoTracking() // Read-only optimization
            .Where(p => p.Status == RecallPromptStatus.Active &&
                        (p.SnoozedUntil == null || p.SnoozedUntil <= now))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsForAsync(RecallPromptKind kind, string? entityId, DateTime? anchorStart, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Deliberately matches rows of EVERY status: a Dismissed or Answered
        // row must keep blocking regeneration of the same question.
        var query = context.RecallPrompts
            .AsNoTracking()
            .Where(p => p.Kind == kind && p.EntityId == entityId);

        if (anchorStart.HasValue)
        {
            var anchor = anchorStart.Value;
            query = query.Where(p => p.AnchorStart == anchor);
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<RecallPrompt> AddAsync(RecallPrompt prompt, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.RecallPrompts.Add(prompt);
        await context.SaveChangesAsync(cancellationToken);
        return prompt;
    }

    public async Task UpdateAsync(RecallPrompt prompt, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached (loaded by a different, already-disposed context):
        // Update attaches it and marks it Modified.
        context.RecallPrompts.Update(prompt);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<RecallPrompt?> GetByIdAsync(string promptId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RecallPrompts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PromptId == promptId, cancellationToken);
    }
}
