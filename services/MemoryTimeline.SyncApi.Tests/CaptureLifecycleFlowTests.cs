using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.SyncContracts;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// POST /captures/{id}/complete and /retry against the real host, as the
/// contract describes them: complete refuses until every declared artifact is
/// complete and never republishes a capture Windows already has; retry
/// re-queues a failed capture exactly once per failure and republishes its
/// audio, and refuses captures that are not failed.
/// </summary>
public class CaptureLifecycleFlowTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public CaptureLifecycleFlowTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Complete_WithNoDeclaredArtifact_Returns409Retryable()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var capture = await SyncApiFixture.CreateCaptureAsync(client);

        var response = await client.PostAsync($"/api/v1/captures/{capture.CaptureId}/complete", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("capture_artifacts_incomplete");
        error.Retryable.Should().BeTrue("declaring and uploading an artifact makes the same call succeed");
    }

    [Fact]
    public async Task Complete_WhileAnArtifactIsStillUploading_Returns409ThenSucceedsOnceUploaded()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var capture = await SyncApiFixture.CreateCaptureAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var content = SyncApiFixture.RandomBytes(100);
        var initiate = await SyncApiFixture.InitiateArtifactAsync(phoneClient, capture.CaptureId, content);
        // Only the first of two parts: the artifact is "uploading".
        (await SyncApiFixture.UploadPartAsync(phoneClient, initiate.ArtifactId, 1, content[..SyncApiFixture.PartSizeBytes]))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var early = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/complete", content: null);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var earlyError = await SyncApiFixture.ReadApiErrorAsync(early);
        earlyError.Code.Should().Be("capture_artifacts_incomplete");
        earlyError.Retryable.Should().BeTrue();
        earlyError.Message.Should().Contain(initiate.ArtifactId, "the client learns which artifact is holding things up");

        await SyncApiFixture.UploadPartsAsync(phoneClient, initiate.ArtifactId, content);
        (await SyncApiFixture.CompleteArtifactAsync(
                phoneClient, initiate.ArtifactId, partCount: 2, totalByteLength: 100, sha256: SyncApiFixture.Sha256Hex(content)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var done = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/complete", content: null);
        done.StatusCode.Should().Be(HttpStatusCode.OK);
        var completed = await done.Content.ReadFromJsonAsync<CaptureResponse>(SyncApiFixture.Json);
        completed!.Status.Should().Be(SyncCaptureStatus.Received);
        completed.Artifacts.Should().ContainSingle().Which.State.Should().Be(ArtifactState.Complete);

        // The capture was already `received` (set at creation) and the artifact
        // completion already published the ingestion change, so completing
        // appended nothing: after the baseline (taken after creation) Windows
        // sees the artifact completion and no second change.
        var pull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        var change = pull.Changes.Where(c => c.EntityId == capture.CaptureId).Should().ContainSingle().Which;
        JsonSerializer.Deserialize<CaptureChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!
            .AudioArtifact.Should().NotBeNull("that one change is the artifact completion");
        completed.ServerRevision.Should().Be(2, "create and artifact completion; complete itself bumped nothing");
    }

    [Fact]
    public async Task Complete_OnAnUploadingCapture_AdvancesItToReceivedAndPublishesExactlyOnce()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var capture = await SyncApiFixture.CreateCaptureAsync(phoneClient);
        var artifactId = await UploadAudioAsync(phoneClient, capture.CaptureId);

        // The phone's own outbox can carry a client-authored `uploading` status
        // for the capture entity; the service applies it to the capture row.
        await PushCaptureStatusAsync(phoneClient, 1, capture, SyncCaptureStatus.Uploading);
        var before = await phoneClient.GetFromJsonAsync<CaptureResponse>(
            $"/api/v1/captures/{capture.CaptureId}", SyncApiFixture.Json);
        before!.Status.Should().Be(SyncCaptureStatus.Uploading);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var done = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/complete", content: null);
        done.StatusCode.Should().Be(HttpStatusCode.OK);
        var completed = await done.Content.ReadFromJsonAsync<CaptureResponse>(SyncApiFixture.Json);
        completed!.Status.Should().Be(SyncCaptureStatus.Received);
        completed.ServerRevision.Should().Be(before.ServerRevision + 1);

        var pull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        var change = pull.Changes.Should().ContainSingle(c => c.EntityId == capture.CaptureId).Which;
        change.Revision.Should().Be(completed.ServerRevision);
        var payload = JsonSerializer.Deserialize<CaptureChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!;
        payload.Status.Should().Be(SyncCaptureStatus.Received);
        payload.AudioArtifact!.ArtifactId.Should().Be(artifactId);
        payload.AudioArtifact.State.Should().Be(ArtifactState.Complete);

        // Replaying complete is a no-op: same state back, nothing appended.
        var replay = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/complete", content: null);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<CaptureResponse>(SyncApiFixture.Json))!
            .ServerRevision.Should().Be(completed.ServerRevision);
        (await SyncApiFixture.PullAsync(windowsClient, pull.NextCursor)).Changes
            .Should().NotContain(c => c.EntityId == capture.CaptureId);
    }

    [Fact]
    public async Task Retry_OnAFailedCapture_RequeuesItAndRepublishesTheAudioForWindows()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var capture = await SyncApiFixture.CreateCaptureAsync(phoneClient);
        var artifactId = await UploadAudioAsync(phoneClient, capture.CaptureId);

        // Windows reports the processing failure the way its outbox does.
        await RetentionSweepTests.PushStatusAsync(windowsClient, 1, capture.CaptureId, SyncCaptureStatus.Failed);
        var failed = await phoneClient.GetFromJsonAsync<CaptureResponse>(
            $"/api/v1/captures/{capture.CaptureId}", SyncApiFixture.Json);
        failed!.Status.Should().Be(SyncCaptureStatus.Failed);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var retry = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/retry", content: null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var requeued = await retry.Content.ReadFromJsonAsync<CaptureResponse>(SyncApiFixture.Json);
        requeued!.Status.Should().Be(SyncCaptureStatus.Received);
        requeued.ServerRevision.Should().Be(failed.ServerRevision + 1);

        // Windows receives one capture change carrying the audio again.
        var pull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        var change = pull.Changes.Should().ContainSingle(c => c.EntityType == SyncChangeEntityType.Capture).Which;
        change.EntityId.Should().Be(capture.CaptureId);
        change.Revision.Should().Be(requeued.ServerRevision);
        var payload = JsonSerializer.Deserialize<CaptureChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!;
        payload.Status.Should().Be(SyncCaptureStatus.Received);
        payload.AudioArtifact!.ArtifactId.Should().Be(artifactId);

        // A second retry has nothing to retry: the capture is no longer failed.
        var again = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/retry", content: null);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SyncApiFixture.ReadApiErrorAsync(again)).Code.Should().Be("capture_not_failed");
    }

    [Fact]
    public async Task Retry_OnACaptureThatIsNotFailed_Returns409AndAppendsNothing()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var capture = await SyncApiFixture.CreateCaptureAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var response = await phoneClient.PostAsync($"/api/v1/captures/{capture.CaptureId}/retry", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await SyncApiFixture.ReadApiErrorAsync(response);
        error.Code.Should().Be("capture_not_failed");
        error.Retryable.Should().BeFalse();

        (await SyncApiFixture.PullAsync(windowsClient, windowsBaseline)).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAndRetry_OnAnUnknownCapture_Return404()
    {
        var (_, client) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var unknown = Guid.NewGuid().ToString("D");

        var complete = await client.PostAsync($"/api/v1/captures/{unknown}/complete", content: null);
        complete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SyncApiFixture.ReadApiErrorAsync(complete)).Code.Should().Be("capture_not_found");

        var retry = await client.PostAsync($"/api/v1/captures/{unknown}/retry", content: null);
        retry.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SyncApiFixture.ReadApiErrorAsync(retry)).Code.Should().Be("capture_not_found");
    }

    /// <summary>Uploads a complete 100-byte audio artifact on the capture and returns its ID.</summary>
    private static async Task<string> UploadAudioAsync(HttpClient client, string captureId)
    {
        var content = SyncApiFixture.RandomBytes(100);
        var initiate = await SyncApiFixture.InitiateArtifactAsync(client, captureId, content);
        await SyncApiFixture.UploadPartsAsync(client, initiate.ArtifactId, content);
        (await SyncApiFixture.CompleteArtifactAsync(
                client, initiate.ArtifactId, partCount: 2, totalByteLength: 100, sha256: SyncApiFixture.Sha256Hex(content)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return initiate.ArtifactId;
    }

    /// <summary>Pushes a client-authored capture entity change with the given status, as the phone's outbox would.</summary>
    private static async Task PushCaptureStatusAsync(HttpClient client, long clientSequence, CaptureResponse capture, string status)
    {
        var payload = new CaptureChangePayload
        {
            CaptureId = capture.CaptureId,
            SourceDeviceId = capture.SourceDeviceId,
            SourcePlatform = capture.SourcePlatform,
            CaptureType = capture.CaptureType,
            CapturedAtUtc = capture.CapturedAtUtc,
            Status = status,
        };
        var push = await SyncApiFixture.PushAsync(client, new SyncPushEntry
        {
            ClientSequence = clientSequence,
            EntityType = SyncChangeEntityType.Capture,
            EntityId = capture.CaptureId,
            Operation = SyncOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(payload, SyncApiFixture.Json),
            ChangedAtUtc = DateTime.UtcNow,
        });
        push.Results.Should().ContainSingle().Which.Accepted.Should().BeTrue();
    }
}
