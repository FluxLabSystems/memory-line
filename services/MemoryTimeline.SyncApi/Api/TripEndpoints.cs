using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Api;

/// <summary>Trip lifecycle, route context and detour generation routes (design §11.2 "Trips and detours").</summary>
public static class TripEndpoints
{
    /// <summary>Maps the authenticated trip routes.</summary>
    public static void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/trips", CreateAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapGet("/trips", ListAsync);
        group.MapGet("/trips/{tripId}", GetAsync);
        group.MapPatch("/trips/{tripId}", UpdateAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapPost("/trips/{tripId}/route-context", AppendRouteContextAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapPost("/trips/{tripId}/detours/generate", GenerateDetoursAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapGet("/trips/{tripId}/detours", ListDetoursAsync);
    }

    private static async Task<IResult> CreateAsync(
        TripCreateRequest request,
        HttpContext context,
        ITripService trips,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await trips.CreateAsync(caller, request, cancellationToken);
        return result.IsSuccess
            ? Results.Created($"/api/v1/trips/{result.Value!.TripId}", result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ITripService trips,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await trips.ListAsync(caller, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> GetAsync(
        string tripId,
        HttpContext context,
        ITripService trips,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await trips.GetAsync(caller, tripId, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> UpdateAsync(
        string tripId,
        HttpContext context,
        ITripService trips,
        CancellationToken cancellationToken)
    {
        // Parsed as a JSON object (not a DTO), as the capture PATCH is, so an
        // absent key means "unchanged" while an explicit null clears a field.
        JsonObject body;
        try
        {
            var node = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            if (node is not JsonObject parsed)
            {
                return ApiErrorResults.Error(
                    context, StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                    "The request body must be a JSON object.");
            }

            body = parsed;
        }
        catch (JsonException)
        {
            return ApiErrorResults.Error(
                context, StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                "The request body is not valid JSON.");
        }

        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await trips.UpdateAsync(caller, tripId, body, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> AppendRouteContextAsync(
        string tripId,
        TripRouteContext request,
        HttpContext context,
        ITripService trips,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await trips.AppendRouteContextAsync(caller, tripId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return ApiErrorResults.ToResult(result.Error!, context);
        }

        // Replaying a routeContextId returns the stored snapshot with 200.
        var write = result.Value!;
        return write.Created
            ? Results.Json(write.Response, statusCode: StatusCodes.Status201Created)
            : Results.Json(write.Response);
    }

    private static async Task<IResult> GenerateDetoursAsync(
        string tripId,
        HttpContext context,
        IDetourService detours,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await detours.GenerateAsync(caller, tripId, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> ListDetoursAsync(
        string tripId,
        HttpContext context,
        IDetourService detours,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await detours.ListAsync(caller, tripId, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }
}
