using System.Text.Json;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Api;

/// <summary>
/// Optional Idempotency-Key replay for authenticated mutations (design §11.4).
/// A repeated key from the same device returns the stored status code and body
/// without re-executing the handler. Successful (2xx) responses are stored;
/// failures are not, so a corrected retry re-executes. Runs after
/// <see cref="DeviceAuthEndpointFilter"/> (group filters run first).
/// </summary>
public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    /// <summary>Idempotency header name (shared-contracts OpenAPI parameter).</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Longest accepted key (the OpenAPI parameter's maxLength).</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Shared stateless instance (dependencies resolve per-request from RequestServices).</summary>
    public static readonly IdempotencyEndpointFilter Instance = new();

    /// <summary>
    /// Reads the Idempotency-Key header. <paramref name="key"/> is null when
    /// the header is absent or blank. Returns false, with <paramref name="rejection"/>
    /// set to the 400 to send, when the key exceeds <see cref="MaxKeyLength"/> —
    /// one rule for every route that honours the header, including the
    /// unauthenticated register endpoint, which cannot run this filter because
    /// no device exists yet to scope the record to.
    /// </summary>
    public static bool TryReadKey(HttpContext http, out string? key, out IResult? rejection)
    {
        rejection = null;
        var raw = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            key = null;
            return true;
        }

        if (raw.Length > MaxKeyLength)
        {
            key = null;
            rejection = ApiErrorResults.Error(
                http, StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                $"{HeaderName} must be at most {MaxKeyLength} characters.");
            return false;
        }

        key = raw;
        return true;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!TryReadKey(http, out var key, out var rejection))
        {
            return rejection;
        }

        if (key is null)
        {
            return await next(context);
        }

        if (http.Items[DeviceAuthEndpointFilter.DeviceItemKey] is not Device device)
        {
            return await next(context);
        }

        var factory = http.RequestServices.GetRequiredService<IDbContextFactory<SyncDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(http.RequestAborted))
        {
            var stored = await db.IdempotencyRecords.FindAsync([device.DeviceId, key], http.RequestAborted);
            if (stored is not null)
            {
                return stored.ResponseJson is null
                    ? Results.StatusCode(stored.StatusCode)
                    : Results.Text(stored.ResponseJson, "application/json", statusCode: stored.StatusCode);
            }
        }

        var result = await next(context);

        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        if (statusCode is < 200 or >= 300)
        {
            return result;
        }

        var value = (result as IValueHttpResult)?.Value;
        var responseJson = value is null ? null : JsonSerializer.Serialize(value, value.GetType(), SyncJson.Options);

        try
        {
            await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                DeviceId = device.DeviceId,
                IdempotencyKey = key,
                Endpoint = $"{http.Request.Method} {http.Request.Path}",
                StatusCode = statusCode,
                ResponseJson = responseJson,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent request with the same key won the insert; this
            // request already executed, so return its own (equivalent) result.
            var logger = http.RequestServices.GetRequiredService<ILogger<IdempotencyEndpointFilter>>();
            logger.LogWarning(
                ex, "Concurrent idempotency record insert for device {DeviceId}.", device.DeviceId);
        }

        return result;
    }
}
