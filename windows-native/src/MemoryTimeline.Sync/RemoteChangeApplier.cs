using System.Text.Json;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="IRemoteChangeApplier"/> (design §7.4): capture upserts resolve to
/// download-artifact → idempotent ingestion → optional queue processing, a
/// <c>pending_event_decision</c> is handed to
/// <see cref="IPendingEventDecisionApplier"/>, an <c>assistant_turn</c> to
/// <see cref="IAssistantTurnResponder"/>; every other entity type/operation
/// is skipped. The local audio cache path is keyed by capture AND artifact
/// identity, so a later conflicting artifact for an already-ingested capture
/// downloads to a different file and can never clobber the audio the ingested
/// queue item points at. Exceptions never escape <see cref="ApplyAsync"/> — each
/// change maps to a <see cref="ChangeApplicationResult"/> so the worker can
/// decide whether the cursor advances.
/// </summary>
public class RemoteChangeApplier : IRemoteChangeApplier
{
    private readonly IArtifactTransferClient _transferClient;
    private readonly ICaptureIngestionService _ingestionService;
    private readonly IQueueService _queueService;
    private readonly ISyncSettingsStore _settingsStore;
    private readonly ILogger<RemoteChangeApplier> _logger;
    private readonly IPendingEventDecisionApplier? _decisionApplier;
    private readonly IAssistantTurnResponder? _assistantResponder;

    /// <summary>
    /// Creates the applier. The two trailing collaborators are optional like
    /// the status publisher elsewhere: without the decision applier a
    /// companion's verdicts are skipped, without the responder a companion's
    /// questions are skipped, and either way the cycle itself keeps running.
    /// </summary>
    public RemoteChangeApplier(
        IArtifactTransferClient transferClient,
        ICaptureIngestionService ingestionService,
        IQueueService queueService,
        ISyncSettingsStore settingsStore,
        ILogger<RemoteChangeApplier> logger,
        IPendingEventDecisionApplier? decisionApplier = null,
        IAssistantTurnResponder? assistantResponder = null)
    {
        _transferClient = transferClient;
        _ingestionService = ingestionService;
        _queueService = queueService;
        _settingsStore = settingsStore;
        _logger = logger;
        _decisionApplier = decisionApplier;
        _assistantResponder = assistantResponder;
    }

    /// <inheritdoc />
    public async Task<ChangeApplicationResult> ApplyAsync(
        SyncChangeDto change, CancellationToken cancellationToken = default)
    {
        try
        {
            // The one entity a companion may author besides its own captures.
            // It goes to a dedicated applier because approving is a review-stack
            // operation, not an artifact one.
            if (string.Equals(
                    change.EntityType,
                    SyncChangeEntityType.PendingEventDecision,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_decisionApplier == null)
                {
                    _logger.LogDebug(
                        "Skipping change {ChangeId}: no pending event decision applier is registered",
                        change.ChangeId);
                    return ChangeApplicationResult.Skipped;
                }

                return await _decisionApplier.ApplyAsync(change, cancellationToken);
            }

            // A question asked from a companion (Phase 4). Answered by the
            // responder, which writes the answer to the outbox for the drain
            // that follows this pull; chunks are a responder's own output and
            // never arrive here as work.
            if (string.Equals(
                    change.EntityType,
                    SyncChangeEntityType.AssistantTurn,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_assistantResponder == null)
                {
                    _logger.LogDebug(
                        "Skipping change {ChangeId}: no assistant turn responder is registered", change.ChangeId);
                    return ChangeApplicationResult.Skipped;
                }

                return await _assistantResponder.ApplyAsync(change, cancellationToken);
            }

            if (!string.Equals(change.EntityType, SyncChangeEntityType.Capture, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Skipping change {ChangeId}: entity type {EntityType} is not applied by this client",
                    change.ChangeId, change.EntityType);
                return ChangeApplicationResult.Skipped;
            }

            if (string.Equals(change.Operation, SyncOperation.Delete, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Skipping change {ChangeId}: delete operations are not applied in Phase 1", change.ChangeId);
                return ChangeApplicationResult.Skipped;
            }

            var payload = ParsePayload(change);
            if (payload == null)
            {
                return ChangeApplicationResult.FailedPermanent;
            }

            // Echo defense: pulls exclude the caller's own changes server-side,
            // but a Windows-originated capture must never round-trip regardless.
            if (string.Equals(payload.SourcePlatform, CapturePlatform.Windows, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Skipping change {ChangeId}: capture {CaptureId} originated on this platform",
                    change.ChangeId, payload.CaptureId);
                return ChangeApplicationResult.Skipped;
            }

            var audioArtifact = payload.AudioArtifact;
            if (audioArtifact == null ||
                !string.Equals(audioArtifact.State, ArtifactState.Complete, StringComparison.OrdinalIgnoreCase))
            {
                // Metadata-only change; the artifact-complete change follows and
                // is the one that triggers ingestion.
                _logger.LogDebug(
                    "Skipping change {ChangeId}: capture {CaptureId} has no complete audio artifact yet",
                    change.ChangeId, payload.CaptureId);
                return ChangeApplicationResult.Skipped;
            }

            // The artifact ID becomes part of a local cache file name (like the
            // capture ID, validated in ParsePayload) — reject anything that
            // could escape the cache directory.
            if (string.IsNullOrWhiteSpace(audioArtifact.ArtifactId) ||
                audioArtifact.ArtifactId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _logger.LogError(
                    "Change {ChangeId} artifact ID for capture {CaptureId} is missing or contains invalid file name characters; permanently skipped",
                    change.ChangeId, payload.CaptureId);
                return ChangeApplicationResult.FailedPermanent;
            }

            var localPath = BuildLocalAudioPath(payload.CaptureId, audioArtifact);

            if (RequiresDownload(localPath, audioArtifact))
            {
                try
                {
                    await _transferClient.DownloadArtifactAsync(audioArtifact.ArtifactId, localPath, cancellationToken);
                }
                catch (SyncApiException ex) when (ex.Retryable)
                {
                    _logger.LogWarning(
                        ex,
                        "Artifact {ArtifactId} for capture {CaptureId} could not be downloaded; change {ChangeId} will be retried",
                        audioArtifact.ArtifactId, payload.CaptureId, change.ChangeId);
                    return ChangeApplicationResult.FailedRetryable;
                }
                catch (SyncApiException ex)
                {
                    _logger.LogError(
                        ex,
                        "Artifact {ArtifactId} for capture {CaptureId} failed to download permanently; change {ChangeId} is skipped",
                        audioArtifact.ArtifactId, payload.CaptureId, change.ChangeId);
                    return ChangeApplicationResult.FailedPermanent;
                }
            }
            else
            {
                _logger.LogDebug(
                    "Artifact {ArtifactId} for capture {CaptureId} is already cached locally",
                    audioArtifact.ArtifactId, payload.CaptureId);
            }

            var envelope = BuildEnvelope(payload, audioArtifact, localPath);
            var result = await _ingestionService.IngestRemoteCaptureAsync(envelope, cancellationToken);

            if (result.Success)
            {
                if (!result.AlreadyIngested && !string.IsNullOrEmpty(result.QueueId))
                {
                    await TryStartProcessingAsync(result.QueueId, cancellationToken);
                }

                return ChangeApplicationResult.Applied;
            }

            switch (result.FailureKind)
            {
                case IngestionFailureKind.InvalidRequest:
                case IngestionFailureKind.ArtifactInvalid:
                    _logger.LogError(
                        "Capture {CaptureId} from change {ChangeId} failed ingestion permanently ({FailureKind}): {Reason}",
                        payload.CaptureId, change.ChangeId, result.FailureKind, result.FailureReason);
                    return ChangeApplicationResult.FailedPermanent;
                default:
                    _logger.LogWarning(
                        "Capture {CaptureId} from change {ChangeId} failed ingestion retryably ({FailureKind}): {Reason}",
                        payload.CaptureId, change.ChangeId, result.FailureKind, result.FailureReason);
                    return ChangeApplicationResult.FailedRetryable;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Applying change {ChangeId} was cancelled; it will be retried", change.ChangeId);
            return ChangeApplicationResult.FailedRetryable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying change {ChangeId} failed unexpectedly; it will be retried", change.ChangeId);
            return ChangeApplicationResult.FailedRetryable;
        }
    }

    #region Private Methods

    private CaptureChangePayload? ParsePayload(SyncChangeDto change)
    {
        if (string.IsNullOrWhiteSpace(change.PayloadJson))
        {
            _logger.LogError(
                "Change {ChangeId} for capture entity {EntityId} carries no payload; permanently skipped",
                change.ChangeId, change.EntityId);
            return null;
        }

        CaptureChangePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CaptureChangePayload>(change.PayloadJson, SyncJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex, "Change {ChangeId} payload could not be parsed as a capture change; permanently skipped",
                change.ChangeId);
            return null;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.CaptureId))
        {
            _logger.LogError(
                "Change {ChangeId} payload has no capture ID; permanently skipped", change.ChangeId);
            return null;
        }

        // The capture ID becomes a local cache file name — reject anything that
        // could escape the cache directory.
        if (payload.CaptureId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _logger.LogError(
                "Change {ChangeId} capture ID contains invalid file name characters; permanently skipped",
                change.ChangeId);
            return null;
        }

        return payload;
    }

    /// <summary>
    /// Cache path keyed by capture AND artifact identity: replays of the same
    /// artifact reuse the cached file, while a conflicting artifact for an
    /// already-ingested capture writes a different file instead of overwriting
    /// the audio the existing queue item's AudioFilePath/StorageLocator point at
    /// (the download runs before ingestion's same-ID/different-content check).
    /// </summary>
    private static string BuildLocalAudioPath(string captureId, ArtifactSummary artifact)
    {
        // The SyncedAudio tree is one of the recording roots the cleanup sweep
        // and the normalizer know about; the layout is defined once, in Core.
        var paths = RecordingStoragePaths.Default;
        Directory.CreateDirectory(paths.SyncedAudioRoot);
        return paths.BuildSyncedAudioPath(captureId, artifact.ArtifactId, ExtensionFor(artifact));
    }

    private static string ExtensionFor(ArtifactSummary artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.OriginalFileName))
        {
            var extension = Path.GetExtension(artifact.OriginalFileName);
            if (!string.IsNullOrEmpty(extension) &&
                extension.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                return extension;
            }
        }

        return artifact.MediaType?.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
            "audio/mpeg" => ".mp3",
            "audio/aac" => ".aac",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            _ => ".bin",
        };
    }

    /// <summary>Skips the download when the cached file already has the expected byte length.</summary>
    private static bool RequiresDownload(string localPath, ArtifactSummary artifact)
    {
        if (!File.Exists(localPath))
        {
            return true;
        }

        if (artifact.ByteLength is not long expectedLength)
        {
            return true;
        }

        return new FileInfo(localPath).Length != expectedLength;
    }

    private static RemoteCaptureEnvelope BuildEnvelope(
        CaptureChangePayload payload, ArtifactSummary artifact, string localPath) => new()
    {
        CaptureId = payload.CaptureId,
        SourceDeviceId = string.IsNullOrWhiteSpace(payload.SourceDeviceId) ? null : payload.SourceDeviceId,
        SourcePlatform = payload.SourcePlatform,
        CaptureType = payload.CaptureType,
        CapturedAtUtc = payload.CapturedAtUtc,
        TimezoneId = payload.TimezoneId,
        LocalOffsetMinutes = payload.LocalOffsetMinutes,
        TitleHint = payload.TitleHint,
        UserNote = payload.UserNote,
        TripId = payload.TripId,
        ClientSchemaVersion = payload.ClientSchemaVersion,
        ArtifactId = artifact.ArtifactId,
        OriginalFileName = artifact.OriginalFileName,
        MediaType = artifact.MediaType,
        ExpectedByteLength = artifact.ByteLength ?? 0,
        ExpectedSha256 = artifact.Sha256 ?? string.Empty,
        StorageLocator = localPath,
    };

    private async Task TryStartProcessingAsync(string queueId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _settingsStore.IsAutoProcessEnabledAsync())
            {
                _logger.LogDebug(
                    "Auto-processing is disabled; queue item {QueueId} stays pending for manual review", queueId);
                return;
            }

            await _queueService.ProcessCaptureAsync(queueId, cancellationToken);
        }
        catch (Exception ex)
        {
            // Processing failures are already visible as queue item states in the
            // review UI; they must not fail the sync cycle (the capture IS ingested).
            _logger.LogWarning(
                ex, "Auto-processing queue item {QueueId} failed; the item remains visible in the review queue",
                queueId);
        }
    }

    #endregion
}
