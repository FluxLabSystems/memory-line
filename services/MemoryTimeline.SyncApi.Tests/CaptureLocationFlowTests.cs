using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.SyncContracts;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The capture location snapshot leaves the service (contract v1.5): a
/// LocationContext sent on create comes back on the create response, on a
/// read, and on the capture change Windows pulls; coordinates outside the
/// globe are refused at create with the same rule the trip route-context
/// endpoint applies.
/// </summary>
public class CaptureLocationFlowTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public CaptureLocationFlowTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_WithLocation_EchoesItOnTheResponseTheReadAndTheChangeWindowsPulls()
    {
        var (_, phone) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windows) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windows);

        var request = SyncApiFixture.NewCaptureRequest();
        request.LocationContext = new LocationContext
        {
            Latitude = 43.6532,
            Longitude = -79.3832,
            HorizontalAccuracyM = 18.0,
            HeadingDegrees = 255.0,
            SpeedMps = 21.4,
        };

        var created = await SyncApiFixture.CreateCaptureAsync(phone, request);
        AssertSnapshot(created.Location, "the create response echoes the snapshot");

        var read = await phone.GetFromJsonAsync<CaptureResponse>(
            $"/api/v1/captures/{created.CaptureId}", SyncApiFixture.Json);
        AssertSnapshot(read!.Location, "a read returns the stored snapshot");

        var pull = await SyncApiFixture.PullAsync(windows, windowsBaseline);
        var change = pull.Changes.Where(c => c.EntityId == created.CaptureId).Should().ContainSingle().Which;
        var payload = JsonSerializer.Deserialize<CaptureChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!;
        AssertSnapshot(payload.Location, "the capture change carries the snapshot to Windows");
    }

    [Fact]
    public async Task Create_WithoutLocation_LeavesTheSnapshotNull()
    {
        var (_, phone) = await _fixture.RegisterClientAsync("ios", "iPhone");

        var created = await SyncApiFixture.CreateCaptureAsync(phone);
        created.Location.Should().BeNull();

        var read = await phone.GetFromJsonAsync<CaptureResponse>(
            $"/api/v1/captures/{created.CaptureId}", SyncApiFixture.Json);
        read!.Location.Should().BeNull();
    }

    [Theory]
    [InlineData(90.5, 0.0, "latitude")]
    [InlineData(-90.5, 0.0, "latitude")]
    [InlineData(0.0, 180.5, "longitude")]
    [InlineData(0.0, -180.5, "longitude")]
    public async Task Create_WithCoordinatesOffTheGlobe_Returns400AndCreatesNothing(
        double latitude, double longitude, string offendingField)
    {
        var (_, phone) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var request = SyncApiFixture.NewCaptureRequest();
        request.LocationContext = new LocationContext { Latitude = latitude, Longitude = longitude };

        var response = await phone.PostAsJsonAsync("/api/v1/captures", request, SyncApiFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("validation_error");
        error.Message.Should().Contain(offendingField);

        // Nothing was created: the same id, with a valid snapshot, is a fresh create.
        var valid = SyncApiFixture.NewCaptureRequest(request.CaptureId);
        valid.LocationContext = new LocationContext { Latitude = 0.0, Longitude = 0.0 };
        var retry = await phone.PostAsJsonAsync("/api/v1/captures", valid, SyncApiFixture.Json);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static void AssertSnapshot(LocationContext? location, string because)
    {
        location.Should().NotBeNull(because);
        location!.Latitude.Should().BeApproximately(43.6532, 1e-9);
        location.Longitude.Should().BeApproximately(-79.3832, 1e-9);
        location.HorizontalAccuracyM.Should().Be(18.0);
        location.HeadingDegrees.Should().Be(255.0);
        location.SpeedMps.Should().Be(21.4);
    }
}
