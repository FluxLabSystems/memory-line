using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MemoryTimeline.SyncContracts;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// Device lifecycle flows against the real host (default refresh grace
/// window): pairing-gated registration, idempotent registration replay with
/// fresh tokens, bearer-token enforcement on every authenticated route,
/// refresh token rotation with lost-response recovery inside the grace
/// window, and immediate revocation by a peer device. Strict rotation
/// (grace 0) is covered by <see cref="DeviceRefreshStrictRotationTests"/>.
/// </summary>
public class DeviceFlowTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public DeviceFlowTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Register_ValidPairingCode_Returns201WithCredentialsAndListsDevice()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/devices/register",
            new DeviceRegisterRequest
            {
                PairingCode = SyncApiFixture.PairingCode,
                Platform = "ios",
                DisplayName = "iPhone 15",
                AppVersion = "1.0.0",
            },
            SyncApiFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>(SyncApiFixture.Json);
        registered!.DeviceId.Should().NotBeNullOrEmpty();
        Guid.TryParse(registered.DeviceId, out _).Should().BeTrue("device IDs are GUIDs");
        registered.OwnerId.Should().NotBeNullOrEmpty();
        registered.AccessToken.Should().NotBeNullOrEmpty();
        registered.RefreshToken.Should().NotBeNullOrEmpty();
        registered.AccessTokenExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);

        using var authed = _fixture.CreateClient(registered.AccessToken);
        var devices = await authed.GetFromJsonAsync<List<DeviceInfoResponse>>(
            "/api/v1/devices", SyncApiFixture.Json);
        devices!.Should().Contain(d =>
            d.DeviceId == registered.DeviceId && d.Platform == "ios" && d.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Register_WrongPairingCode_Returns401WithApiError()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/devices/register",
            new DeviceRegisterRequest
            {
                PairingCode = "wrong-pairing-code",
                Platform = "ios",
                DisplayName = "Intruder",
            },
            SyncApiFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("invalid_pairing_code");
        error.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticatedRoutes_WithoutBearerToken_Return401()
    {
        using var client = _fixture.CreateClient();

        var pull = await client.GetAsync("/api/v1/sync/pull?cursor=0");
        pull.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var captureCreate = await client.PostAsJsonAsync(
            "/api/v1/captures", SyncApiFixture.NewCaptureRequest(), SyncApiFixture.Json);
        captureCreate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var download = await client.GetAsync($"/api/v1/artifacts/{Guid.NewGuid():D}/download");
        download.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var partUpload = await SyncApiFixture.UploadPartAsync(
            client, Guid.NewGuid().ToString("D"), 1, new byte[] { 1, 2, 3 });
        partUpload.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var list = await client.GetAsync("/api/v1/devices");
        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var error = await SyncApiFixture.ReadApiErrorAsync(list);
        error.Code.Should().Be("unauthorized");
    }

    [Fact]
    public async Task AuthenticatedRoutes_WithGarbageBearerToken_Return401()
    {
        using var client = _fixture.CreateClient("not-a-valid-jwt");
        var response = await client.GetAsync("/api/v1/devices");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("unauthorized");
    }

    [Fact]
    public async Task Register_ReplayWithSameIdempotencyKey_ReturnsSameDeviceWithFreshTokens()
    {
        using var client = _fixture.CreateClient();
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var first = await client.SendAsync(NewRegisterRequest(idempotencyKey));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var original = await first.Content.ReadFromJsonAsync<DeviceRegisterResponse>(SyncApiFixture.Json);

        var replay = await client.SendAsync(NewRegisterRequest(idempotencyKey));
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        var replayed = await replay.Content.ReadFromJsonAsync<DeviceRegisterResponse>(SyncApiFixture.Json);

        // Same identity — the replay must not create a second device.
        replayed!.DeviceId.Should().Be(original!.DeviceId);
        replayed.OwnerId.Should().Be(original.OwnerId);

        // Fresh credentials: the replay mints a new token pair rather than
        // replaying a stored response, proving idempotency_records holds no
        // token material.
        replayed.RefreshToken.Should().NotBeNullOrEmpty();
        replayed.RefreshToken.Should().NotBe(original.RefreshToken, "replays mint fresh tokens");

        // The replayed pair works: the access token authenticates (and sees
        // exactly one device for this ID)...
        using var authed = _fixture.CreateClient(replayed.AccessToken);
        var devices = await authed.GetFromJsonAsync<List<DeviceInfoResponse>>(
            "/api/v1/devices", SyncApiFixture.Json);
        devices!.Should().ContainSingle(d => d.DeviceId == replayed.DeviceId);

        // ...and the replayed refresh token is the live credential.
        var refresh = await client.PostAsJsonAsync(
            $"/api/v1/devices/{replayed.DeviceId}/refresh",
            new TokenRefreshRequest { RefreshToken = replayed.RefreshToken },
            SyncApiFixture.Json);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_OldTokenWithinGraceWindow_RecoversWithWorkingFreshPair()
    {
        var device = await _fixture.RegisterDeviceAsync("windows", "Desktop");
        using var client = _fixture.CreateClient();

        // First refresh rotates; the original token enters the grace window.
        var first = await client.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/refresh",
            new TokenRefreshRequest { RefreshToken = device.RefreshToken },
            SyncApiFixture.Json);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await first.Content.ReadFromJsonAsync<TokenRefreshResponse>(SyncApiFixture.Json);
        rotated!.RefreshToken.Should().NotBe(device.RefreshToken, "refresh rotates the token");

        // Presenting the pre-rotation token within the grace window is the
        // crash/lost-response recovery path: 200 with another fresh pair.
        var recovery = await client.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/refresh",
            new TokenRefreshRequest { RefreshToken = device.RefreshToken },
            SyncApiFixture.Json);
        recovery.StatusCode.Should().Be(HttpStatusCode.OK);
        var recovered = await recovery.Content.ReadFromJsonAsync<TokenRefreshResponse>(SyncApiFixture.Json);
        recovered!.RefreshToken.Should().NotBe(device.RefreshToken);
        recovered.RefreshToken.Should().NotBe(rotated.RefreshToken);

        // The recovered pair works: the access token authenticates...
        using var authed = _fixture.CreateClient(recovered.AccessToken);
        var list = await authed.GetAsync("/api/v1/devices");
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        // ...and the recovered refresh token is the current credential.
        var next = await client.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/refresh",
            new TokenRefreshRequest { RefreshToken = recovered.RefreshToken },
            SyncApiFixture.Json);
        next.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_TokenMatchingNeitherCurrentNorPrevious_Returns401()
    {
        var device = await _fixture.RegisterDeviceAsync("windows", "Desktop");
        using var client = _fixture.CreateClient();

        // Rotate once so both a current and a previous (in-grace) token exist.
        var rotate = await client.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/refresh",
            new TokenRefreshRequest { RefreshToken = device.RefreshToken },
            SyncApiFixture.Json);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);

        // A token matching neither slot is rejected even inside the grace window.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/refresh",
            new TokenRefreshRequest { RefreshToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" },
            SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("refresh_token_invalid");
    }

    [Fact]
    public async Task Revoke_ByPeerDevice_RejectsAccessTokenAndRefreshImmediately()
    {
        var (deviceA, clientA) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (deviceB, clientB) = await _fixture.RegisterClientAsync("windows", "Desktop");
        using (clientA)
        using (clientB)
        {
            // Windows (B) revokes the iPhone (A).
            var revoke = await clientB.DeleteAsync($"/api/v1/devices/{deviceA.DeviceId}");
            revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // A's still-unexpired access token is rejected by the device filter.
            var rejected = await clientA.GetAsync("/api/v1/devices");
            rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var accessError = await SyncApiFixture.ReadApiErrorAsync(rejected);
            accessError.Code.Should().Be("device_revoked");

            // A's refresh token is rejected too.
            using var anonymous = _fixture.CreateClient();
            var refresh = await anonymous.PostAsJsonAsync(
                $"/api/v1/devices/{deviceA.DeviceId}/refresh",
                new TokenRefreshRequest { RefreshToken = deviceA.RefreshToken },
                SyncApiFixture.Json);
            refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var refreshError = await SyncApiFixture.ReadApiErrorAsync(refresh);
            refreshError.Code.Should().Be("device_revoked");

            // The revoking device still works and sees the revocation recorded.
            var devices = await clientB.GetFromJsonAsync<List<DeviceInfoResponse>>(
                "/api/v1/devices", SyncApiFixture.Json);
            devices!.Should().Contain(d => d.DeviceId == deviceA.DeviceId && d.RevokedAtUtc != null);
        }
    }

    [Fact]
    public async Task Register_IdempotencyKeyOverTheLimit_Returns400BeforeTouchingThePairingCode()
    {
        using var client = _fixture.CreateClient();

        // 129 characters: one over the OpenAPI parameter's maxLength, which the
        // authenticated routes' filter enforces and register used to skip.
        var overLong = await client.SendAsync(NewRegisterRequest(new string('k', 129)));
        overLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await SyncApiFixture.ReadApiErrorAsync(overLong);
        error.Code.Should().Be("validation_error");
        error.Message.Should().Contain("128");

        // Exactly at the limit is accepted and replays like any other key.
        var atLimit = new string('k', 128);
        var first = await client.SendAsync(NewRegisterRequest(atLimit));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await first.Content.ReadFromJsonAsync<DeviceRegisterResponse>(SyncApiFixture.Json);

        var replay = await client.SendAsync(NewRegisterRequest(atLimit));
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        var replayed = await replay.Content.ReadFromJsonAsync<DeviceRegisterResponse>(SyncApiFixture.Json);
        replayed!.DeviceId.Should().Be(registered!.DeviceId);
    }

    /// <summary>A valid registration request carrying the given Idempotency-Key header (single-use — build one per send).</summary>
    private static HttpRequestMessage NewRegisterRequest(string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/register")
        {
            Content = JsonContent.Create(
                new DeviceRegisterRequest
                {
                    PairingCode = SyncApiFixture.PairingCode,
                    Platform = "ios",
                    DisplayName = "iPhone 15",
                    AppVersion = "1.0.0",
                },
                options: SyncApiFixture.Json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }
}
