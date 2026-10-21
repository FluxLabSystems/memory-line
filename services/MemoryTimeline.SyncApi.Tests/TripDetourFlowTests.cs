using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// Trips and route context against the real host (design §6.4, §12.2): a
/// trip is created, read back and listed; status moves only along the state
/// machine and the latest valid transition wins; route context snapshots are
/// stored, validated and replayed by ID; and with no place provider
/// configured, detour generation honestly yields nothing.
/// </summary>
public class TripFlowTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public TripFlowTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Trip_Create_ReadsBackAndListsForEveryDeviceOfTheOwner()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");

        var response = await phoneClient.PostAsJsonAsync("/api/v1/trips", new TripCreateRequest
        {
            Name = "Utah loop",
            OriginLabel = "Denver",
            DestinationLabel = "Moab",
            PreferencesJson = new JsonObject { ["categories"] = new JsonArray("museum", "diner"), ["pace"] = "relaxed" },
        }, SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var trip = (await response.Content.ReadFromJsonAsync<TripResponse>(SyncApiFixture.Json))!;
        response.Headers.Location!.ToString().Should().Be($"/api/v1/trips/{trip.TripId}");
        trip.Status.Should().Be(TripStatus.Planned, "the default when none is sent");
        trip.StartedAtUtc.Should().BeNull();
        trip.PreferencesJson!["pace"]!.GetValue<string>().Should().Be("relaxed");

        // Trips are owner-scoped: the owner's other device reads the same trip.
        var fetched = await windowsClient.GetFromJsonAsync<TripResponse>($"/api/v1/trips/{trip.TripId}", SyncApiFixture.Json);
        fetched!.Name.Should().Be("Utah loop");
        fetched.PreferencesJson!["categories"]!.AsArray().Should().HaveCount(2);

        var listed = await windowsClient.GetFromJsonAsync<List<TripResponse>>("/api/v1/trips", SyncApiFixture.Json);
        listed!.Should().Contain(t => t.TripId == trip.TripId);

        (await phoneClient.GetAsync($"/api/v1/trips/{Guid.NewGuid():D}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Trip_Create_RequiresANameAndAKnownStatus()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");

        var noName = await client.PostAsJsonAsync("/api/v1/trips", new TripCreateRequest { Name = "  " }, SyncApiFixture.Json);
        noName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SyncApiFixture.ReadApiErrorAsync(noName)).Code.Should().Be("validation_error");

        var badStatus = await client.PostAsJsonAsync(
            "/api/v1/trips", new TripCreateRequest { Name = "x", Status = "driving" }, SyncApiFixture.Json);
        badStatus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SyncApiFixture.ReadApiErrorAsync(badStatus)).Message.Should().Contain("planned, active, paused, completed, archived");
    }

    [Fact]
    public async Task Trip_Status_FollowsTheStateMachineAndStampsTheTimestamps()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await CreateTripAsync(client, "Coast road");

        var active = await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "active" });
        active.StatusCode.Should().Be(HttpStatusCode.OK);
        var activeTrip = (await active.Content.ReadFromJsonAsync<TripResponse>(SyncApiFixture.Json))!;
        activeTrip.Status.Should().Be(TripStatus.Active);
        activeTrip.StartedAtUtc.Should().NotBeNull("activating stamps the start when the client sent none");

        // Same status again is a no-op, not a conflict.
        (await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "active" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var paused = await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "paused", ["name"] = "Coast road, day 2" });
        var pausedTrip = (await paused.Content.ReadFromJsonAsync<TripResponse>(SyncApiFixture.Json))!;
        pausedTrip.Status.Should().Be(TripStatus.Paused);
        pausedTrip.Name.Should().Be("Coast road, day 2");

        var completed = await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "completed" });
        var completedTrip = (await completed.Content.ReadFromJsonAsync<TripResponse>(SyncApiFixture.Json))!;
        completedTrip.EndedAtUtc.Should().NotBeNull("completing stamps the end");

        // Backwards is not a transition the machine has.
        var reopened = await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "active" });
        reopened.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SyncApiFixture.ReadApiErrorAsync(reopened)).Code.Should().Be("trip_invalid_transition");

        (await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "archived" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var fromArchived = await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "planned" });
        fromArchived.StatusCode.Should().Be(HttpStatusCode.Conflict, "archived is terminal");

        // Descriptive fields: absent means unchanged, explicit null clears.
        var cleared = await PatchAsync(client, trip.TripId, new JsonObject { ["originLabel"] = null });
        (await cleared.Content.ReadFromJsonAsync<TripResponse>(SyncApiFixture.Json))!.OriginLabel.Should().BeNull();

        var unknownStatus = await PatchAsync(client, trip.TripId, new JsonObject { ["status"] = "lost" });
        unknownStatus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RouteContext_IsStoredValidatedAndReplayedById()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await CreateTripAsync(client, "Desert run");

        var minted = await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context", new TripRouteContext
        {
            CapturedAtUtc = DateTime.UtcNow,
            Latitude = 38.5733,
            Longitude = -109.5498,
            SpeedMps = 27.3,
            RouteProgress = 0.4,
            CurrentRoadName = "US-191",
            Provider = "apple_maps",
        }, SyncApiFixture.Json);
        minted.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = (await minted.Content.ReadFromJsonAsync<TripRouteContext>(SyncApiFixture.Json))!;
        Guid.TryParse(stored.RouteContextId, out _).Should().BeTrue("the server mints an ID when none was sent");
        stored.TripId.Should().Be(trip.TripId);
        stored.CurrentRoadName.Should().Be("US-191");

        var snapshotId = Guid.NewGuid().ToString("D");
        var request = new TripRouteContext { RouteContextId = snapshotId, CapturedAtUtc = DateTime.UtcNow, Latitude = 38.6, Longitude = -109.6 };
        (await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context", request, SyncApiFixture.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var replay = await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context", request, SyncApiFixture.Json);
        replay.StatusCode.Should().Be(HttpStatusCode.OK, "replaying a snapshot ID returns the stored snapshot");
        (await replay.Content.ReadFromJsonAsync<TripRouteContext>(SyncApiFixture.Json))!.RouteContextId.Should().Be(snapshotId);

        var badLatitude = await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
            new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 91, Longitude = 0 }, SyncApiFixture.Json);
        badLatitude.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noTimestamp = await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
            new TripRouteContext { Latitude = 1, Longitude = 1 }, SyncApiFixture.Json);
        noTimestamp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknownTrip = await client.PostAsJsonAsync($"/api/v1/trips/{Guid.NewGuid():D}/route-context",
            new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 1, Longitude = 1 }, SyncApiFixture.Json);
        unknownTrip.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SyncApiFixture.ReadApiErrorAsync(unknownTrip)).Code.Should().Be("trip_not_found");
    }

    [Fact]
    public async Task Detours_WithoutAConfiguredProvider_GenerateNothingAndSaySo()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await CreateTripAsync(client, "No provider");
        (await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
                new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 40, Longitude = -105 }, SyncApiFixture.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var generate = await client.PostAsync($"/api/v1/trips/{trip.TripId}/detours/generate", content: null);
        generate.StatusCode.Should().Be(HttpStatusCode.OK);
        (await generate.Content.ReadFromJsonAsync<List<DetourSuggestion>>(SyncApiFixture.Json))!.Should().BeEmpty(
            "the service ships with no place provider, so it invents nothing");

        var list = await client.GetFromJsonAsync<List<DetourSuggestion>>($"/api/v1/trips/{trip.TripId}/detours", SyncApiFixture.Json);
        list!.Should().BeEmpty();

        (await client.PostAsync($"/api/v1/trips/{Guid.NewGuid():D}/detours/generate", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/v1/detours/{Guid.NewGuid():D}/accept", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TripRoutes_RequireAuthentication()
    {
        using var client = _fixture.CreateClient();
        (await client.GetAsync("/api/v1/trips")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync($"/api/v1/detours/{Guid.NewGuid():D}/save", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    internal static async Task<TripResponse> CreateTripAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/trips", new TripCreateRequest { Name = name }, SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TripResponse>(SyncApiFixture.Json))!;
    }

    internal static Task<HttpResponseMessage> PatchAsync(HttpClient client, string tripId, JsonObject body)
        => client.PatchAsync($"/api/v1/trips/{tripId}", JsonContent.Create(body, options: SyncApiFixture.Json));
}

/// <summary>
/// Detour suggestions against a host whose provider is a test double, so the
/// lifecycle the service owns can be exercised: candidates become proposed
/// suggestions, places already on the table are not proposed twice, listing
/// is newest first, disposition is monotonic (accepted and dismissed are
/// terminal, saving is a step before either), and a suggestion past its
/// expiry is expired lazily and can no longer be acted on.
/// </summary>
public class DetourFlowTests : IClassFixture<DetourProviderSyncApiFixture>
{
    private readonly DetourProviderSyncApiFixture _fixture;

    public DetourFlowTests(DetourProviderSyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Detours_GenerateListAndDispose_FollowTheMonotonicStateMachine()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await TripFlowTests.CreateTripAsync(client, "Museum hunt");

        // No route context yet: nothing to generate from, and the provider is not even asked.
        _fixture.Provider.Calls = 0;
        var premature = await Generate(client, trip.TripId);
        premature.Should().BeEmpty();
        _fixture.Provider.Calls.Should().Be(0);

        (await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
                new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 39.7, Longitude = -104.9 }, SyncApiFixture.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        _fixture.Provider.Candidates = () =>
        [
            Candidate("place-aviation", "Aviation museum", "museum"),
            Candidate("place-diner", "Roadside diner", "food"),
        ];
        var generated = await Generate(client, trip.TripId);
        generated.Should().HaveCount(2);
        generated.Should().OnlyContain(s => s.State == DetourSuggestionState.Proposed);
        generated.Should().OnlyContain(s => s.TripId == trip.TripId);
        generated.Should().OnlyContain(s => s.ExpiresAtUtc != null, "the service applies a default lifetime");
        generated.Should().OnlyContain(s => s.PlaceProvider == "fake");
        var aviation = generated.Single(s => s.ProviderPlaceId == "place-aviation");
        var diner = generated.Single(s => s.ProviderPlaceId == "place-diner");
        aviation.VerificationSourcesJson!["hours"]!.GetValue<string>().Should().Be("provider");

        // The same places are not proposed twice: the provider is told what is excluded.
        var again = await Generate(client, trip.TripId);
        again.Should().BeEmpty();
        _fixture.Provider.LastExcluded.Should().BeEquivalentTo(["place-aviation", "place-diner"]);

        var listed = await client.GetFromJsonAsync<List<DetourSuggestion>>($"/api/v1/trips/{trip.TripId}/detours", SyncApiFixture.Json);
        listed!.Select(s => s.SuggestionId).Should().BeEquivalentTo([aviation.SuggestionId, diner.SuggestionId]);

        // Save is a step before accept or dismiss; accept and dismiss are terminal.
        var saved = await Transition(client, aviation.SuggestionId, "save");
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(saved)).State.Should().Be(DetourSuggestionState.Saved);

        var accepted = await Transition(client, aviation.SuggestionId, "accept");
        (await Body(accepted)).State.Should().Be(DetourSuggestionState.Accepted);

        var acceptedAgain = await Transition(client, aviation.SuggestionId, "accept");
        acceptedAgain.StatusCode.Should().Be(HttpStatusCode.OK, "re-accepting is idempotent");
        (await Body(acceptedAgain)).State.Should().Be(DetourSuggestionState.Accepted);

        var dismissAccepted = await Transition(client, aviation.SuggestionId, "dismiss");
        dismissAccepted.StatusCode.Should().Be(HttpStatusCode.Conflict, "an accepted suggestion is never overwritten");
        (await SyncApiFixture.ReadApiErrorAsync(dismissAccepted)).Code.Should().Be("detour_invalid_transition");
        (await Transition(client, aviation.SuggestionId, "save")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var dismissed = await Transition(client, diner.SuggestionId, "dismiss");
        (await Body(dismissed)).State.Should().Be(DetourSuggestionState.Dismissed);
        (await Transition(client, diner.SuggestionId, "accept")).StatusCode.Should().Be(HttpStatusCode.Conflict, "dismissed is terminal");
        (await Transition(client, diner.SuggestionId, "dismiss")).StatusCode.Should().Be(HttpStatusCode.OK, "re-dismissing is idempotent");
    }

    [Fact]
    public async Task Detours_PastTheirExpiry_AreExpiredLazilyAndCannotBeActedOn()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await TripFlowTests.CreateTripAsync(client, "Late night");
        (await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
                new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 39.7, Longitude = -104.9 }, SyncApiFixture.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        _fixture.Provider.Candidates = () =>
        [
            Candidate("place-closed", "Closed overlook", "viewpoint", expiresAtUtc: DateTime.UtcNow.AddMinutes(-1)),
        ];
        var generated = await Generate(client, trip.TripId);
        var suggestion = generated.Should().ContainSingle().Which;

        // Listing observes the expiry; acting on it is refused.
        var listed = await client.GetFromJsonAsync<List<DetourSuggestion>>($"/api/v1/trips/{trip.TripId}/detours", SyncApiFixture.Json);
        listed!.Single(s => s.SuggestionId == suggestion.SuggestionId).State.Should().Be(DetourSuggestionState.Expired);

        var accept = await Transition(client, suggestion.SuggestionId, "accept");
        accept.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SyncApiFixture.ReadApiErrorAsync(accept)).Code.Should().Be("detour_invalid_transition");
    }

    [Fact]
    public async Task Detours_AcceptHonoursTheIdempotencyKey()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await TripFlowTests.CreateTripAsync(client, "Replay");
        (await client.PostAsJsonAsync($"/api/v1/trips/{trip.TripId}/route-context",
                new TripRouteContext { CapturedAtUtc = DateTime.UtcNow, Latitude = 39.7, Longitude = -104.9 }, SyncApiFixture.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        _fixture.Provider.Candidates = () => [Candidate("place-replay", "Replayed stop", "food")];
        var suggestion = (await Generate(client, trip.TripId)).Should().ContainSingle().Which;

        var key = Guid.NewGuid().ToString("N");
        var first = await Transition(client, suggestion.SuggestionId, "accept", key);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await Transition(client, suggestion.SuggestionId, "accept", key);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(replay)).State.Should().Be(DetourSuggestionState.Accepted);
    }

    private static DetourCandidate Candidate(string placeId, string name, string category, DateTime? expiresAtUtc = null) => new()
    {
        ProviderPlaceId = placeId,
        Name = name,
        Category = category,
        Reason = $"{name} is a short detour from the current route.",
        Latitude = 39.75,
        Longitude = -104.99,
        RouteAddedDistanceM = 4200,
        RouteAddedDurationS = 660,
        OpenStatus = DetourOpenStatus.Open,
        PriceSummary = "$14",
        AdmissionCurrency = "USD",
        Confidence = 0.8,
        VerificationSourcesJson = new JsonObject { ["hours"] = "provider", ["price"] = "provider" },
        ExpiresAtUtc = expiresAtUtc,
    };

    private static async Task<List<DetourSuggestion>> Generate(HttpClient client, string tripId)
    {
        var response = await client.PostAsync($"/api/v1/trips/{tripId}/detours/generate", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<DetourSuggestion>>(SyncApiFixture.Json))!;
    }

    private static Task<HttpResponseMessage> Transition(HttpClient client, string suggestionId, string action, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/detours/{suggestionId}/{action}");
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    private static async Task<DetourSuggestion> Body(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<DetourSuggestion>(SyncApiFixture.Json))!;
}

/// <summary>
/// Host whose <see cref="IDetourSuggestionProvider"/> is a configurable test
/// double, standing in for the Google/Apple place adapters the service does
/// not ship with.
/// </summary>
public sealed class DetourProviderSyncApiFixture : SyncApiFixture
{
    public FakeDetourProvider Provider { get; }

    public DetourProviderSyncApiFixture()
        : this(new FakeDetourProvider())
    {
    }

    private DetourProviderSyncApiFixture(FakeDetourProvider provider)
        : base(extraSettings: null, configureServices: services => services.AddSingleton<IDetourSuggestionProvider>(provider))
    {
        Provider = provider;
    }
}

/// <summary>Returns whatever <see cref="Candidates"/> yields, minus excluded places, and records what it was asked.</summary>
public sealed class FakeDetourProvider : IDetourSuggestionProvider
{
    public string Name => "fake";

    public Func<List<DetourCandidate>> Candidates { get; set; } = () => [];

    public int Calls { get; set; }

    public IReadOnlyCollection<string> LastExcluded { get; private set; } = [];

    public Task<IReadOnlyList<DetourCandidate>> GenerateAsync(DetourGenerationContext context, CancellationToken cancellationToken)
    {
        Calls++;
        LastExcluded = context.ExcludedProviderPlaceIds.ToList();
        var fresh = Candidates()
            .Where(c => c.ProviderPlaceId is null || !context.ExcludedProviderPlaceIds.Contains(c.ProviderPlaceId))
            .ToList();
        return Task.FromResult<IReadOnlyList<DetourCandidate>>(fresh);
    }
}
