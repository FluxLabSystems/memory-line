using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Api;

/// <summary>Detour suggestion disposition routes (design §6.5, §11.2 "Trips and detours").</summary>
public static class DetourEndpoints
{
    /// <summary>Maps the authenticated detour routes.</summary>
    public static void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/detours/{suggestionId}/accept",
                (string suggestionId, HttpContext context, IDetourService detours, CancellationToken ct)
                    => TransitionAsync(suggestionId, DetourSuggestionState.Accepted, context, detours, ct))
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapPost("/detours/{suggestionId}/dismiss",
                (string suggestionId, HttpContext context, IDetourService detours, CancellationToken ct)
                    => TransitionAsync(suggestionId, DetourSuggestionState.Dismissed, context, detours, ct))
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapPost("/detours/{suggestionId}/save",
                (string suggestionId, HttpContext context, IDetourService detours, CancellationToken ct)
                    => TransitionAsync(suggestionId, DetourSuggestionState.Saved, context, detours, ct))
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
    }

    private static async Task<IResult> TransitionAsync(
        string suggestionId,
        string targetState,
        HttpContext context,
        IDetourService detours,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await detours.TransitionAsync(caller, suggestionId, targetState, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }
}
