using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Api;

/// <summary>
/// Device registration, token refresh, revocation, and listing
/// (design §11.2 "Device registration").
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>
    /// Maps the unauthenticated device routes. Registration is gated by the
    /// pairing code; refresh authenticates with the refresh token itself.
    /// </summary>
    public static void MapPublicEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/devices/register", RegisterAsync);
        group.MapPost("/devices/{deviceId}/refresh", RefreshAsync);
    }

    /// <summary>Maps the authenticated device routes (list, revoke).</summary>
    public static void MapAuthenticatedEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/devices", ListAsync);
        group.MapDelete("/devices/{deviceId}", RevokeAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
    }

    private static async Task<IResult> RegisterAsync(
        DeviceRegisterRequest request,
        HttpContext context,
        IDeviceService devices,
        CancellationToken cancellationToken)
    {
        // Same bound as the authenticated routes' filter; the register endpoint
        // reads the header itself because no device exists yet to scope a
        // stored record to.
        if (!IdempotencyEndpointFilter.TryReadKey(context, out var idempotencyKey, out var rejection))
        {
            return rejection!;
        }

        var result = await devices.RegisterAsync(request, idempotencyKey, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> RefreshAsync(
        string deviceId,
        TokenRefreshRequest request,
        HttpContext context,
        IDeviceService devices,
        CancellationToken cancellationToken)
    {
        var result = await devices.RefreshAsync(deviceId, request, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IDeviceService devices,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await devices.ListAsync(caller, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> RevokeAsync(
        string deviceId,
        HttpContext context,
        IDeviceService devices,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await devices.RevokeAsync(caller, deviceId, cancellationToken);
        return result.IsSuccess
            ? Results.NoContent()
            : ApiErrorResults.ToResult(result.Error!, context);
    }
}
