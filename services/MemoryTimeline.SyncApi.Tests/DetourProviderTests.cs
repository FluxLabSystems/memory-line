using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The Google Places (New) adapter against a stub transport: the request it
/// sends is the documented one (endpoint, key and field-mask headers, nearby
/// search body), the documented response shape maps onto candidates with
/// unknowns labelled unknown, places already on the table are dropped,
/// transient failures are retried while client errors are not, and the daily
/// request cap of design §22 decision 5 stops it cold until the day rolls over.
/// </summary>
public class GooglePlacesDetourProviderTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc);

    private const string TwoPlaces = """
        {"places":[
          {"id":"ChIJ-museum","displayName":{"text":"Aviation Museum","languageCode":"en"},
           "location":{"latitude":39.7500,"longitude":-104.9900},
           "primaryType":"museum","primaryTypeDisplayName":{"text":"Museum","languageCode":"en"},
           "types":["museum","tourist_attraction","point_of_interest"],
           "currentOpeningHours":{"openNow":true,"weekdayDescriptions":["Monday: 9:00 AM – 5:00 PM"]},
           "priceLevel":"PRICE_LEVEL_MODERATE","rating":4.6,"userRatingCount":150,
           "editorialSummary":{"text":"Restored Cold War aircraft.","languageCode":"en"}},
          {"id":"ChIJ-diner","displayName":{"text":"Roadside Diner","languageCode":"en"},
           "location":{"latitude":39.7400,"longitude":-104.9950},
           "primaryType":"restaurant","types":["restaurant","food"]}
        ]}
        """;

    [Fact]
    public async Task MapsPlacesIntoCandidates_AndSendsTheDocumentedRequest()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, TwoPlaces);
        var options = new DetourOptions { Provider = "google", ApiKey = "test-key" };
        var provider = Provider(stub, options);

        var candidates = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);

        var request = stub.Requests.Should().ContainSingle().Which;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(GooglePlacesDetourProvider.SearchUri);
        request.Headers["X-Goog-Api-Key"].Should().Be("test-key");
        request.Headers["X-Goog-FieldMask"].Should().Be(GooglePlacesDetourProvider.FieldMask);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        body["includedTypes"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal(options.GooglePlaceTypes);
        body["maxResultCount"]!.GetValue<int>().Should().Be(20);
        var circle = body["locationRestriction"]!["circle"]!;
        circle["center"]!["latitude"]!.GetValue<double>().Should().Be(39.7392);
        circle["center"]!["longitude"]!.GetValue<double>().Should().Be(-104.9903);
        circle["radius"]!.GetValue<double>().Should().Be(options.SearchRadiusMeters);

        // Nearest first: the diner is a few hundred metres away, the museum over a kilometre.
        candidates.Select(c => c.ProviderPlaceId).Should().Equal("ChIJ-diner", "ChIJ-museum");
        candidates.Should().OnlyContain(c => c.PlaceProvider == GooglePlacesDetourProvider.ProviderName);

        var museum = candidates.Single(c => c.ProviderPlaceId == "ChIJ-museum");
        museum.Name.Should().Be("Aviation Museum");
        museum.Category.Should().Be("Museum");
        museum.Reason.Should().Be("Restored Cold War aircraft.");
        museum.Latitude.Should().Be(39.75);
        museum.Longitude.Should().Be(-104.99);
        museum.OpenStatus.Should().Be(DetourOpenStatus.Open);
        museum.PriceSummary.Should().Be("$$");
        museum.AdmissionCurrency.Should().BeNull("the price level carries no currency");
        museum.Confidence.Should().Be(0.9);
        var expectedDistance = DetourGeometry.DistanceMeters(39.7392, -104.9903, 39.75, -104.99);
        museum.RouteAddedDistanceM.Should().Be(Math.Round(2 * expectedDistance));
        museum.RouteAddedDurationS.Should().Be(Math.Round(2 * expectedDistance / DetourGeometry.AssumedSpeedMetersPerSecond));
        museum.VerificationSourcesJson!["hours"]!.GetValue<string>().Should().Be(GooglePlacesDetourProvider.ProviderName);
        museum.VerificationSourcesJson["price"]!.GetValue<string>().Should().Be(GooglePlacesDetourProvider.ProviderName);
        museum.VerificationSourcesJson["route"]!.GetValue<string>().Should().Be(DetourGeometry.RouteEstimateSource);
        museum.ExpiresAtUtc.Should().BeNull("the service applies its default lifetime");

        // No hours and no price level: unknown, not invented (design §10.3).
        var diner = candidates.Single(c => c.ProviderPlaceId == "ChIJ-diner");
        diner.OpenStatus.Should().Be(DetourOpenStatus.Unknown);
        diner.PriceSummary.Should().BeNull();
        diner.Confidence.Should().Be(0.5);
        diner.Category.Should().Be("restaurant", "the primary type stands in when no display name is sent");
        diner.VerificationSourcesJson!["hours"]!.GetValue<string>().Should().Be("unknown");
        diner.VerificationSourcesJson["price"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public async Task ExcludesPlacesAlreadyOnTheTable()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, TwoPlaces);
        var provider = Provider(stub, new DetourOptions { Provider = "google", ApiKey = "k" });

        var candidates = await provider.GenerateAsync(
            Context(39.7392, -104.9903, "ChIJ-museum"), CancellationToken.None);

        candidates.Should().ContainSingle().Which.ProviderPlaceId.Should().Be("ChIJ-diner");
    }

    [Fact]
    public async Task RetriesATransientFailureThenSucceeds()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.ServiceUnavailable);
        stub.Enqueue(HttpStatusCode.OK, TwoPlaces);
        var provider = Provider(stub, new DetourOptions { Provider = "google", ApiKey = "k", MaxCandidates = 1 });

        var candidates = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);

        stub.Requests.Should().HaveCount(2, "a 503 is retried once the back-off elapses");
        candidates.Should().ContainSingle().Which.ProviderPlaceId.Should().Be("ChIJ-diner");
    }

    [Fact]
    public async Task DoesNotRetryAClientError_AndYieldsNothing()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"bad field mask"}}""");
        var provider = Provider(stub, new DetourOptions { Provider = "google", ApiKey = "k" });

        var candidates = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);

        stub.Requests.Should().ContainSingle("a 400 will not get better by asking again");
        candidates.Should().BeEmpty("a provider failure is an honest empty answer, not an invented place");
    }

    [Fact]
    public async Task StopsAtTheDailyCap_UntilTheDayRollsOver()
    {
        var stub = new StubHttpMessageHandler();
        stub.Fallback = _ => StubHttpMessageHandler.Response(HttpStatusCode.OK, TwoPlaces);
        var budget = new DetourRequestBudget(dailyCap: 1);
        var provider = Provider(stub, new DetourOptions { Provider = "google", ApiKey = "k" }, budget);

        (await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None)).Should().HaveCount(2);
        stub.Requests.Should().HaveCount(1);

        // The cap is spent for the day: nothing is sent and nothing is suggested.
        (await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None)).Should().BeEmpty();
        stub.Requests.Should().HaveCount(1, "a capped provider must not call out");
        budget.Spent(Now).Should().Be(1);

        // Tomorrow is a fresh budget.
        var tomorrow = Context(39.7392, -104.9903, nowUtc: Now.AddDays(1));
        (await provider.GenerateAsync(tomorrow, CancellationToken.None)).Should().HaveCount(2);
        stub.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void Budget_TreatsZeroAsUncapped()
    {
        var budget = new DetourRequestBudget(dailyCap: 0);
        for (var i = 0; i < 1000; i++)
        {
            budget.TryConsume(Now).Should().BeTrue();
        }

        budget.Spent(Now).Should().Be(1000);
        budget.Spent(Now.AddDays(1)).Should().Be(0);
    }

    private static GooglePlacesDetourProvider Provider(
        StubHttpMessageHandler stub, DetourOptions options, DetourRequestBudget? budget = null)
        => new(
            new StubHttpClientFactory(stub),
            options,
            budget ?? new DetourRequestBudget(options.DailyRequestCap),
            NullLogger<GooglePlacesDetourProvider>.Instance,
            DetourTestSupport.NoDelayRetry);

    private static DetourGenerationContext Context(double latitude, double longitude, params string[] excluded)
        => Context(latitude, longitude, Now, excluded);

    private static DetourGenerationContext Context(double latitude, double longitude, DateTime nowUtc, params string[] excluded)
        => DetourTestSupport.Context(latitude, longitude, nowUtc, excluded);
}

/// <summary>
/// The Apple Maps Server API adapter against a stub transport: the configured
/// authorization token is exchanged once for an access token that then
/// authorizes the searches (the exchange is cached and not budgeted), the
/// documented search response maps onto candidates whose hours and price are
/// honestly unknown, a result without an ID gets a deterministic one so
/// exclusion still works, and a refused exchange yields nothing.
/// </summary>
public class AppleMapsDetourProviderTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc);

    private const string Token = """{"accessToken":"access-1","expiresInSeconds":1800}""";

    private const string TwoResults = """
        {"displayMapRegion":{"southLatitude":39.6,"westLongitude":-105.1,"northLatitude":39.9,"eastLongitude":-104.8},
         "results":[
          {"id":"I2E9F8C","coordinate":{"latitude":39.7500,"longitude":-104.9900},
           "displayMapRegion":{"southLatitude":39.74,"westLongitude":-105.0,"northLatitude":39.76,"eastLongitude":-104.98},
           "name":"Aviation Museum",
           "formattedAddressLines":["7711 E Academy Blvd","Denver, CO 80230","United States"],
           "structuredAddress":{"administrativeArea":"Colorado","locality":"Denver"},
           "country":"United States","countryCode":"US","poiCategory":"Museum"},
          {"coordinate":{"latitude":39.7400,"longitude":-104.9950},"name":"Roadside Diner",
           "formattedAddressLines":["1 Main St"],"poiCategory":"Restaurant"}
        ]}
        """;

    [Fact]
    public async Task ExchangesTheAuthorizationTokenOnce_AndSearchesWithIt()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, Token);
        stub.Enqueue(HttpStatusCode.OK, TwoResults);
        stub.Enqueue(HttpStatusCode.OK, TwoResults);
        var options = new DetourOptions { Provider = "apple", ApiKey = "dev-jwt" };
        var budget = new DetourRequestBudget(options.DailyRequestCap);
        var provider = Provider(stub, options, budget);

        var first = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);
        var second = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        stub.Requests.Should().HaveCount(3, "one exchange, then one search per generation");

        var exchange = stub.Requests[0];
        exchange.Method.Should().Be(HttpMethod.Get);
        exchange.Uri.Should().Be(AppleMapsDetourProvider.TokenUri);
        exchange.Headers["Authorization"].Should().Be("Bearer dev-jwt");

        foreach (var search in stub.Requests.Skip(1))
        {
            search.Method.Should().Be(HttpMethod.Get);
            search.Uri.GetLeftPart(UriPartial.Path).Should().Be(AppleMapsDetourProvider.SearchUri.ToString());
            search.Headers["Authorization"].Should().Be("Bearer access-1", "the exchanged token authorizes the search");
            var query = System.Web.HttpUtility.ParseQueryString(search.Uri.Query);
            query["q"].Should().Be(options.AppleSearchQuery);
            query["searchLocation"].Should().Be("39.739200,-104.990300");
            query["resultTypeFilter"].Should().Be("Poi");
            query["includePoiCategories"].Should().Be(string.Join(",", options.ApplePoiCategories));
            query["searchRegion"]!.Split(',').Should().HaveCount(4, "north,east,south,west");
        }

        budget.Spent(Now).Should().Be(2, "the token exchange is not a search and is not budgeted");
    }

    [Fact]
    public async Task MapsResults_WithUnknownHoursAndPrice_AndASyntheticIdWhenNoneIsReturned()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, Token);
        stub.Enqueue(HttpStatusCode.OK, TwoResults);
        var provider = Provider(stub, new DetourOptions { Provider = "apple", ApiKey = "dev-jwt" });

        var candidates = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);

        candidates.Select(c => c.ProviderPlaceId).Should().Equal(
            "apple_maps:roadside diner@39.7400,-104.9950", "I2E9F8C");
        candidates.Should().OnlyContain(c => c.PlaceProvider == AppleMapsDetourProvider.ProviderName);
        candidates.Should().OnlyContain(c => c.OpenStatus == DetourOpenStatus.Unknown);
        candidates.Should().OnlyContain(c => c.PriceSummary == null);
        candidates.Should().OnlyContain(c => c.Confidence == 0.4);

        var museum = candidates.Single(c => c.ProviderPlaceId == "I2E9F8C");
        museum.Name.Should().Be("Aviation Museum");
        museum.Category.Should().Be("Museum");
        museum.Reason.Should().Contain("7711 E Academy Blvd");
        museum.Latitude.Should().Be(39.75);
        museum.Longitude.Should().Be(-104.99);
        museum.RouteAddedDistanceM.Should().Be(
            Math.Round(2 * DetourGeometry.DistanceMeters(39.7392, -104.9903, 39.75, -104.99)));
        museum.VerificationSourcesJson!["hours"]!.GetValue<string>().Should().Be("unknown");
        museum.VerificationSourcesJson["price"]!.GetValue<string>().Should().Be("unknown");
        museum.VerificationSourcesJson["route"]!.GetValue<string>().Should().Be(DetourGeometry.RouteEstimateSource);

        // Dismissed once, excluded from then on — even without a provider ID.
        stub.Enqueue(HttpStatusCode.OK, TwoResults);
        var again = await provider.GenerateAsync(
            Context(39.7392, -104.9903, "apple_maps:roadside diner@39.7400,-104.9950"), CancellationToken.None);
        again.Should().ContainSingle().Which.ProviderPlaceId.Should().Be("I2E9F8C");
    }

    [Fact]
    public async Task AFailedTokenExchange_YieldsNothingAndSearchesNothing()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.Unauthorized, """{"error":{"message":"Not Authorized","details":[]}}""");
        var provider = Provider(stub, new DetourOptions { Provider = "apple", ApiKey = "bad-jwt" });

        var candidates = await provider.GenerateAsync(Context(39.7392, -104.9903), CancellationToken.None);

        candidates.Should().BeEmpty();
        stub.Requests.Should().ContainSingle().Which.Uri.Should().Be(AppleMapsDetourProvider.TokenUri);
    }

    private static AppleMapsDetourProvider Provider(
        StubHttpMessageHandler stub, DetourOptions options, DetourRequestBudget? budget = null)
        => new(
            new StubHttpClientFactory(stub),
            options,
            budget ?? new DetourRequestBudget(options.DailyRequestCap),
            NullLogger<AppleMapsDetourProvider>.Instance,
            DetourTestSupport.NoDelayRetry);

    private static DetourGenerationContext Context(double latitude, double longitude, params string[] excluded)
        => DetourTestSupport.Context(latitude, longitude, Now, excluded);
}

/// <summary>
/// Provider selection from configuration: nothing configured means the honest
/// "none" provider, a name selects its adapter, and a misconfiguration — an
/// unknown name, or a provider without its credential — refuses to register
/// rather than quietly becoming "none".
/// </summary>
public class DetourProviderRegistrationTests
{
    [Fact]
    public void DefaultsToTheNoneProvider()
    {
        Resolve(new DetourOptions()).Should().BeOfType<NoDetourSuggestionProvider>();
    }

    [Fact]
    public void SelectsGoogleAndAppleByName_CaseInsensitively()
    {
        Resolve(new DetourOptions { Provider = "Google", ApiKey = "k" }).Should().BeOfType<GooglePlacesDetourProvider>();
        Resolve(new DetourOptions { Provider = "APPLE", ApiKey = "k" }).Should().BeOfType<AppleMapsDetourProvider>();
    }

    [Fact]
    public void RefusesAProviderWithoutItsKey_AndAnUnknownProvider()
    {
        var noKey = () => Resolve(new DetourOptions { Provider = "google" });
        noKey.Should().Throw<InvalidOperationException>().WithMessage("*Detours:ApiKey is required*google*");

        var unknown = () => Resolve(new DetourOptions { Provider = "bing", ApiKey = "k" });
        unknown.Should().Throw<InvalidOperationException>().WithMessage("*Detours:Provider must be one of: none, google, apple*");

        var badCap = () => Resolve(new DetourOptions { DailyRequestCap = -1 });
        badCap.Should().Throw<InvalidOperationException>();
    }

    private static IDetourSuggestionProvider Resolve(DetourOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDetourSuggestionProvider(options);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IDetourSuggestionProvider>();
    }
}

/// <summary>
/// The configured provider through the real host: with <c>Detours:Provider</c>
/// set to google and a key, <c>POST /trips/{id}/detours/generate</c> calls the
/// Places API (here a stub behind the named HttpClient) and the service turns
/// what comes back into proposed suggestions, excluding them on the next run.
/// </summary>
public class ConfiguredDetourProviderFlowTests : IClassFixture<GoogleDetourSyncApiFixture>
{
    private readonly GoogleDetourSyncApiFixture _fixture;

    public ConfiguredDetourProviderFlowTests(GoogleDetourSyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Detours_GenerateThroughTheHost_UsesTheConfiguredGoogleProvider()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await TripFlowTests.CreateTripAsync(client, "Configured provider");
        (await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
                new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 39.7392, Longitude = -104.9903 }, SyncApiFixture.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var generate = await client.PostAsync($"/api/v1/trips/{trip.TripId}/detours/generate", content: null);
        generate.StatusCode.Should().Be(HttpStatusCode.OK);
        var generated = (await generate.Content.ReadFromJsonAsync<List<DetourSuggestion>>(SyncApiFixture.Json))!;

        var suggestion = generated.Should().ContainSingle().Which;
        suggestion.PlaceProvider.Should().Be(GooglePlacesDetourProvider.ProviderName);
        suggestion.ProviderPlaceId.Should().Be("ChIJ-museum");
        suggestion.Name.Should().Be("Aviation Museum");
        suggestion.State.Should().Be(DetourSuggestionState.Proposed);
        suggestion.OpenStatus.Should().Be(DetourOpenStatus.Open);
        suggestion.ExpiresAtUtc.Should().NotBeNull("the service applied its default lifetime");
        suggestion.VerificationSourcesJson!["route"]!.GetValue<string>().Should().Be(DetourGeometry.RouteEstimateSource);

        var request = _fixture.Stub.Requests.Should().ContainSingle().Which;
        request.Uri.Should().Be(GooglePlacesDetourProvider.SearchUri);
        request.Headers["X-Goog-Api-Key"].Should().Be("test-key");

        // The same place is not proposed twice.
        var again = await client.PostAsync($"/api/v1/trips/{trip.TripId}/detours/generate", content: null);
        (await again.Content.ReadFromJsonAsync<List<DetourSuggestion>>(SyncApiFixture.Json))!.Should().BeEmpty();
        _fixture.Stub.Requests.Should().HaveCount(2);
    }
}

/// <summary>Host configured for the Google provider, with the provider's HttpClient routed to a stub.</summary>
public sealed class GoogleDetourSyncApiFixture : SyncApiFixture
{
    private const string OnePlace = """
        {"places":[{"id":"ChIJ-museum","displayName":{"text":"Aviation Museum","languageCode":"en"},
          "location":{"latitude":39.7500,"longitude":-104.9900},"primaryType":"museum",
          "primaryTypeDisplayName":{"text":"Museum","languageCode":"en"},"types":["museum"],
          "currentOpeningHours":{"openNow":true},"priceLevel":"PRICE_LEVEL_INEXPENSIVE","userRatingCount":40}]}
        """;

    public StubHttpMessageHandler Stub { get; }

    public GoogleDetourSyncApiFixture()
        : this(new StubHttpMessageHandler { Fallback = _ => StubHttpMessageHandler.Response(HttpStatusCode.OK, OnePlace) })
    {
    }

    private GoogleDetourSyncApiFixture(StubHttpMessageHandler stub)
        : base(
            extraSettings: new Dictionary<string, string>
            {
                ["Detours:Provider"] = "google",
                ["Detours:ApiKey"] = "test-key",
            },
            configureServices: services => services
                .AddHttpClient(DetourProviderRegistration.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => stub))
    {
        Stub = stub;
    }
}

/// <summary>Shared builders for the provider tests.</summary>
internal static class DetourTestSupport
{
    /// <summary>A retry policy that counts attempts but never sleeps.</summary>
    public static readonly HttpRetry NoDelayRetry = new((_, _) => Task.CompletedTask);

    public static DetourGenerationContext Context(double latitude, double longitude, DateTime nowUtc, params string[] excluded)
        => new(
            new TripResponse { TripId = Guid.NewGuid().ToString("D"), Name = "Provider test", Status = TripStatus.Active },
            new TripRouteContext { CapturedAtUtc = nowUtc, Latitude = latitude, Longitude = longitude },
            excluded,
            nowUtc);
}

/// <summary>Hands the provider an HttpClient over the stub transport; the stub outlives the clients.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// Records every request (method, URI, headers, body) and answers from a
/// script of responses, then from <see cref="Fallback"/>, then 404. Each
/// response is built fresh because callers dispose what they receive.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

    private readonly Queue<Func<HttpResponseMessage>> _scripted = new();

    public List<RecordedRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    public void Enqueue(HttpStatusCode status, string? json = null) => _scripted.Enqueue(() => Response(status, json));

    public static HttpResponseMessage Response(HttpStatusCode status, string? json) => new(status)
    {
        Content = json is null
            ? new StringContent(string.Empty)
            : new StringContent(json, Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Requests)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        }

        if (_scripted.Count > 0)
        {
            return _scripted.Dequeue()();
        }

        return Fallback?.Invoke(request) ?? Response(HttpStatusCode.NotFound, null);
    }
}
