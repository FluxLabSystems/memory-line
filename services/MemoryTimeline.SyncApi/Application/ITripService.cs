using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Trip lifecycle and route context snapshots (design §6.4, §11.2, §19
/// Phase 5). Trips are owner-scoped: every device of the owner reads and
/// updates the same trips. Status changes follow a state machine and the
/// latest <em>valid</em> transition wins (§12.2); an invalid one is refused
/// with 409 trip_invalid_transition rather than silently ignored.
/// </summary>
public interface ITripService
{
    /// <summary>Creates a trip; the service mints the ID.</summary>
    Task<ServiceResult<TripResponse>> CreateAsync(Device caller, TripCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Fetches one of the owner's trips.</summary>
    Task<ServiceResult<TripResponse>> GetAsync(Device caller, string tripId, CancellationToken cancellationToken);

    /// <summary>Lists the owner's trips, newest first.</summary>
    Task<ServiceResult<List<TripResponse>>> ListAsync(Device caller, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a partial update from the raw JSON object — only keys present
    /// are touched; an explicit null clears a nullable field. A status key is
    /// run through the state machine.
    /// </summary>
    Task<ServiceResult<TripResponse>> UpdateAsync(Device caller, string tripId, JsonObject body, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a route context snapshot. A client-supplied routeContextId that
    /// parses as a UUID is honoured and replaying it returns the stored
    /// snapshot (Created=false); otherwise the service mints one.
    /// </summary>
    Task<ServiceResult<RouteContextWriteResult>> AppendRouteContextAsync(
        Device caller, string tripId, TripRouteContext request, CancellationToken cancellationToken);
}

/// <summary>A route-context write outcome: the stored snapshot plus whether it was created (201) or replayed (200).</summary>
public sealed record RouteContextWriteResult(TripRouteContext Response, bool Created);
