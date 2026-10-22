using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// <see cref="IDetourSuggestionProvider"/> over the Google Places API (New)
/// nearby search (<c>POST https://places.googleapis.com/v1/places:searchNearby</c>),
/// the default of design §22 decision 5. One request per generation, inside
/// the daily cap of <see cref="DetourRequestBudget"/>.
///
/// <para>What is relayed and what is estimated. Name, place ID, type,
/// coordinates, open-now and price level come from the provider and are
/// relayed as they came; the route delta is the straight-line estimate of
/// <see cref="DetourGeometry"/>, and every candidate's
/// <c>verificationSourcesJson</c> names which is which, so an unknown opening
/// time or price is labelled unknown rather than invented (§10.3).</para>
///
/// <para>Failures are logged by status code and answered with no candidates;
/// the key and the response body are never logged (§14.5), and a provider
/// outage must not turn a 200-with-nothing endpoint into a 500.</para>
/// </summary>
public sealed class GooglePlacesDetourProvider : IDetourSuggestionProvider
{
    /// <summary>What <c>DetourSuggestion.placeProvider</c> carries for this adapter.</summary>
    public const string ProviderName = "google_places";

    /// <summary>The nearby search endpoint.</summary>
    public static readonly Uri SearchUri = new("https://places.googleapis.com/v1/places:searchNearby");

    /// <summary>The response fields asked for — the API returns nothing without a mask.</summary>
    public const string FieldMask =
        "places.id,places.displayName,places.location,places.primaryType,places.primaryTypeDisplayName," +
        "places.types,places.currentOpeningHours.openNow,places.priceLevel,places.rating,places.userRatingCount," +
        "places.editorialSummary";

    /// <summary>The API caps a nearby search at 20 results.</summary>
    private const int MaxResultCount = 20;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DetourOptions _options;
    private readonly DetourRequestBudget _budget;
    private readonly HttpRetry _retry;
    private readonly ILogger<GooglePlacesDetourProvider> _logger;

    public GooglePlacesDetourProvider(
        IHttpClientFactory httpClientFactory,
        DetourOptions options,
        DetourRequestBudget budget,
        ILogger<GooglePlacesDetourProvider> logger)
        : this(httpClientFactory, options, budget, logger, HttpRetry.Default)
    {
    }

    /// <summary>Test seam: a retry policy whose delays do not sleep.</summary>
    public GooglePlacesDetourProvider(
        IHttpClientFactory httpClientFactory,
        DetourOptions options,
        DetourRequestBudget budget,
        ILogger<GooglePlacesDetourProvider> logger,
        HttpRetry retry)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _budget = budget;
        _logger = logger;
        _retry = retry;
    }

    public string Name => ProviderName;

    public async Task<IReadOnlyList<DetourCandidate>> GenerateAsync(DetourGenerationContext context, CancellationToken cancellationToken)
    {
        if (!_budget.TryConsume(context.NowUtc))
        {
            _logger.LogWarning(
                "Detour generation for trip {TripId} skipped: the daily request cap of {Cap} is spent (design §22 decision 5). " +
                "Raise Detours:DailyRequestCap or switch Detours:Provider to apple for the rest of the day.",
                context.Trip.TripId, _budget.DailyCap);
            return [];
        }

        var here = context.RouteContext;
        var body = new SearchNearbyRequest
        {
            IncludedTypes = _options.GooglePlaceTypes,
            MaxResultCount = MaxResultCount,
            LocationRestriction = new LocationRestriction
            {
                Circle = new Circle
                {
                    Center = new LatLng { Latitude = here.Latitude, Longitude = here.Longitude },
                    Radius = _options.SearchRadiusMeters,
                },
            },
        };

        SearchNearbyResponse? parsed;
        try
        {
            using var client = _httpClientFactory.CreateClient(DetourProviderRegistration.HttpClientName);
            using var response = await _retry.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, SearchUri)
                {
                    Content = JsonContent.Create(body, options: SyncJson.Options),
                };
                request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
                request.Headers.Add("X-Goog-FieldMask", FieldMask);
                return request;
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Status only: the body of a Places error names the project and the key's restrictions.
                _logger.LogError(
                    "Google Places nearby search for trip {TripId} answered HTTP {Status}; no suggestions generated.",
                    context.Trip.TripId, (int)response.StatusCode);
                return [];
            }

            parsed = await response.Content.ReadFromJsonAsync<SearchNearbyResponse>(SyncJson.Options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Google Places nearby search for trip {TripId} failed; no suggestions generated.", context.Trip.TripId);
            return [];
        }

        var candidates = new List<(double Distance, DetourCandidate Candidate)>();
        foreach (var place in parsed?.Places ?? [])
        {
            var name = place.DisplayName?.Text?.Trim();
            if (string.IsNullOrEmpty(name) || place.Location is null)
            {
                continue;
            }

            if (place.Id is { Length: > 0 } id && context.ExcludedProviderPlaceIds.Contains(id))
            {
                continue;
            }

            var distance = DetourGeometry.DistanceMeters(
                here.Latitude, here.Longitude, place.Location.Latitude, place.Location.Longitude);
            var added = DetourGeometry.AddedDistanceMeters(distance);
            var openNow = place.CurrentOpeningHours?.OpenNow;
            var price = PriceSummary(place.PriceLevel);
            var category = place.PrimaryTypeDisplayName?.Text ?? place.PrimaryType ?? place.Types?.FirstOrDefault();

            candidates.Add((distance, new DetourCandidate
            {
                Name = name,
                PlaceProvider = ProviderName,
                ProviderPlaceId = place.Id,
                Category = category,
                Reason = place.EditorialSummary?.Text
                    ?? $"{category ?? "A stop"} about {Math.Round(distance / 1000, 1)} km from your route.",
                Latitude = place.Location.Latitude,
                Longitude = place.Location.Longitude,
                RouteAddedDistanceM = Math.Round(added),
                RouteAddedDurationS = Math.Round(DetourGeometry.AddedDurationSeconds(added)),
                OpenStatus = openNow switch
                {
                    true => DetourOpenStatus.Open,
                    false => DetourOpenStatus.Closed,
                    null => DetourOpenStatus.Unknown,
                },
                PriceSummary = price,
                AdmissionCurrency = null,
                Confidence = Confidence(openNow is not null, price is not null, place.UserRatingCount),
                VerificationSourcesJson = new JsonObject
                {
                    ["place"] = ProviderName,
                    ["hours"] = openNow is null ? "unknown" : ProviderName,
                    ["price"] = price is null ? "unknown" : ProviderName,
                    ["route"] = DetourGeometry.RouteEstimateSource,
                },
            }));
        }

        var nearest = candidates
            .OrderBy(c => c.Distance)
            .Take(_options.MaxCandidates)
            .Select(c => c.Candidate)
            .ToList();
        _logger.LogInformation(
            "Google Places nearby search for trip {TripId} returned {Returned} place(s); {Kept} kept.",
            context.Trip.TripId, parsed?.Places?.Count ?? 0, nearest.Count);
        return nearest;
    }

    /// <summary>The Places price level enum rendered the way a card shows it; null when unknown.</summary>
    internal static string? PriceSummary(string? priceLevel) => priceLevel switch
    {
        "PRICE_LEVEL_FREE" => "Free",
        "PRICE_LEVEL_INEXPENSIVE" => "$",
        "PRICE_LEVEL_MODERATE" => "$$",
        "PRICE_LEVEL_EXPENSIVE" => "$$$",
        "PRICE_LEVEL_VERY_EXPENSIVE" => "$$$$",
        _ => null,
    };

    /// <summary>
    /// 0.5 for an identified place, plus what was actually verified (§10.3):
    /// hours 0.2, price 0.1, and 0.1 when enough people have rated it that the
    /// listing is unlikely to be stale. Never 1.0 — the route cost is estimated.
    /// </summary>
    internal static double Confidence(bool hoursKnown, bool priceKnown, int? userRatingCount)
        => Math.Round(0.5 + (hoursKnown ? 0.2 : 0) + (priceKnown ? 0.1 : 0) + (userRatingCount >= 20 ? 0.1 : 0), 2);

    // Wire shapes of the Places API (New), camelCase like every other JSON here.

    private sealed class SearchNearbyRequest
    {
        public string[] IncludedTypes { get; set; } = [];
        public int MaxResultCount { get; set; }
        public LocationRestriction LocationRestriction { get; set; } = new();
    }

    private sealed class LocationRestriction
    {
        public Circle Circle { get; set; } = new();
    }

    private sealed class Circle
    {
        public LatLng Center { get; set; } = new();
        public double Radius { get; set; }
    }

    private sealed class LatLng
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    private sealed class SearchNearbyResponse
    {
        public List<Place>? Places { get; set; }
    }

    private sealed class Place
    {
        public string? Id { get; set; }
        public LocalizedText? DisplayName { get; set; }
        public LatLng? Location { get; set; }
        public string? PrimaryType { get; set; }
        public LocalizedText? PrimaryTypeDisplayName { get; set; }
        public List<string>? Types { get; set; }
        public OpeningHours? CurrentOpeningHours { get; set; }
        public string? PriceLevel { get; set; }
        public double? Rating { get; set; }
        public int? UserRatingCount { get; set; }
        public LocalizedText? EditorialSummary { get; set; }
    }

    private sealed class LocalizedText
    {
        public string? Text { get; set; }
        public string? LanguageCode { get; set; }
    }

    private sealed class OpeningHours
    {
        public bool? OpenNow { get; set; }
    }
}
