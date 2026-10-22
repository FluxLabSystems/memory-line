using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Capture metadata lifecycle (design §6.1, §11.2). Creation is idempotent by
/// captureId; every accepted mutation appends a capture change row in the same
/// transaction so other devices see it via /sync/pull.
/// </summary>
public interface ICaptureService
{
    /// <summary>
    /// Creates a capture. The source device is always the caller from the
    /// access token — the request's sourceDeviceId is ignored. Replaying an
    /// existing captureId returns the current capture with Created=false.
    /// </summary>
    Task<ServiceResult<CaptureWriteResult>> CreateAsync(Device caller, CaptureCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Fetches a capture with its artifact summaries.</summary>
    Task<ServiceResult<CaptureResponse>> GetAsync(Device caller, string captureId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a partial update (captureType, titleHint, userNote, tripId —
    /// only keys present in <paramref name="body"/>; explicit null clears a
    /// nullable field). Bumps the revision and appends a change on any change.
    /// </summary>
    Task<ServiceResult<CaptureResponse>> UpdateAsync(Device caller, string captureId, JsonObject body, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the capture fully uploaded. Every declared artifact must be
    /// complete (or already purged by retention), or the call fails with 409
    /// capture_artifacts_incomplete — retryable, since finishing the upload
    /// fixes it. A capture still in a client-authored pre-upload status
    /// (local_only, uploading) advances to received and publishes a capture
    /// change carrying the completed audio artifact; a capture already at or
    /// past received is left alone, so a replay is a no-op that appends
    /// nothing.
    /// </summary>
    Task<ServiceResult<CaptureResponse>> CompleteAsync(Device caller, string captureId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-queues a failed capture: status goes back to received, the revision
    /// is bumped, and a capture change carrying the complete audio artifact is
    /// published so Windows can pick it up again. Only a failed capture may be
    /// retried (409 capture_not_failed otherwise), and not one whose audio the
    /// retention sweep has purged (409 artifact_purged — re-upload first).
    /// Windows authors the capture_status projection, so that row is left as
    /// it is until Windows reports on the retried run.
    /// </summary>
    Task<ServiceResult<CaptureResponse>> RetryAsync(Device caller, string captureId, CancellationToken cancellationToken);
}

/// <summary>A capture write outcome: the wire response plus whether a row was created (201) or replayed (200).</summary>
public sealed record CaptureWriteResult(CaptureResponse Response, bool Created);
