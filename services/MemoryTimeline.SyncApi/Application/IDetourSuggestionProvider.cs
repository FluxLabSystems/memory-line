using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Produces detour candidates for a trip's current route context (design §10,
/// the detour agent). The service owns the lifecycle — persisting candidates
/// as <c>proposed</c> suggestions, expiring them, and moving them through
/// save/accept/dismiss — while a provider owns the part that needs the
/// outside world: place lookup, opening hours, route cost and price
/// verification (§10.2–§10.4), which per design §22 decision 5 means Google
/// Places inside the free tier with an Apple MapKit failover.
///
/// No provider ships with the service yet. <see cref="NoDetourSuggestionProvider"/>
/// is registered by default and yields nothing, so
/// <c>POST /trips/{id}/detours/generate</c> is honest — an empty list, not an
/// invented place — until a real adapter is configured.
/// </summary>
public interface IDetourSuggestionProvider
{
    /// <summary>Short provider name for logs and for <c>DetourSuggestion.placeProvider</c> when the candidate names none.</summary>
    string Name { get; }

    /// <summary>
    /// Returns candidates for the given context. Candidates whose
    /// <c>ProviderPlaceId</c> is in <see cref="DetourGenerationContext.ExcludedProviderPlaceIds"/>
    /// should not be returned; the service drops any that are, so a dismissed
    /// place is not suggested again (§10.5).
    /// </summary>
    Task<IReadOnlyList<DetourCandidate>> GenerateAsync(DetourGenerationContext context, CancellationToken cancellationToken);
}

/// <summary>Everything a provider gets to work with for one generation request.</summary>
public sealed record DetourGenerationContext(
    TripResponse Trip,
    TripRouteContext RouteContext,
    IReadOnlyCollection<string> ExcludedProviderPlaceIds,
    DateTime NowUtc);

/// <summary>
/// A provider's proposal, before the service gives it an ID, a trip, a
/// generation time and the <c>proposed</c> state. The fields mirror the
/// descriptive half of <see cref="DetourSuggestion"/>.
/// </summary>
public sealed class DetourCandidate
{
    public string Name { get; set; } = string.Empty;

    public string? PlaceProvider { get; set; }

    public string? ProviderPlaceId { get; set; }

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

    public System.Text.Json.Nodes.JsonObject? VerificationSourcesJson { get; set; }

    /// <summary>When the suggestion stops being actionable; the service applies a default lifetime when null.</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>The provider registered when none is configured: nothing is suggested.</summary>
public sealed class NoDetourSuggestionProvider : IDetourSuggestionProvider
{
    public string Name => "none";

    public Task<IReadOnlyList<DetourCandidate>> GenerateAsync(DetourGenerationContext context, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DetourCandidate>>([]);
}
