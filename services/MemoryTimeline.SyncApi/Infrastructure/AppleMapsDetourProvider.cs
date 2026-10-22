using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// <see cref="IDetourSuggestionProvider"/> over the Apple Maps Server API
/// search (<c>GET https://maps-api.apple.com/v1/search</c>), the selectable
/// alternative of design §22 decision 5 and the failover once Google's cap
/// is spent.
///
/// <para>Two calls, one credential. The configured key is the developer's
/// Maps authorization token; it is exchanged at <c>GET /v1/token</c> for a
/// short-lived access token (the exchange is cached until shortly before it
/// expires and does not count against the daily cap), and the access token
/// authorizes the search. Only the search is budgeted.</para>
///
/// <para>Less is verified here than with Google. A search result carries a
/// name, coordinates, a point-of-interest category and an address — no
/// opening hours and no price — so every candidate's <c>openStatus</c> is
/// <c>unknown</c>, <c>priceSummary</c> is null, and the confidence says so
/// (§10.3: unknown is labelled unknown, never invented).</para>
/// </summary>
public sealed class AppleMapsDetourProvider : IDetourSuggestionProvider
{
    /// <summary>What <c>DetourSuggestion.placeProvider</c> carries for this adapter.</summary>
    public const string ProviderName = "apple_maps";

    public static readonly Uri TokenUri = new("https://maps-api.apple.com/v1/token");
    public static readonly Uri SearchUri = new("https://maps-api.apple.com/v1/search");

    /// <summary>Renew the access token this long before the API says it expires.</summary>
    private static readonly TimeSpan TokenRenewalSlack = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DetourOptions _options;
    private readonly DetourRequestBudget _budget;
    private readonly HttpRetry _retry;
    private readonly ILogger<AppleMapsDetourProvider> _logger;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessTokenExpiresAtUtc;

    public AppleMapsDetourProvider(
        IHttpClientFactory httpClientFactory,
        DetourOptions options,
        DetourRequestBudget budget,
        ILogger<AppleMapsDetourProvider> logger)
        : this(httpClientFactory, options, budget, logger, HttpRetry.Default)
    {
    }

    /// <summary>Test seam: a retry policy whose delays do not sleep.</summary>
    public AppleMapsDetourProvider(
        IHttpClientFactory httpClientFactory,
        DetourOptions options,
        DetourRequestBudget budget,
        ILogger<AppleMapsDetourProvider> logger,
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
                "Raise Detours:DailyRequestCap to continue today.",
                context.Trip.TripId, _budget.DailyCap);
            return [];
        }

        var here = context.RouteContext;
        SearchResponse? parsed;
        try
        {
            using var client = _httpClientFactory.CreateClient(DetourProviderRegistration.HttpClientName);
            var accessToken = await AccessTokenAsync(client, context.NowUtc, cancellationToken);
            if (accessToken is null)
            {
                return [];
            }

            var box = DetourGeometry.BoundingBox(here.Latitude, here.Longitude, _options.SearchRadiusMeters);
            var query = new Dictionary<string, string>
            {
                ["q"] = _options.AppleSearchQuery,
                ["searchLocation"] = $"{Coordinate(here.Latitude)},{Coordinate(here.Longitude)}",
                ["searchRegion"] = $"{Coordinate(box.North)},{Coordinate(box.East)},{Coordinate(box.South)},{Coordinate(box.West)}",
                ["resultTypeFilter"] = "Poi",
                ["lang"] = "en-US",
            };
            if (_options.ApplePoiCategories is { Length: > 0 })
            {
                query["includePoiCategories"] = string.Join(",", _options.ApplePoiCategories);
            }

            var searchUri = new Uri(SearchUri + "?" + string.Join("&",
                query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));

            using var response = await _retry.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, searchUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return request;
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Apple Maps search for trip {TripId} answered HTTP {Status}; no suggestions generated.",
                    context.Trip.TripId, (int)response.StatusCode);
                if ((int)response.StatusCode == 401)
                {
                    _accessToken = null; // force a fresh exchange next time
                }

                return [];
            }

            parsed = await response.Content.ReadFromJsonAsync<SearchResponse>(SyncJson.Options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Apple Maps search for trip {TripId} failed; no suggestions generated.", context.Trip.TripId);
            return [];
        }

        var candidates = new List<(double Distance, DetourCandidate Candidate)>();
        foreach (var place in parsed?.Results ?? [])
        {
            var name = place.Name?.Trim();
            if (string.IsNullOrEmpty(name) || place.Coordinate is null)
            {
                continue;
            }

            // The search result's own ID when the API returns one; otherwise a
            // deterministic stand-in so a dismissed place still stays dismissed.
            var placeId = string.IsNullOrWhiteSpace(place.Id)
                ? SyntheticPlaceId(name, place.Coordinate.Latitude, place.Coordinate.Longitude)
                : place.Id;
            if (context.ExcludedProviderPlaceIds.Contains(placeId))
            {
                continue;
            }

            var distance = DetourGeometry.DistanceMeters(
                here.Latitude, here.Longitude, place.Coordinate.Latitude, place.Coordinate.Longitude);
            var added = DetourGeometry.AddedDistanceMeters(distance);
            var address = place.FormattedAddressLines?.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

            candidates.Add((distance, new DetourCandidate
            {
                Name = name,
                PlaceProvider = ProviderName,
                ProviderPlaceId = placeId,
                Category = place.PoiCategory,
                Reason = address is null
                    ? $"{place.PoiCategory ?? "A stop"} about {Math.Round(distance / 1000, 1)} km from your route."
                    : $"{place.PoiCategory ?? "A stop"} near {address}, about {Math.Round(distance / 1000, 1)} km from your route.",
                Latitude = place.Coordinate.Latitude,
                Longitude = place.Coordinate.Longitude,
                RouteAddedDistanceM = Math.Round(added),
                RouteAddedDurationS = Math.Round(DetourGeometry.AddedDurationSeconds(added)),
                OpenStatus = DetourOpenStatus.Unknown,
                PriceSummary = null,
                AdmissionCurrency = null,
                Confidence = 0.4,
                VerificationSourcesJson = new JsonObject
                {
                    ["place"] = ProviderName,
                    ["hours"] = "unknown",
                    ["price"] = "unknown",
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
            "Apple Maps search for trip {TripId} returned {Returned} place(s); {Kept} kept.",
            context.Trip.TripId, parsed?.Results?.Count ?? 0, nearest.Count);
        return nearest;
    }

    /// <summary>The ID used when a result carries none: the name and the coordinates to ~10 m.</summary>
    internal static string SyntheticPlaceId(string name, double latitude, double longitude)
        => $"{ProviderName}:{name.ToLowerInvariant()}@{latitude.ToString("F4", CultureInfo.InvariantCulture)},{longitude.ToString("F4", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The cached access token, exchanging the configured authorization token
    /// for a new one when none is held or the held one is about to expire.
    /// Returns null (after logging) when the exchange fails.
    /// </summary>
    private async Task<string?> AccessTokenAsync(HttpClient client, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && nowUtc < _accessTokenExpiresAtUtc - TokenRenewalSlack)
            {
                return _accessToken;
            }

            using var response = await _retry.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, TokenUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                return request;
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Apple Maps token exchange answered HTTP {Status}; check Detours:ApiKey. No suggestions generated.",
                    (int)response.StatusCode);
                return null;
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(SyncJson.Options, cancellationToken);
            if (token?.AccessToken is not { Length: > 0 } accessToken)
            {
                _logger.LogError("Apple Maps token exchange returned no access token; no suggestions generated.");
                return null;
            }

            _accessToken = accessToken;
            _accessTokenExpiresAtUtc = nowUtc.AddSeconds(Math.Max(60, token.ExpiresInSeconds));
            return accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static string Coordinate(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    // Wire shapes of the Maps Server API, camelCase like every other JSON here.

    private sealed class TokenResponse
    {
        public string? AccessToken { get; set; }
        public int ExpiresInSeconds { get; set; }
    }

    private sealed class SearchResponse
    {
        public List<Place>? Results { get; set; }
    }

    private sealed class Place
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public PlaceCoordinate? Coordinate { get; set; }
        public string? PoiCategory { get; set; }
        public List<string>? FormattedAddressLines { get; set; }
        public string? Country { get; set; }
        public string? CountryCode { get; set; }
    }

    private sealed class PlaceCoordinate
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
