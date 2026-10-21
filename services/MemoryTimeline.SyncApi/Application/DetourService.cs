using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="IDetourService"/> over the sync database and the configured
/// <see cref="IDetourSuggestionProvider"/>. Stateless over the context
/// factory; registered as a singleton. Logs suggestion IDs, trip IDs, counts
/// and states only — never place names or coordinates (design §14.5).
///
/// <para><b>Monotonic disposition</b> (§12.2): <c>proposed → heard → saved</c>
/// may each move to <c>accepted</c> or <c>dismissed</c>, which are terminal;
/// <c>expired</c> is reached only from a non-terminal state once
/// <c>expiresAtUtc</c> passes and is terminal too. Asking for the state a
/// suggestion already has is a no-op, so a replay is harmless; asking for a
/// move the machine does not allow is 409 — an accepted suggestion is never
/// overwritten by a stale proposed state, and a dismissed one does not come
/// back.</para>
/// </summary>
public sealed class DetourService : IDetourService
{
    private static readonly string[] ActiveStates =
    [
        DetourSuggestionState.Proposed,
        DetourSuggestionState.Heard,
        DetourSuggestionState.Saved,
    ];

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly IDetourSuggestionProvider _provider;
    private readonly SyncApiOptions _options;
    private readonly ILogger<DetourService> _logger;

    public DetourService(
        IDbContextFactory<SyncDbContext> contextFactory,
        IDetourSuggestionProvider provider,
        IOptions<SyncApiOptions> options,
        ILogger<DetourService> logger)
    {
        _contextFactory = contextFactory;
        _provider = provider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<List<DetourSuggestion>>> GenerateAsync(
        Device caller,
        string tripId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (trip, error) = await TripService.LoadTripAsync(db, caller, tripId, track: false, cancellationToken);
        if (trip is null)
        {
            return ServiceResult<List<DetourSuggestion>>.Fail(error!);
        }

        var now = DateTime.UtcNow;
        var existing = await db.DetourSuggestions
            .Where(s => s.TripId == trip.TripId)
            .ToListAsync(cancellationToken);
        ExpireStale(existing, now);

        var latestContext = await db.RouteContexts.AsNoTracking()
            .Where(r => r.TripId == trip.TripId)
            .OrderByDescending(r => r.CapturedAtUtc)
            .ThenByDescending(r => r.ReceivedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var generated = new List<DetourSuggestionRow>();
        if (latestContext is null)
        {
            _logger.LogInformation(
                "Detour generation for trip {TripId} skipped: no route context has been recorded.", trip.TripId);
        }
        else
        {
            // Every place already on the table — live, saved, taken or turned
            // down — is excluded so the same stop is never proposed twice (§10.5).
            var excluded = existing
                .Where(s => !string.IsNullOrWhiteSpace(s.ProviderPlaceId))
                .Select(s => s.ProviderPlaceId!)
                .ToHashSet(StringComparer.Ordinal);

            var context = new DetourGenerationContext(
                TripService.ToResponse(trip), TripService.ToResponse(latestContext), excluded, now);
            var candidates = await _provider.GenerateAsync(context, cancellationToken);

            foreach (var candidate in candidates)
            {
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.Name))
                {
                    continue;
                }

                if (candidate.ProviderPlaceId is { Length: > 0 } placeId && !excluded.Add(placeId))
                {
                    continue; // already suggested, or returned twice in one batch
                }

                generated.Add(new DetourSuggestionRow
                {
                    SuggestionId = Guid.NewGuid().ToString("D"),
                    TripId = trip.TripId,
                    OwnerId = caller.OwnerId,
                    GeneratedAtUtc = now,
                    ExpiresAtUtc = candidate.ExpiresAtUtc ?? now.AddHours(_options.DetourSuggestionLifetimeHours),
                    PlaceProvider = string.IsNullOrWhiteSpace(candidate.PlaceProvider) ? _provider.Name : candidate.PlaceProvider,
                    ProviderPlaceId = candidate.ProviderPlaceId,
                    Name = candidate.Name.Trim(),
                    Category = candidate.Category,
                    Reason = candidate.Reason,
                    Latitude = candidate.Latitude,
                    Longitude = candidate.Longitude,
                    RouteAddedDistanceM = candidate.RouteAddedDistanceM,
                    RouteAddedDurationS = candidate.RouteAddedDurationS,
                    OpenStatus = candidate.OpenStatus,
                    NextOpenInterval = candidate.NextOpenInterval,
                    PriceSummary = candidate.PriceSummary,
                    AdmissionCurrency = candidate.AdmissionCurrency,
                    Confidence = candidate.Confidence,
                    VerificationSourcesJson = candidate.VerificationSourcesJson?.ToJsonString(SyncJson.Options),
                    State = DetourSuggestionState.Proposed,
                    StateChangedAtUtc = now,
                });
            }

            db.DetourSuggestions.AddRange(generated);
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Detour generation for trip {TripId} produced {Count} suggestion(s) via provider {Provider}.",
            trip.TripId, generated.Count, _provider.Name);
        return ServiceResult<List<DetourSuggestion>>.Ok(
            generated.OrderByDescending(s => s.GeneratedAtUtc).ThenByDescending(s => s.SuggestionId).Select(ToResponse).ToList());
    }

    public async Task<ServiceResult<List<DetourSuggestion>>> ListAsync(
        Device caller,
        string tripId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (trip, error) = await TripService.LoadTripAsync(db, caller, tripId, track: false, cancellationToken);
        if (trip is null)
        {
            return ServiceResult<List<DetourSuggestion>>.Fail(error!);
        }

        var suggestions = await db.DetourSuggestions
            .Where(s => s.TripId == trip.TripId)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .ThenByDescending(s => s.SuggestionId)
            .ToListAsync(cancellationToken);
        if (ExpireStale(suggestions, DateTime.UtcNow) > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return ServiceResult<List<DetourSuggestion>>.Ok(suggestions.Select(ToResponse).ToList());
    }

    public async Task<ServiceResult<DetourSuggestion>> TransitionAsync(
        Device caller,
        string suggestionId,
        string targetState,
        CancellationToken cancellationToken)
    {
        var notFound = new ServiceError(
            StatusCodes.Status404NotFound, SyncApiErrorCodes.DetourNotFound, "No such detour suggestion.");
        if (!Guid.TryParse(suggestionId, out var suggestionGuid))
        {
            return ServiceResult<DetourSuggestion>.Fail(notFound);
        }

        var normalizedId = suggestionGuid.ToString("D");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var suggestion = await db.DetourSuggestions.FirstOrDefaultAsync(
            s => s.SuggestionId == normalizedId && s.OwnerId == caller.OwnerId, cancellationToken);
        if (suggestion is null)
        {
            return ServiceResult<DetourSuggestion>.Fail(notFound);
        }

        var now = DateTime.UtcNow;
        ExpireStale([suggestion], now);

        if (suggestion.State == targetState)
        {
            await db.SaveChangesAsync(cancellationToken); // persists a lazy expiry, if any; otherwise a no-op
            return ServiceResult<DetourSuggestion>.Ok(ToResponse(suggestion));
        }

        if (!IsAllowed(suggestion.State, targetState))
        {
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<DetourSuggestion>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.DetourInvalidTransition,
                $"A {suggestion.State} suggestion cannot be {targetState}.");
        }

        suggestion.State = targetState;
        suggestion.StateChangedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Detour suggestion {SuggestionId} on trip {TripId} is now {State} (device {DeviceId}).",
            suggestion.SuggestionId, suggestion.TripId, targetState, caller.DeviceId);
        return ServiceResult<DetourSuggestion>.Ok(ToResponse(suggestion));
    }

    /// <summary>The moves the state machine allows; everything else is 409.</summary>
    private static bool IsAllowed(string from, string to)
    {
        if (!ActiveStates.Contains(from))
        {
            return false; // accepted, dismissed and expired are terminal
        }

        return to switch
        {
            DetourSuggestionState.Accepted => true,
            DetourSuggestionState.Dismissed => true,
            DetourSuggestionState.Saved => from != DetourSuggestionState.Saved,
            _ => false,
        };
    }

    /// <summary>Marks live suggestions whose time has passed as expired; returns how many moved.</summary>
    private static int ExpireStale(IEnumerable<DetourSuggestionRow> suggestions, DateTime nowUtc)
    {
        var expired = 0;
        foreach (var suggestion in suggestions)
        {
            if (ActiveStates.Contains(suggestion.State) && suggestion.ExpiresAtUtc is { } expiresAt && expiresAt <= nowUtc)
            {
                suggestion.State = DetourSuggestionState.Expired;
                suggestion.StateChangedAtUtc = nowUtc;
                expired++;
            }
        }

        return expired;
    }

    private static DetourSuggestion ToResponse(DetourSuggestionRow row) => new()
    {
        SuggestionId = row.SuggestionId,
        TripId = row.TripId,
        GeneratedAtUtc = row.GeneratedAtUtc,
        ExpiresAtUtc = row.ExpiresAtUtc,
        PlaceProvider = row.PlaceProvider,
        ProviderPlaceId = row.ProviderPlaceId,
        Name = row.Name,
        Category = row.Category,
        Reason = row.Reason,
        Latitude = row.Latitude,
        Longitude = row.Longitude,
        RouteAddedDistanceM = row.RouteAddedDistanceM,
        RouteAddedDurationS = row.RouteAddedDurationS,
        OpenStatus = row.OpenStatus,
        NextOpenInterval = row.NextOpenInterval,
        PriceSummary = row.PriceSummary,
        AdmissionCurrency = row.AdmissionCurrency,
        Confidence = row.Confidence,
        VerificationSourcesJson = TripService.ParseObject(row.VerificationSourcesJson),
        State = row.State,
    };
}
