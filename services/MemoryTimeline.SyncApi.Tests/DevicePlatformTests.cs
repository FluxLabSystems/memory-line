using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.SyncContracts;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The platform vocabulary the service accepts (contract v1.5): the Mac app
/// registers as `macos` and its captures are attributed to that platform
/// everywhere the platform is reported, while an unknown platform is still
/// refused with the full list in the message.
/// </summary>
public class DevicePlatformTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public DevicePlatformTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Register_AsMacos_IsAcceptedAndListedAsMacos()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/devices/register",
            new DeviceRegisterRequest
            {
                PairingCode = SyncApiFixture.PairingCode,
                Platform = "macos",
                DisplayName = "Studio",
                AppVersion = "1.0.0",
            },
            SyncApiFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>(SyncApiFixture.Json);

        using var authed = _fixture.CreateClient(registered!.AccessToken);
        var devices = await authed.GetFromJsonAsync<List<DeviceInfoResponse>>("/api/v1/devices", SyncApiFixture.Json);
        devices!.Should().Contain(d => d.DeviceId == registered.DeviceId && d.Platform == "macos");
    }

    [Fact]
    public async Task Capture_FromAMacosDevice_ReportsMacosAsItsSourcePlatformToEveryReader()
    {
        var (_, mac) = await _fixture.RegisterClientAsync("macos", "Studio");
        var (_, windows) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windows);

        var created = await SyncApiFixture.CreateCaptureAsync(mac);
        created.SourcePlatform.Should().Be("macos");

        var pull = await SyncApiFixture.PullAsync(windows, windowsBaseline);
        var change = pull.Changes.Where(c => c.EntityId == created.CaptureId).Should().ContainSingle().Which;
        JsonSerializer.Deserialize<CaptureChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!
            .SourcePlatform.Should().Be("macos", "Windows tells a Mac capture from its own by this value");
    }

    [Fact]
    public async Task Register_WithAnUnknownPlatform_Returns400NamingEveryAcceptedValue()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/devices/register",
            new DeviceRegisterRequest
            {
                PairingCode = SyncApiFixture.PairingCode,
                Platform = "toaster",
                DisplayName = "Toaster",
            },
            SyncApiFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("validation_error");
        error.Message.Should().Contain("macos").And.Contain("ios").And.Contain("windows").And.Contain("other");
    }
}
