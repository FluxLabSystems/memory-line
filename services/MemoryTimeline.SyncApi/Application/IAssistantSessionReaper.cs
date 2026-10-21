namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Removes assistant sessions that have gone idle (design §6.6 gives a session
/// a start and an end; §14.5 is why the turns go with it — questions and
/// answers are personal memory content, and a conversation nobody has touched
/// for a day has no reason to stay on the service). Runs on a timer from
/// <c>AssistantSessionReaperHostedService</c>; exposed as a service so a sweep
/// can be driven explicitly.
/// </summary>
public interface IAssistantSessionReaper
{
    /// <summary>
    /// Reaps every session whose last accepted turn is older than the idle
    /// window as of <paramref name="nowUtc"/>. A turn still waiting on a
    /// responder is cancelled and the cancellation published, so a responder
    /// that finally wakes does not answer into a deleted conversation.
    /// </summary>
    Task<AssistantReapReport> ReapAsync(DateTime nowUtc, CancellationToken cancellationToken);
}

/// <summary>What one reap did, for the log line and for tests.</summary>
public sealed record AssistantReapReport(int SessionsReaped, int TurnsCancelled, int TurnsDeleted);
