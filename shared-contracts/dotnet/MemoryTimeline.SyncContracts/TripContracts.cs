using System.Text.Json.Nodes;

namespace MemoryTimeline.SyncContracts;

/// <summary>
/// Wire contracts for trips, route context snapshots and detour suggestions
/// (design §6.4, §6.5, §11.2 "Trips and detours", §19 Phase 5). Mirrors the
/// Trip, TripCreateRequest, RouteContext and DetourSuggestion schemas in
/// shared-contracts/openapi/memory-line-sync-v1.yaml; TripUpdateRequest has no
/// DTO because PATCH is applied key by key from the raw JSON object, exactly
/// as the capture PATCH is (an absent key means unchanged, an explicit null
/// clears).
///
/// <para>Trips are owner-scoped like captures: any of the owner's devices may
/// read and update them. They are plain REST resources, not change-feed
/// entities — nothing here rides <c>/sync/pull</c>.</para>
///
/// <para>Privacy (§14.4/§14.5): route context is exact location and is never
/// logged; the service keeps a trip's trail for a bounded retention window
/// (§22 decision 9) and a capture's single snapshot with the capture.</para>
/// </summary>
public static class TripStatus
{
    public const string Planned = "planned";
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Archived = "archived";
}

/// <summary>Creates a trip. The server mints the tripId.</summary>
public class TripCreateRequest
{
    public string Name { get; set; } = string.Empty;

    public DateTime? StartedAtUtc { get; set; }

    public string? OriginLabel { get; set; }

    public string? DestinationLabel { get; set; }

    /// <summary>One of <see cref="TripStatus"/>; defaults to planned.</summary>
    public string Status { get; set; } = TripStatus.Planned;

    /// <summary>Free-form trip preferences (detour categories, budget, pace, ...). Stored verbatim.</summary>
    public JsonObject? PreferencesJson { get; set; }
}

/// <summary>A trip (the OpenAPI <c>Trip</c> schema).</summary>
public class TripResponse
{
    public string TripId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public string? OriginLabel { get; set; }

    public string? DestinationLabel { get; set; }

    /// <summary>One of <see cref="TripStatus"/>.</summary>
    public string Status { get; set; } = TripStatus.Planned;

    public JsonObject? PreferencesJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Point-in-time route/location snapshot for a trip (the OpenAPI
/// <c>RouteContext</c> schema; named differently here only because ASP.NET
/// Core already has a <c>RouteContext</c> type). The same shape is the request
/// and the response: <see cref="RouteContextId"/> and <see cref="TripId"/> are
/// optional on the way in — the server mints an ID when none parses as a UUID
/// and the trip comes from the path — and always present on the way out.
/// </summary>
public class TripRouteContext
{
    /// <summary>Client-generated snapshot ID; replaying it returns the stored snapshot.</summary>
    public string? RouteContextId { get; set; }

    public string? TripId { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double? HorizontalAccuracyM { get; set; }

    public double? HeadingDegrees { get; set; }

    public double? SpeedMps { get; set; }

    /// <summary>Fraction of the route completed, 0.0 to 1.0.</summary>
    public double? RouteProgress { get; set; }

    public double? RemainingDistanceM { get; set; }

    public double? RemainingDurationS { get; set; }

    public string? CurrentRoadName { get; set; }

    public DateTime? DestinationEtaUtc { get; set; }

    /// <summary>Source of the routing data, e.g. apple_maps.</summary>
    public string? Provider { get; set; }
}

/// <summary>Opening-hours verdicts a detour suggestion may carry.</summary>
public static class DetourOpenStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
    public const string ClosingSoon = "closing_soon";
    public const string Unknown = "unknown";
}

/// <summary>
/// Disposition of a detour suggestion (design §6.5). Transitions are
/// monotonic (§12.2): accepted and dismissed are terminal, expired is
/// reached only from a non-terminal state when <c>expiresAtUtc</c> passes,
/// and an accepted suggestion is never overwritten by a stale proposed state.
/// </summary>
public static class DetourSuggestionState
{
    public const string Proposed = "proposed";
    public const string Heard = "heard";
    public const string Saved = "saved";
    public const string Accepted = "accepted";
    public const string Dismissed = "dismissed";
    public const string Expired = "expired";
}

/// <summary>A verified detour suggestion (the OpenAPI <c>DetourSuggestion</c> schema).</summary>
public class DetourSuggestion
{
    public string SuggestionId { get; set; } = string.Empty;

    public string TripId { get; set; } = string.Empty;

    public DateTime GeneratedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? PlaceProvider { get; set; }

    public string? ProviderPlaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    /// <summary>Short human-readable justification for the suggestion.</summary>
    public string? Reason { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? RouteAddedDistanceM { get; set; }

    public double? RouteAddedDurationS { get; set; }

    /// <summary>One of <see cref="DetourOpenStatus"/>.</summary>
    public string? OpenStatus { get; set; }

    public string? NextOpenInterval { get; set; }

    public string? PriceSummary { get; set; }

    public string? AdmissionCurrency { get; set; }

    /// <summary>Verification confidence, 0.0 to 1.0.</summary>
    public double? Confidence { get; set; }

    /// <summary>Provenance of the verification checks. Stored verbatim.</summary>
    public JsonObject? VerificationSourcesJson { get; set; }

    /// <summary>One of <see cref="DetourSuggestionState"/>.</summary>
    public string State { get; set; } = DetourSuggestionState.Proposed;
}
