using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The artifact retention sweep (design §22 decision 7) against the real host:
/// bytes are purged only once Windows has acknowledged archival and the
/// retention window has passed, a purged artifact answers 410 rather than a
/// retryable error, unacknowledged artifacts are never touched, and
/// crash-orphaned temporary files are removed while in-flight ones are kept.
/// The sweep takes its clock as a parameter, so "fourteen days later" is a
/// value rather than a wait.
/// </summary>
public class RetentionSweepTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public RetentionSweepTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Sweep_PurgesOnlyArtifactsAcknowledgedCompleteBeyondTheRetentionWindow()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");

        var archived = await UploadCaptureAsync(phoneClient);
        var stillProcessing = await UploadCaptureAsync(phoneClient);
        await PushStatusAsync(windowsClient, 1, archived.CaptureId, SyncCaptureStatus.Completed);
        await PushStatusAsync(windowsClient, 2, stillProcessing.CaptureId, SyncCaptureStatus.Processing);

        var sweep = _fixture.Services.GetRequiredService<IArtifactRetentionService>();

        // Inside the window: acknowledged, but not yet old enough.
        var early = await sweep.SweepAsync(DateTime.UtcNow.AddDays(13), CancellationToken.None);
        early.ArtifactsPurgedByAge.Should().Be(0);
        (await DownloadAsync(phoneClient, archived.ArtifactId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Past the window: the archived capture's audio goes, the other stays.
        var late = await sweep.SweepAsync(DateTime.UtcNow.AddDays(15), CancellationToken.None);
        late.ArtifactsPurgedByAge.Should().Be(1);
        late.ArtifactsPurgedByPressure.Should().Be(0, "no storage budget is configured on this host");

        var purgedDownload = await DownloadAsync(phoneClient, archived.ArtifactId);
        purgedDownload.StatusCode.Should().Be(HttpStatusCode.Gone);
        var error = await SyncApiFixture.ReadApiErrorAsync(purgedDownload);
        error.Code.Should().Be("artifact_purged");
        error.Retryable.Should().BeFalse("the bytes are not coming back, so a client must not retry");

        // The metadata survives with the state saying what happened.
        var capture = await phoneClient.GetFromJsonAsync<CaptureResponse>(
            $"/api/v1/captures/{archived.CaptureId}", SyncApiFixture.Json);
        var summary = capture!.Artifacts.Should().ContainSingle().Which;
        summary.State.Should().Be(ArtifactState.Purged);
        summary.ByteLength.Should().Be(100, "the measured length is kept for the record");
        File.Exists(Path.Combine(_fixture.DataDirectory, "artifacts", $"{archived.ArtifactId}.bin"))
            .Should().BeFalse("the blob itself is deleted");

        (await DownloadAsync(phoneClient, stillProcessing.ArtifactId)).StatusCode.Should().Be(
            HttpStatusCode.OK, "Windows has not archived this capture, so its audio is kept indefinitely");

        // Sweeping again finds nothing new to do.
        var again = await sweep.SweepAsync(DateTime.UtcNow.AddDays(15), CancellationToken.None);
        again.ArtifactsPurgedByAge.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_RemovesOrphanedTemporaryFilesButKeepsOnesStillBeingWritten()
    {
        var artifactsDir = Path.Combine(_fixture.DataDirectory, "artifacts");
        var orphanId = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.Combine(artifactsDir, orphanId));

        // The two layouts a crashed request leaves behind.
        var staleAssembly = Path.Combine(artifactsDir, $"{orphanId}.bin.{Guid.NewGuid():N}.tmp");
        var stalePart = Path.Combine(artifactsDir, orphanId, $"part_00001.{Guid.NewGuid():N}.tmp");
        var freshPart = Path.Combine(artifactsDir, orphanId, $"part_00002.{Guid.NewGuid():N}.tmp");
        foreach (var path in new[] { staleAssembly, stalePart, freshPart })
        {
            await File.WriteAllBytesAsync(path, SyncApiFixture.RandomBytes(16));
        }

        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(staleAssembly, twoHoursAgo);
        File.SetLastWriteTimeUtc(stalePart, twoHoursAgo);

        var sweep = _fixture.Services.GetRequiredService<IArtifactRetentionService>();
        var report = await sweep.SweepAsync(DateTime.UtcNow, CancellationToken.None);

        report.TemporaryFilesRemoved.Should().BeGreaterThanOrEqualTo(2);
        File.Exists(staleAssembly).Should().BeFalse();
        File.Exists(stalePart).Should().BeFalse();
        File.Exists(freshPart).Should().BeTrue("a file written within the grace window may still be in flight");
    }

    [Fact]
    public async Task Sweep_DeletesOldRouteContextsButKeepsEachTripsNewestSnapshot()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var trip = await TripFlowTests.CreateTripAsync(client, "Long haul");
        var captured = DateTime.UtcNow;
        var first = await AppendRouteContextAsync(client, trip.TripId, captured.AddMinutes(-10));
        var second = await AppendRouteContextAsync(client, trip.TripId, captured.AddMinutes(-5));
        var newest = await AppendRouteContextAsync(client, trip.TripId, captured);

        var sweep = _fixture.Services.GetRequiredService<IArtifactRetentionService>();

        var early = await sweep.SweepAsync(DateTime.UtcNow.AddDays(13), CancellationToken.None);
        early.RouteContextsRemoved.Should().Be(0, "all three are inside the 14-day window");

        var late = await sweep.SweepAsync(DateTime.UtcNow.AddDays(15), CancellationToken.None);
        late.RouteContextsRemoved.Should().Be(2, "the trail goes, the trip's current position stays");

        // A replay tells us what survived: a stored snapshot replays with 200,
        // a deleted one is re-created with 201.
        (await ReplayAsync(client, trip.TripId, newest)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReplayAsync(client, trip.TripId, first)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReplayAsync(client, trip.TripId, second)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<TripRouteContext> AppendRouteContextAsync(HttpClient client, string tripId, DateTime capturedAtUtc)
    {
        var request = new TripRouteContext
        {
            RouteContextId = Guid.NewGuid().ToString("D"),
            CapturedAtUtc = capturedAtUtc,
            Latitude = 44.0,
            Longitude = -110.0,
        };
        var response = await client.PostAsJsonAsync($"/api/v1/trips/{tripId}/route-context", request, SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return request;
    }

    private static Task<HttpResponseMessage> ReplayAsync(HttpClient client, string tripId, TripRouteContext snapshot)
        => client.PostAsJsonAsync($"/api/v1/trips/{tripId}/route-context", snapshot, SyncApiFixture.Json);

    /// <summary>Creates a capture and uploads a 100-byte audio artifact; returns the IDs.</summary>
    internal static async Task<(string CaptureId, string ArtifactId)> UploadCaptureAsync(HttpClient client)
    {
        var capture = await SyncApiFixture.CreateCaptureAsync(client);
        var content = SyncApiFixture.RandomBytes(100);
        var initiate = await SyncApiFixture.InitiateArtifactAsync(client, capture.CaptureId, content);
        await SyncApiFixture.UploadPartsAsync(client, initiate.ArtifactId, content);
        var complete = await SyncApiFixture.CompleteArtifactAsync(
            client, initiate.ArtifactId, partCount: 2, totalByteLength: 100, sha256: SyncApiFixture.Sha256Hex(content));
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        return (capture.CaptureId, initiate.ArtifactId);
    }

    /// <summary>Pushes a Windows-authored capture_status for the capture, the way the outbox publisher does.</summary>
    internal static async Task PushStatusAsync(HttpClient windowsClient, long clientSequence, string captureId, string status)
    {
        var payload = new CaptureStatusChangePayload
        {
            CaptureId = captureId,
            Status = status,
            UpdatedAtUtc = DateTime.UtcNow,
            TranscriptAvailable = status == SyncCaptureStatus.Completed,
        };
        var push = await SyncApiFixture.PushAsync(windowsClient, new SyncPushEntry
        {
            ClientSequence = clientSequence,
            EntityType = SyncChangeEntityType.CaptureStatus,
            EntityId = captureId,
            Operation = SyncOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(payload, SyncApiFixture.Json),
            ChangedAtUtc = DateTime.UtcNow,
        });
        push.Results.Should().ContainSingle().Which.Accepted.Should().BeTrue();
    }

    internal static Task<HttpResponseMessage> DownloadAsync(HttpClient client, string artifactId)
        => client.GetAsync($"/api/v1/artifacts/{artifactId}/download");
}

/// <summary>
/// The storage-pressure half of the sweep, on a host whose budget holds one
/// 100-byte artifact but not two: acknowledged artifacts are purged
/// oldest-acknowledgement-first until the store is back under budget, and an
/// unacknowledged artifact is kept even when it is what keeps the store over.
/// </summary>
public class RetentionStoragePressureTests : IClassFixture<StoragePressureSyncApiFixture>
{
    private readonly StoragePressureSyncApiFixture _fixture;

    public RetentionStoragePressureTests(StoragePressureSyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Sweep_OverBudget_PurgesOldestAcknowledgedArtifactsFirstAndNeverUnacknowledgedOnes()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");

        var first = await RetentionSweepTests.UploadCaptureAsync(phoneClient);
        var second = await RetentionSweepTests.UploadCaptureAsync(phoneClient);
        var unacknowledged = await RetentionSweepTests.UploadCaptureAsync(phoneClient);
        await RetentionSweepTests.PushStatusAsync(windowsClient, 1, first.CaptureId, SyncCaptureStatus.Completed);
        await RetentionSweepTests.PushStatusAsync(windowsClient, 2, second.CaptureId, SyncCaptureStatus.Completed);
        await RetentionSweepTests.PushStatusAsync(windowsClient, 3, unacknowledged.CaptureId, SyncCaptureStatus.ReviewReady);

        // 300 bytes held against a 150-byte budget, none of it old enough to
        // purge by age: both acknowledged artifacts go (oldest first), the
        // review-ready one stays even though the store is still over budget.
        var sweep = _fixture.Services.GetRequiredService<IArtifactRetentionService>();
        var report = await sweep.SweepAsync(DateTime.UtcNow, CancellationToken.None);
        report.ArtifactsPurgedByAge.Should().Be(0);
        report.ArtifactsPurgedByPressure.Should().Be(2);

        (await RetentionSweepTests.DownloadAsync(phoneClient, first.ArtifactId)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await RetentionSweepTests.DownloadAsync(phoneClient, second.ArtifactId)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await RetentionSweepTests.DownloadAsync(phoneClient, unacknowledged.ArtifactId)).StatusCode.Should().Be(
            HttpStatusCode.OK, "Windows may still need audio it has not archived");
    }
}
