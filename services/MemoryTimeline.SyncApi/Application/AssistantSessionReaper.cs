using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="IAssistantSessionReaper"/> over the sync database. Stateless
/// over the context factory; registered as a singleton.
///
/// <para><b>Idle means no accepted turn within the window.</b> The session's
/// <c>LastTurnAtUtc</c> moves on every accepted submission and starts at
/// creation, so an opened-and-abandoned session ages out like a finished one.
/// The window defaults to 24 hours (<see cref="SyncApiOptions.AssistantSessionIdleHours"/>):
/// the design doc names no figure, and a day comfortably outlasts any single
/// drive while keeping a question's text off the service for no longer than
/// the answer plausibly needs.</para>
///
/// <para><b>Unanswered turns are cancelled, not just deleted.</b> Windows may
/// have been asleep for the whole window with the question still on its feed.
/// Publishing a <c>cancelled</c> revision — with no source device, so echo
/// suppression hides it from nobody — tells a responder that finally wakes to
/// drop the turn, exactly as a user's explicit cancel does; a late answer for
/// it is then discarded by the push path's terminal-state guard. Deleting
/// silently would have the responder push an answer into a turn that no
/// longer exists and log a rejection it could do nothing about.</para>
/// </summary>
public sealed class AssistantSessionReaper : IAssistantSessionReaper
{
    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly SyncApiOptions _options;
    private readonly ILogger<AssistantSessionReaper> _logger;

    public AssistantSessionReaper(
        IDbContextFactory<SyncDbContext> contextFactory,
        IOptions<SyncApiOptions> options,
        ILogger<AssistantSessionReaper> logger)
    {
        _contextFactory = contextFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AssistantReapReport> ReapAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var cutoffUtc = nowUtc.AddHours(-_options.AssistantSessionIdleHours);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var idle = await db.AssistantSessions
            .Where(s => s.LastTurnAtUtc < cutoffUtc)
            .ToListAsync(cancellationToken);
        if (idle.Count == 0)
        {
            return new AssistantReapReport(0, 0, 0);
        }

        var sessionIds = idle.Select(s => s.SessionId).ToList();
        var turns = await db.AssistantTurns
            .Where(t => sessionIds.Contains(t.SessionId))
            .ToListAsync(cancellationToken);

        var cancelled = 0;
        foreach (var turn in turns)
        {
            if (AssistantTurnLifecycle.IsTerminal(turn.Status))
            {
                continue;
            }

            turn.Status = AssistantTurnStatus.Cancelled;
            turn.UpdatedAtUtc = nowUtc;
            turn.CompletedAtUtc = nowUtc;
            turn.Revision++;
            db.SyncChanges.Add(EntityMappers.CreateAssistantTurnChange(turn, sourceDeviceId: null));
            cancelled++;
        }

        // The change rows above reference turn IDs, not rows, so removing the
        // turns in the same save is fine; the feed keeps the history.
        db.AssistantTurns.RemoveRange(turns);
        db.AssistantSessions.RemoveRange(idle);
        await db.SaveChangesAsync(cancellationToken);

        // IDs and counts only (design §14.5): the questions just deleted are
        // exactly the kind of content that must never reach a log.
        _logger.LogInformation(
            "Reaped {SessionCount} idle assistant session(s) with {TurnCount} turn(s), cancelling {CancelledCount} still unanswered.",
            idle.Count, turns.Count, cancelled);
        return new AssistantReapReport(idle.Count, cancelled, turns.Count);
    }
}
