using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Detour suggestion generation and disposition (design §6.5, §10, §12.2).
/// Generation delegates the outside-world part to <see cref="IDetourSuggestionProvider"/>;
/// this service persists what comes back, expires it when its time passes,
/// and moves it through the monotonic save/accept/dismiss transitions.
/// </summary>
public interface IDetourService
{
    /// <summary>
    /// Generates suggestions for the trip's most recent route context and
    /// returns the newly created ones, newest first. Expired suggestions are
    /// marked on the way, and places already suggested or dismissed are
    /// excluded so nothing is proposed twice (§10.5). Yields an empty list
    /// when the trip has no route context yet or no provider is configured.
    /// </summary>
    Task<ServiceResult<List<DetourSuggestion>>> GenerateAsync(Device caller, string tripId, CancellationToken cancellationToken);

    /// <summary>Lists the trip's suggestions, newest first, expiring any whose time has passed.</summary>
    Task<ServiceResult<List<DetourSuggestion>>> ListAsync(Device caller, string tripId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a suggestion to <paramref name="targetState"/> (saved, accepted or
    /// dismissed). Idempotent on the target state; a move the state machine
    /// does not allow — out of accepted, dismissed or expired — is 409
    /// detour_invalid_transition.
    /// </summary>
    Task<ServiceResult<DetourSuggestion>> TransitionAsync(
        Device caller, string suggestionId, string targetState, CancellationToken cancellationToken);
}
