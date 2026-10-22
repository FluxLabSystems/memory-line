namespace MemoryTimeline.SyncApi.Domain;

/// <summary>
/// A trip (design §6.4), owner-scoped like a capture. Status follows the
/// state machine in <c>TripService</c> (§12.2 "latest valid transition
/// wins"). <see cref="PreferencesJson"/> is the client's free-form object,
/// stored verbatim as text and never interpreted here.
/// </summary>
public class TripRow
{
    /// <summary>Trip ID (GUID, "D" format), minted by the service.</summary>
    public string TripId { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public string? OriginLabel { get; set; }

    public string? DestinationLabel { get; set; }

    /// <summary>TripStatus values.</summary>
    public string Status { get; set; } = string.Empty;

    public string? PreferencesJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One route/location snapshot of a trip (design §6.4). Exact location: never
/// logged (§14.5), and swept by the retention sweep after the route-context
/// window (§22 decision 9), keeping each trip's newest snapshot so detour
/// generation on a long trip still has a position to work from.
/// </summary>
public class RouteContextRow
{
    /// <summary>Snapshot ID (GUID, "D" format) — client-supplied when it parses, minted otherwise.</summary>
    public string RouteContextId { get; set; } = string.Empty;

    public string TripId { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public DateTime CapturedAtUtc { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double? HorizontalAccuracyM { get; set; }

    public double? HeadingDegrees { get; set; }

    public double? SpeedMps { get; set; }

    public double? RouteProgress { get; set; }

    public double? RemainingDistanceM { get; set; }

    public double? RemainingDurationS { get; set; }

    public string? CurrentRoadName { get; set; }

    public DateTime? DestinationEtaUtc { get; set; }

    public string? Provider { get; set; }

    /// <summary>When the service stored it; the retention clock.</summary>
    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>
/// A detour suggestion (design §6.5) with its monotonic disposition. The
/// descriptive columns are what a provider produced and are relayed as they
/// came; only <see cref="State"/> moves, through <c>DetourService</c>.
/// </summary>
public class DetourSuggestionRow
{
    /// <summary>Suggestion ID (GUID, "D" format), minted by the service.</summary>
    public string SuggestionId { get; set; } = string.Empty;

    public string TripId { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public DateTime GeneratedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? PlaceProvider { get; set; }

    public string? ProviderPlaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? Reason { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? RouteAddedDistanceM { get; set; }

    public double? RouteAddedDurationS { get; set; }

    public string? OpenStatus { get; set; }

    public string? NextOpenInterval { get; set; }

    public string? PriceSummary { get; set; }

    public string? AdmissionCurrency { get; set; }

    public double? Confidence { get; set; }

    public string? VerificationSourcesJson { get; set; }

    /// <summary>DetourSuggestionState values.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>When <see cref="State"/> last changed; the retention clock for terminal states.</summary>
    public DateTime StateChangedAtUtc { get; set; }
}
