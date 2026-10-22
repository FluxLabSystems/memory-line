using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="ITripService"/> over the sync database. Stateless over the
/// context factory; registered as a singleton. Logs trip and snapshot IDs and
/// statuses only — never names, labels or coordinates (design §14.4/§14.5).
///
/// <para><b>The trip state machine</b> (§12.2 "latest valid transition wins"):
/// planned → active | archived; active → paused | completed | archived;
/// paused → active | completed | archived; completed → archived; archived is
/// terminal. Setting the status it already has is a no-op, so a replayed
/// PATCH is harmless. Moving to active stamps startedAtUtc when the trip has
/// none, and completing stamps endedAtUtc the same way — the client may still
/// send either explicitly.</para>
/// </summary>
public sealed class TripService : ITripService
{
    private const int NameMaxChars = 200;
    private const int LabelMaxChars = 500;

    private static readonly string[] AllowedStatuses =
    [
        TripStatus.Planned,
        TripStatus.Active,
        TripStatus.Paused,
        TripStatus.Completed,
        TripStatus.Archived,
    ];

    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        [TripStatus.Planned] = [TripStatus.Active, TripStatus.Archived],
        [TripStatus.Active] = [TripStatus.Paused, TripStatus.Completed, TripStatus.Archived],
        [TripStatus.Paused] = [TripStatus.Active, TripStatus.Completed, TripStatus.Archived],
        [TripStatus.Completed] = [TripStatus.Archived],
        [TripStatus.Archived] = [],
    };

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly ILogger<TripService> _logger;

    public TripService(IDbContextFactory<SyncDbContext> contextFactory, ILogger<TripService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<ServiceResult<TripResponse>> CreateAsync(
        Device caller,
        TripCreateRequest request,
        CancellationToken cancellationToken)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return Validation<TripResponse>("name is required.");
        }

        if (name.Length > NameMaxChars)
        {
            return Validation<TripResponse>($"name must be at most {NameMaxChars} characters.");
        }

        var status = Normalize(request.Status);
        if (status.Length == 0)
        {
            status = TripStatus.Planned;
        }

        if (!AllowedStatuses.Contains(status))
        {
            return Validation<TripResponse>(StatusMessage());
        }

        if (request.OriginLabel is { Length: > LabelMaxChars } || request.DestinationLabel is { Length: > LabelMaxChars })
        {
            return Validation<TripResponse>($"originLabel and destinationLabel must be at most {LabelMaxChars} characters.");
        }

        var now = DateTime.UtcNow;
        var trip = new TripRow
        {
            TripId = Guid.NewGuid().ToString("D"),
            OwnerId = caller.OwnerId,
            Name = name,
            StartedAtUtc = request.StartedAtUtc ?? (status == TripStatus.Active ? now : null),
            OriginLabel = request.OriginLabel,
            DestinationLabel = request.DestinationLabel,
            Status = status,
            PreferencesJson = request.PreferencesJson?.ToJsonString(SyncJson.Options),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.Trips.Add(trip);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Trip {TripId} created by device {DeviceId} (status {Status}).", trip.TripId, caller.DeviceId, status);
        return ServiceResult<TripResponse>.Ok(ToResponse(trip));
    }

    public async Task<ServiceResult<TripResponse>> GetAsync(Device caller, string tripId, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (trip, error) = await LoadTripAsync(db, caller, tripId, track: false, cancellationToken);
        return trip is null
            ? ServiceResult<TripResponse>.Fail(error!)
            : ServiceResult<TripResponse>.Ok(ToResponse(trip));
    }

    public async Task<ServiceResult<List<TripResponse>>> ListAsync(Device caller, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var trips = await db.Trips.AsNoTracking()
            .Where(t => t.OwnerId == caller.OwnerId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ThenByDescending(t => t.TripId)
            .ToListAsync(cancellationToken);
        return ServiceResult<List<TripResponse>>.Ok(trips.Select(ToResponse).ToList());
    }

    public async Task<ServiceResult<TripResponse>> UpdateAsync(
        Device caller,
        string tripId,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (trip, error) = await LoadTripAsync(db, caller, tripId, track: true, cancellationToken);
        if (trip is null)
        {
            return ServiceResult<TripResponse>.Fail(error!);
        }

        var changed = false;
        var now = DateTime.UtcNow;

        if (body.TryGetPropertyValue("name", out var nameNode))
        {
            if (!TryGetString(nameNode, out var rawName) || string.IsNullOrWhiteSpace(rawName))
            {
                return Validation<TripResponse>("name must be a non-empty string.");
            }

            var name = rawName.Trim();
            if (name.Length > NameMaxChars)
            {
                return Validation<TripResponse>($"name must be at most {NameMaxChars} characters.");
            }

            changed |= trip.Name != name;
            trip.Name = name;
        }

        if (body.TryGetPropertyValue("originLabel", out var originNode))
        {
            if (!TryGetString(originNode, out var origin) || origin is { Length: > LabelMaxChars })
            {
                return Validation<TripResponse>($"originLabel must be a string of at most {LabelMaxChars} characters, or null.");
            }

            changed |= trip.OriginLabel != origin;
            trip.OriginLabel = origin;
        }

        if (body.TryGetPropertyValue("destinationLabel", out var destinationNode))
        {
            if (!TryGetString(destinationNode, out var destination) || destination is { Length: > LabelMaxChars })
            {
                return Validation<TripResponse>($"destinationLabel must be a string of at most {LabelMaxChars} characters, or null.");
            }

            changed |= trip.DestinationLabel != destination;
            trip.DestinationLabel = destination;
        }

        if (body.TryGetPropertyValue("startedAtUtc", out var startedNode))
        {
            if (!TryGetDateTime(startedNode, out var started))
            {
                return Validation<TripResponse>("startedAtUtc must be an ISO 8601 date-time or null.");
            }

            changed |= trip.StartedAtUtc != started;
            trip.StartedAtUtc = started;
        }

        if (body.TryGetPropertyValue("endedAtUtc", out var endedNode))
        {
            if (!TryGetDateTime(endedNode, out var ended))
            {
                return Validation<TripResponse>("endedAtUtc must be an ISO 8601 date-time or null.");
            }

            changed |= trip.EndedAtUtc != ended;
            trip.EndedAtUtc = ended;
        }

        if (body.TryGetPropertyValue("preferencesJson", out var preferencesNode))
        {
            if (preferencesNode is not null && preferencesNode is not JsonObject)
            {
                return Validation<TripResponse>("preferencesJson must be an object or null.");
            }

            var preferences = preferencesNode?.ToJsonString(SyncJson.Options);
            changed |= trip.PreferencesJson != preferences;
            trip.PreferencesJson = preferences;
        }

        if (body.TryGetPropertyValue("status", out var statusNode))
        {
            if (!TryGetString(statusNode, out var rawStatus) || rawStatus is null)
            {
                return Validation<TripResponse>("status must be a string.");
            }

            var status = Normalize(rawStatus);
            if (!AllowedStatuses.Contains(status))
            {
                return Validation<TripResponse>(StatusMessage());
            }

            if (status != trip.Status)
            {
                if (!Transitions.TryGetValue(trip.Status, out var allowed) || !allowed.Contains(status))
                {
                    return ServiceResult<TripResponse>.Fail(
                        StatusCodes.Status409Conflict, SyncApiErrorCodes.TripInvalidTransition,
                        $"A trip cannot move from {trip.Status} to {status}.");
                }

                trip.Status = status;
                if (status == TripStatus.Active)
                {
                    trip.StartedAtUtc ??= now;
                }
                else if (status == TripStatus.Completed)
                {
                    trip.EndedAtUtc ??= now;
                }

                changed = true;
            }
        }

        if (changed)
        {
            trip.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Trip {TripId} updated by device {DeviceId} (status {Status}).", trip.TripId, caller.DeviceId, trip.Status);
        }

        return ServiceResult<TripResponse>.Ok(ToResponse(trip));
    }

    public async Task<ServiceResult<RouteContextWriteResult>> AppendRouteContextAsync(
        Device caller,
        string tripId,
        TripRouteContext request,
        CancellationToken cancellationToken)
    {
        if (request.CapturedAtUtc == default)
        {
            return Validation<RouteContextWriteResult>("capturedAtUtc is required.");
        }

        if (request.Latitude is < -90 or > 90 || double.IsNaN(request.Latitude))
        {
            return Validation<RouteContextWriteResult>("latitude must be between -90 and 90.");
        }

        if (request.Longitude is < -180 or > 180 || double.IsNaN(request.Longitude))
        {
            return Validation<RouteContextWriteResult>("longitude must be between -180 and 180.");
        }

        if (request.RouteProgress is < 0 or > 1)
        {
            return Validation<RouteContextWriteResult>("routeProgress must be between 0 and 1.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (trip, error) = await LoadTripAsync(db, caller, tripId, track: false, cancellationToken);
        if (trip is null)
        {
            return ServiceResult<RouteContextWriteResult>.Fail(error!);
        }

        // Like artifact IDs, a client-supplied snapshot ID is used only once
        // parsed and re-serialized; anything else gets a minted one.
        var snapshotId = (Guid.TryParse(request.RouteContextId, out var requested) ? requested : Guid.NewGuid()).ToString("D");

        var existing = await db.RouteContexts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RouteContextId == snapshotId, cancellationToken);
        if (existing is not null)
        {
            if (existing.TripId != trip.TripId)
            {
                return ServiceResult<RouteContextWriteResult>.Fail(
                    StatusCodes.Status409Conflict, SyncApiErrorCodes.ValidationError,
                    "This routeContextId already belongs to another trip.");
            }

            return ServiceResult<RouteContextWriteResult>.Ok(new RouteContextWriteResult(ToResponse(existing), Created: false));
        }

        var row = new RouteContextRow
        {
            RouteContextId = snapshotId,
            TripId = trip.TripId,
            OwnerId = caller.OwnerId,
            CapturedAtUtc = request.CapturedAtUtc,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            HorizontalAccuracyM = request.HorizontalAccuracyM,
            HeadingDegrees = request.HeadingDegrees,
            SpeedMps = request.SpeedMps,
            RouteProgress = request.RouteProgress,
            RemainingDistanceM = request.RemainingDistanceM,
            RemainingDurationS = request.RemainingDurationS,
            CurrentRoadName = request.CurrentRoadName,
            DestinationEtaUtc = request.DestinationEtaUtc,
            Provider = request.Provider,
            ReceivedAtUtc = DateTime.UtcNow,
        };
        db.RouteContexts.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a concurrent append of the same snapshot ID: replay the winner.
            await using var retryDb = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var winner = await retryDb.RouteContexts.AsNoTracking()
                .FirstOrDefaultAsync(r => r.RouteContextId == snapshotId && r.TripId == trip.TripId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return ServiceResult<RouteContextWriteResult>.Ok(new RouteContextWriteResult(ToResponse(winner), Created: false));
        }

        // The snapshot ID and the trip only — the coordinates are exact location (§14.5).
        _logger.LogDebug("Route context {RouteContextId} stored on trip {TripId}.", snapshotId, trip.TripId);
        return ServiceResult<RouteContextWriteResult>.Ok(new RouteContextWriteResult(ToResponse(row), Created: true));
    }

    /// <summary>Maps a trip row to the wire response, rehydrating the stored preferences object.</summary>
    public static TripResponse ToResponse(TripRow trip) => new()
    {
        TripId = trip.TripId,
        Name = trip.Name,
        StartedAtUtc = trip.StartedAtUtc,
        EndedAtUtc = trip.EndedAtUtc,
        OriginLabel = trip.OriginLabel,
        DestinationLabel = trip.DestinationLabel,
        Status = trip.Status,
        PreferencesJson = ParseObject(trip.PreferencesJson),
        CreatedAtUtc = trip.CreatedAtUtc,
        UpdatedAtUtc = trip.UpdatedAtUtc,
    };

    /// <summary>Maps a snapshot row to the wire shape.</summary>
    public static TripRouteContext ToResponse(RouteContextRow row) => new()
    {
        RouteContextId = row.RouteContextId,
        TripId = row.TripId,
        CapturedAtUtc = row.CapturedAtUtc,
        Latitude = row.Latitude,
        Longitude = row.Longitude,
        HorizontalAccuracyM = row.HorizontalAccuracyM,
        HeadingDegrees = row.HeadingDegrees,
        SpeedMps = row.SpeedMps,
        RouteProgress = row.RouteProgress,
        RemainingDistanceM = row.RemainingDistanceM,
        RemainingDurationS = row.RemainingDurationS,
        CurrentRoadName = row.CurrentRoadName,
        DestinationEtaUtc = row.DestinationEtaUtc,
        Provider = row.Provider,
    };

    /// <summary>
    /// Rehydrates a stored JSON object. Written by this service after the
    /// request deserialized it as an object, so a parse failure means the
    /// row was corrupted; the trip still reads, without its preferences.
    /// </summary>
    internal static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Loads one of the caller's owner's trips; other owners' trips are a plain 404.</summary>
    internal static async Task<(TripRow? Trip, ServiceError? Error)> LoadTripAsync(
        SyncDbContext db,
        Device caller,
        string tripId,
        bool track,
        CancellationToken cancellationToken)
    {
        var notFound = new ServiceError(
            StatusCodes.Status404NotFound, SyncApiErrorCodes.TripNotFound, "No such trip.");

        if (!Guid.TryParse(tripId, out var tripGuid))
        {
            return (null, notFound);
        }

        var normalizedId = tripGuid.ToString("D");
        var query = track ? db.Trips : db.Trips.AsNoTracking();
        var trip = await query.FirstOrDefaultAsync(
            t => t.TripId == normalizedId && t.OwnerId == caller.OwnerId, cancellationToken);
        return trip is null ? (null, notFound) : (trip, null);
    }

    private static string StatusMessage()
        => "status must be one of: planned, active, paused, completed, archived.";

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static ServiceResult<T> Validation<T>(string message)
        => ServiceResult<T>.Fail(StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError, message);

    /// <summary>Reads a JSON node expected to be a string or null; false when it is another type.</summary>
    private static bool TryGetString(JsonNode? node, out string? value)
    {
        if (node is null)
        {
            value = null;
            return true;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Reads a JSON node expected to be an ISO 8601 date-time string or null.</summary>
    private static bool TryGetDateTime(JsonNode? node, out DateTime? value)
    {
        if (node is null)
        {
            value = null;
            return true;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<DateTime>(out var parsed))
        {
            value = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            return true;
        }

        value = null;
        return false;
    }
}
