using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Api;

/// <summary>Capture metadata lifecycle routes (design §11.2 "Captures").</summary>
public static class CaptureEndpoints
{
    /// <summary>Maps the authenticated capture routes.</summary>
    public static void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/captures", CreateAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapGet("/captures/{captureId}", GetAsync);
        group.MapPatch("/captures/{captureId}", UpdateAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapPost("/captures/{captureId}/complete", CompleteAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
        group.MapPost("/captures/{captureId}/retry", RetryAsync)
            .AddEndpointFilter(IdempotencyEndpointFilter.Instance);
    }

    private static async Task<IResult> CreateAsync(
        CaptureCreateRequest request,
        HttpContext context,
        ICaptureService captures,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await captures.CreateAsync(caller, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return ApiErrorResults.ToResult(result.Error!, context);
        }

        // Replaying an existing captureId returns 200 with the current state.
        var write = result.Value!;
        return write.Created
            ? Results.Created($"/api/v1/captures/{write.Response.CaptureId}", write.Response)
            : Results.Json(write.Response);
    }

    private static async Task<IResult> GetAsync(
        string captureId,
        HttpContext context,
        ICaptureService captures,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await captures.GetAsync(caller, captureId, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> UpdateAsync(
        string captureId,
        HttpContext context,
        ICaptureService captures,
        CancellationToken cancellationToken)
    {
        // The body is parsed as a JSON object (not a DTO) so that an absent key
        // means "unchanged" while an explicit null clears a nullable field.
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
        var result = await captures.UpdateAsync(caller, captureId, body, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> CompleteAsync(
        string captureId,
        HttpContext context,
        ICaptureService captures,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await captures.CompleteAsync(caller, captureId, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }

    private static async Task<IResult> RetryAsync(
        string captureId,
        HttpContext context,
        ICaptureService captures,
        CancellationToken cancellationToken)
    {
        var caller = DeviceAuthEndpointFilter.GetDevice(context);
        var result = await captures.RetryAsync(caller, captureId, cancellationToken);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : ApiErrorResults.ToResult(result.Error!, context);
    }
}
