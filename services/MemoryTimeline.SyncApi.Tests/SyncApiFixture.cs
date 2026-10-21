using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryTimeline.SyncContracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// Per-test-class host for the sync service: boots the real application via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with an isolated temporary
/// data directory, a fixed pairing code, and a deliberately tiny
/// SyncApi:MaxPartSizeBytes (<see cref="PartSizeBytes"/>) so multi-part
/// artifact uploads are exercised with small payloads. The temporary directory
/// (SQLite database, signing key, pairing code file, artifact blobs) is
/// deleted on dispose. Derived fixtures can layer extra host settings on top
/// (see <see cref="StrictRotationSyncApiFixture"/>).
/// </summary>
public class SyncApiFixture : IDisposable
{
    /// <summary>Pairing code the test host is configured with (SyncApi:PairingCode).</summary>
    public const string PairingCode = "test-pairing";

    /// <summary>Configured SyncApi:MaxPartSizeBytes — tiny so a ~100-byte payload spans two parts.</summary>
    public const int PartSizeBytes = 64;

    /// <summary>
    /// camelCase wire serializer options — the service relies on ASP.NET Core
    /// JSON defaults, so clients must use <see cref="JsonSerializerDefaults.Web"/>.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;

    public SyncApiFixture()
        : this(extraSettings: null)
    {
    }

    /// <summary>
    /// Boots the host with the standard settings plus <paramref name="extraSettings"/>
    /// (which win on key collisions), and lets a derived fixture swap services
    /// through <paramref name="configureServices"/>, which runs after the
    /// application's own registrations so a later singleton wins.
    /// </summary>
    protected SyncApiFixture(
        IReadOnlyDictionary<string, string>? extraSettings,
        Action<IServiceCollection>? configureServices = null)
    {
        _dataDir = Path.Combine(
            Path.GetTempPath(), "memory-line-syncapi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);

        _baseFactory = new WebApplicationFactory<Program>();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SyncApi:DataDir", _dataDir);
            builder.UseSetting("SyncApi:PairingCode", PairingCode);
            builder.UseSetting("SyncApi:MaxPartSizeBytes", PartSizeBytes.ToString());
            foreach (var (key, value) in extraSettings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });
    }

    /// <summary>The host's service provider, for tests that drive a service directly (sweeps, reapers).</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>The temporary data directory the host runs against (SQLite file, artifact blobs, key, code).</summary>
    public string DataDirectory => _dataDir;

    /// <summary>Creates an unauthenticated client against the test host.</summary>
    public HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>Creates a client sending the given bearer access token on every request.</summary>
    public HttpClient CreateClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Registers a device with the fixture's pairing code and returns its credentials.</summary>
    public async Task<DeviceRegisterResponse> RegisterDeviceAsync(string platform, string displayName)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/devices/register",
            new DeviceRegisterRequest
            {
                PairingCode = PairingCode,
                Platform = platform,
                DisplayName = displayName,
                AppVersion = "1.0.0-test",
            },
            Json);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Device registration failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>(Json)
            ?? throw new InvalidOperationException("Device registration returned no body.");
    }

    /// <summary>Registers a device and returns it together with an authenticated client.</summary>
    public async Task<(DeviceRegisterResponse Device, HttpClient Client)> RegisterClientAsync(
        string platform, string displayName)
    {
        var device = await RegisterDeviceAsync(platform, displayName);
        return (device, CreateClient(device.AccessToken));
    }

    /// <summary>Builds a valid capture create request (random capture ID unless supplied).</summary>
    public static CaptureCreateRequest NewCaptureRequest(string? captureId = null) => new()
    {
        CaptureId = captureId ?? Guid.NewGuid().ToString("D"),
        CaptureType = "memory",
        CapturedAtUtc = DateTime.UtcNow,
        TimezoneId = "America/Denver",
        LocalOffsetMinutes = -360,
        ClientSchemaVersion = 1,
    };

    /// <summary>Creates a capture and returns the server response; throws unless the server replies 201.</summary>
    public static async Task<CaptureResponse> CreateCaptureAsync(
        HttpClient client, CaptureCreateRequest? request = null)
    {
        request ??= NewCaptureRequest();
        var response = await client.PostAsJsonAsync("/api/v1/captures", request, Json);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Capture create failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<CaptureResponse>(Json)
            ?? throw new InvalidOperationException("Capture create returned no body.");
    }

    /// <summary>Pulls one page of changes for the calling device.</summary>
    public static async Task<SyncPullResponse> PullAsync(HttpClient client, long cursor, int? limit = null)
    {
        var url = limit is null
            ? $"/api/v1/sync/pull?cursor={cursor}"
            : $"/api/v1/sync/pull?cursor={cursor}&limit={limit}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncPullResponse>(Json)
            ?? throw new InvalidOperationException("Sync pull returned no body.");
    }

    /// <summary>
    /// Pulls to exhaustion and returns the device's current cursor. Used as a
    /// per-test baseline so assertions on pulled changes are unaffected by
    /// changes earlier tests in the same class appended to the shared log.
    /// </summary>
    public static async Task<long> GetCursorAsync(HttpClient client)
    {
        long cursor = 0;
        while (true)
        {
            var page = await PullAsync(client, cursor, limit: 500);
            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                return cursor;
            }
        }
    }

    /// <summary>Pushes outbox entries and returns the per-entry results.</summary>
    public static async Task<SyncPushResponse> PushAsync(HttpClient client, params SyncPushEntry[] entries)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push", new SyncPushRequest { Entries = entries.ToList() }, Json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncPushResponse>(Json)
            ?? throw new InvalidOperationException("Sync push returned no body.");
    }

    /// <summary>
    /// Initiates an audio artifact on a capture. Declared length and SHA-256
    /// default to the actual content values; pass overrides to test mismatches.
    /// </summary>
    public static async Task<ArtifactInitiateResponse> InitiateArtifactAsync(
        HttpClient client,
        string captureId,
        byte[] content,
        string? expectedSha256 = null,
        long? expectedByteLength = null,
        string? artifactId = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/captures/{captureId}/artifacts/initiate",
            new ArtifactInitiateRequest
            {
                ArtifactId = artifactId,
                ArtifactType = "audio_original",
                MediaType = "audio/mp4",
                OriginalFileName = "capture.m4a",
                ExpectedByteLength = expectedByteLength ?? content.Length,
                ExpectedSha256 = expectedSha256 ?? Sha256Hex(content),
            },
            Json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ArtifactInitiateResponse>(Json)
            ?? throw new InvalidOperationException("Artifact initiate returned no body.");
    }

    /// <summary>PUTs one raw part; returns the response for status assertions.</summary>
    public static Task<HttpResponseMessage> UploadPartAsync(
        HttpClient client, string artifactId, int partNumber, byte[] bytes)
        => client.PutAsync($"/api/v1/artifacts/{artifactId}/parts/{partNumber}", new ByteArrayContent(bytes));

    /// <summary>Uploads the full content split into <see cref="PartSizeBytes"/>-sized parts.</summary>
    public static async Task UploadPartsAsync(HttpClient client, string artifactId, byte[] content)
    {
        var partNumber = 1;
        for (var offset = 0; offset < content.Length; offset += PartSizeBytes, partNumber++)
        {
            var part = content[offset..Math.Min(offset + PartSizeBytes, content.Length)];
            var response = await UploadPartAsync(client, artifactId, partNumber, part);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    $"Part {partNumber} upload failed with status {(int)response.StatusCode}.");
            }
        }
    }

    /// <summary>POSTs the artifact completion claim; returns the response for status assertions.</summary>
    public static Task<HttpResponseMessage> CompleteArtifactAsync(
        HttpClient client, string artifactId, int partCount, long totalByteLength, string sha256)
        => client.PostAsJsonAsync(
            $"/api/v1/artifacts/{artifactId}/complete",
            new ArtifactCompleteRequest
            {
                PartCount = partCount,
                TotalByteLength = totalByteLength,
                Sha256 = sha256,
            },
            Json);

    /// <summary>Lowercase hex SHA-256 of the given bytes (the wire hash format).</summary>
    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Random content for artifact payloads.</summary>
    public static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    /// <summary>Reads the ApiError envelope every 4xx/5xx response must carry.</summary>
    public static async Task<ApiError> ReadApiErrorAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<ApiError>(Json)
            ?? throw new InvalidOperationException("The response did not carry an ApiError body.");

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();

        // Release pooled SQLite handles so the database file can be deleted.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory the OS reclaims anyway;
            // a straggling handle must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above — cleanup only.
        }
    }
}

/// <summary>
/// Host configured with SyncApi:RefreshTokenGraceSeconds=0 — strict refresh
/// rotation, where the pre-rotation token dies immediately with no recovery
/// window.
/// </summary>
public sealed class StrictRotationSyncApiFixture : SyncApiFixture
{
    public StrictRotationSyncApiFixture()
        : base(new Dictionary<string, string> { ["SyncApi:RefreshTokenGraceSeconds"] = "0" })
    {
    }
}

/// <summary>
/// Host configured with a tiny SyncApi:ArtifactStorageBudgetBytes
/// (<see cref="BudgetBytes"/>) so the retention sweep's storage-pressure path
/// can be exercised with 100-byte artifacts.
/// </summary>
public sealed class StoragePressureSyncApiFixture : SyncApiFixture
{
    /// <summary>Configured budget: room for one 100-byte artifact, not two.</summary>
    public const long BudgetBytes = 150;

    public StoragePressureSyncApiFixture()
        : base(new Dictionary<string, string> { ["SyncApi:ArtifactStorageBudgetBytes"] = BudgetBytes.ToString() })
    {
    }
}
