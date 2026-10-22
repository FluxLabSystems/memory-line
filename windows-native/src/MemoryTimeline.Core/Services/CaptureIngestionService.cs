using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Capture ingestion implementation. Registered as a SINGLETON: all dependencies
/// are stateless over the DbContext factory.
///
/// <para><b>Normalization (design §7.2, §8.5).</b> A source that is not already
/// 16 kHz mono 16-bit PCM WAV — an iPhone's AAC <c>.m4a</c>, an imported MP3, a
/// 44.1 kHz WAV — is handed to the <see cref="IAudioNormalizer"/> before the
/// rows are written. The original stays the <c>audio_original</c> artifact
/// (hash, length and idempotency are all about the bytes the device
/// produced); the WAV becomes an <c>audio_normalized</c> artifact written in
/// the same SaveChanges, and the queue item transcribes it. Without a
/// normalizer, or when it cannot convert, the item still enters the queue
/// pointing at the original so the capture is visible and the transcription
/// step reports the format problem — a capture is never dropped on the floor
/// because of its container.</para>
/// </summary>
public class CaptureIngestionService : ICaptureIngestionService
{
    private readonly ICaptureRepository _captureRepository;
    private readonly IRecordingQueueRepository _queueRepository;
    private readonly IArtifactResolver _artifactResolver;
    private readonly ICaptureStatusPublisher? _statusPublisher;
    private readonly IAudioNormalizer? _audioNormalizer;
    private readonly RecordingStoragePaths _storagePaths;
    private readonly ILogger<CaptureIngestionService> _logger;

    /// <summary>
    /// Creates the service. The trailing parameters are optional so every
    /// existing construction keeps compiling: production injects the
    /// registered status publisher and normalizer; tests pass what they
    /// exercise. <paramref name="storagePaths"/> defaults to the production
    /// layout (the container cannot build it, so it arrives as null there).
    /// </summary>
    public CaptureIngestionService(
        ICaptureRepository captureRepository,
        IRecordingQueueRepository queueRepository,
        IArtifactResolver artifactResolver,
        ILogger<CaptureIngestionService> logger,
        ICaptureStatusPublisher? statusPublisher = null,
        IAudioNormalizer? audioNormalizer = null,
        RecordingStoragePaths? storagePaths = null)
    {
        _captureRepository = captureRepository;
        _queueRepository = queueRepository;
        _artifactResolver = artifactResolver;
        _logger = logger;
        _statusPublisher = statusPublisher;
        _audioNormalizer = audioNormalizer;
        _storagePaths = storagePaths ?? RecordingStoragePaths.Default;
    }

    public async Task<IngestionResult> IngestLocalRecordingAsync(
        AudioRecordingDto recording,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(recording.AudioFilePath))
        {
            return IngestionResult.Failed(IngestionFailureKind.InvalidRequest, "Recording has no audio file path.");
        }

        var queueId = string.IsNullOrWhiteSpace(recording.QueueId)
            ? Guid.NewGuid().ToString()
            : recording.QueueId;

        // Idempotent replay: the recording service pre-generates the queue ID, so
        // a retried hand-off must not create a second queue item.
        var existing = await _queueRepository.GetByIdAsync(queueId);
        if (existing != null)
        {
            _logger.LogInformation("Recording {QueueId} already ingested; returning existing item", queueId);
            return IngestionResult.Duplicate(
                existing.SourceCaptureId ?? string.Empty, existing.AudioArtifactId, existing.QueueId);
        }

        if (!File.Exists(recording.AudioFilePath))
        {
            return IngestionResult.Failed(
                IngestionFailureKind.ArtifactUnavailable,
                $"Audio file not found: {recording.AudioFilePath}");
        }

        string sha256;
        long byteLength;
        try
        {
            (sha256, byteLength) = await ComputeSha256Async(recording.AudioFilePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hash recording {QueueId}", queueId);
            return IngestionResult.Failed(
                IngestionFailureKind.ArtifactUnavailable, $"Could not read audio file: {ex.Message}");
        }

        // Content-hash idempotency (§7.2, §12.2): identical bytes are the same
        // recording regardless of path or queue ID (e.g. the same file imported
        // twice under different copy names).
        var sameContent = await _captureRepository.GetQueueItemByContentSha256Async(sha256);
        if (sameContent != null)
        {
            _logger.LogInformation(
                "Recording {QueueId} matches content of existing queue item {ExistingQueueId}; reusing",
                queueId, sameContent.QueueId);
            return IngestionResult.Duplicate(
                sameContent.SourceCaptureId ?? string.Empty, sameContent.AudioArtifactId, sameContent.QueueId);
        }

        var now = DateTime.UtcNow;
        var capture = new Capture
        {
            CaptureId = Guid.NewGuid().ToString(),
            SourcePlatform = CapturePlatform.Windows,
            CaptureType = CaptureTypes.Unclassified,
            CreatedAtUtc = now,
            CapturedAtUtc = recording.CreatedAt == default ? now : recording.CreatedAt,
            Status = CaptureStatus.Received,
        };

        var artifact = new CaptureArtifact
        {
            ArtifactId = Guid.NewGuid().ToString(),
            CaptureId = capture.CaptureId,
            ArtifactType = CaptureArtifactType.AudioOriginal,
            StorageLocator = recording.AudioFilePath,
            MediaType = MediaTypeFromPath(recording.AudioFilePath),
            ByteLength = byteLength,
            Sha256 = sha256,
            CreatedAtUtc = now,
            UploadState = CaptureArtifactUploadState.Local,
        };

        var queueItem = new RecordingQueue
        {
            QueueId = queueId,
            AudioFilePath = recording.AudioFilePath,
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription,
            DurationSeconds = recording.DurationSeconds,
            FileSizeBytes = byteLength,
            CreatedAt = capture.CapturedAtUtc,
            SourceCaptureId = capture.CaptureId,
            SourcePlatform = CapturePlatform.Windows,
            AudioArtifactId = artifact.ArtifactId,
            OriginalFileName = Path.GetFileName(recording.AudioFilePath),
            ContentSha256 = sha256,
            SyncState = QueueSyncState.LocalOnly,
            ReceivedAt = now,
        };

        return await NormalizeAndPersistAsync(capture, artifact, queueItem, cancellationToken);
    }

    public async Task<IngestionResult> IngestRemoteCaptureAsync(
        RemoteCaptureEnvelope capture,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var validationError = ValidateEnvelope(capture);
        if (validationError != null)
        {
            return IngestionResult.Failed(IngestionFailureKind.InvalidRequest, validationError, capture.CaptureId);
        }

        // Idempotency: at most one queue item per capture ID, ever. Repeated
        // uploads and background-transfer retries land here.
        var existing = await _captureRepository.GetQueueItemBySourceCaptureIdAsync(capture.CaptureId);
        if (existing != null)
        {
            // Same ID with different content is a conflict, not a replay.
            if (existing.ContentSha256 != null &&
                !string.Equals(existing.ContentSha256, capture.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return IngestionResult.Failed(
                    IngestionFailureKind.ArtifactInvalid,
                    "Capture ID was already ingested with different content.",
                    capture.CaptureId);
            }

            _logger.LogInformation("Capture {CaptureId} already ingested as queue item {QueueId}",
                capture.CaptureId, existing.QueueId);
            return IngestionResult.Duplicate(capture.CaptureId, existing.AudioArtifactId, existing.QueueId);
        }

        // A capture row without a queue item means the queue item was deleted by
        // the user after a previous ingestion (queue removal does not delete the
        // capture). Deletions are never silently resurrected by a re-upload
        // (§12.2): acknowledge the duplicate without creating rows, instead of
        // colliding on the captures primary key forever.
        var priorCapture = await _captureRepository.GetByIdAsync(capture.CaptureId);
        if (priorCapture != null)
        {
            var priorAudio = priorCapture.Artifacts
                .FirstOrDefault(a => a.ArtifactType == CaptureArtifactType.AudioOriginal);
            if (priorAudio?.Sha256 != null &&
                !string.Equals(priorAudio.Sha256, capture.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return IngestionResult.Failed(
                    IngestionFailureKind.ArtifactInvalid,
                    "Capture ID was already ingested with different content.",
                    capture.CaptureId);
            }

            _logger.LogInformation(
                "Capture {CaptureId} was previously ingested and its queue item since removed; not resurrecting",
                capture.CaptureId);
            return IngestionResult.Duplicate(capture.CaptureId, priorAudio?.ArtifactId, null);
        }

        var resolution = await _artifactResolver.ResolveAsync(capture, cancellationToken);
        if (!resolution.Success || string.IsNullOrEmpty(resolution.LocalPath))
        {
            return IngestionResult.Failed(
                IngestionFailureKind.ArtifactUnavailable,
                resolution.Error ?? "Artifact could not be resolved.",
                capture.CaptureId);
        }

        string sha256;
        long byteLength;
        try
        {
            (sha256, byteLength) = await ComputeSha256Async(resolution.LocalPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hash artifact for capture {CaptureId}", capture.CaptureId);
            return IngestionResult.Failed(
                IngestionFailureKind.ArtifactUnavailable,
                $"Could not read artifact file: {ex.Message}",
                capture.CaptureId);
        }

        if (byteLength != capture.ExpectedByteLength)
        {
            return IngestionResult.Failed(
                IngestionFailureKind.ArtifactInvalid,
                $"Artifact byte length {byteLength} does not match declared {capture.ExpectedByteLength}.",
                capture.CaptureId);
        }

        if (!string.Equals(sha256, capture.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return IngestionResult.Failed(
                IngestionFailureKind.ArtifactInvalid,
                "Artifact SHA-256 does not match the declared hash.",
                capture.CaptureId);
        }

        // Content-hash idempotency (§7.2, §12.2): the same audio bytes under a
        // freshly minted capture ID (e.g. an app reinstall regenerating IDs for
        // unacknowledged recordings) must not produce a second queue item and
        // duplicate pending events.
        var sameContent = await _captureRepository.GetQueueItemByContentSha256Async(sha256);
        if (sameContent != null)
        {
            _logger.LogInformation(
                "Capture {CaptureId} matches content of existing queue item {QueueId}; reusing",
                capture.CaptureId, sameContent.QueueId);
            return IngestionResult.Duplicate(
                sameContent.SourceCaptureId ?? capture.CaptureId, sameContent.AudioArtifactId, sameContent.QueueId);
        }

        // The device's location fix stays on the capture (design §6.1): it is
        // not a named place, and it never enters the outbox payload written
        // below. A fix that is not on Earth is dropped, not fatal.
        var location = capture.Location is { IsInRange: true } ? capture.Location : null;

        var now = DateTime.UtcNow;
        var captureEntity = new Capture
        {
            CaptureId = capture.CaptureId,
            SourceDeviceId = capture.SourceDeviceId,
            SourcePlatform = string.IsNullOrWhiteSpace(capture.SourcePlatform)
                ? CapturePlatform.Other
                : capture.SourcePlatform,
            CaptureType = string.IsNullOrWhiteSpace(capture.CaptureType)
                ? CaptureTypes.Unclassified
                : capture.CaptureType,
            CreatedAtUtc = now,
            CapturedAtUtc = capture.CapturedAtUtc == default ? now : capture.CapturedAtUtc,
            TimezoneId = capture.TimezoneId,
            LocalOffsetMinutes = capture.LocalOffsetMinutes,
            Status = CaptureStatus.Received,
            TitleHint = capture.TitleHint,
            UserNote = capture.UserNote,
            TripId = capture.TripId,
            LocationLatitude = location?.Latitude,
            LocationLongitude = location?.Longitude,
            LocationAccuracyM = location?.HorizontalAccuracyM,
            ClientSchemaVersion = capture.ClientSchemaVersion,
        };

        var artifact = new CaptureArtifact
        {
            ArtifactId = string.IsNullOrWhiteSpace(capture.ArtifactId)
                ? Guid.NewGuid().ToString()
                : capture.ArtifactId,
            CaptureId = captureEntity.CaptureId,
            ArtifactType = CaptureArtifactType.AudioOriginal,
            StorageLocator = resolution.LocalPath,
            MediaType = capture.MediaType ?? MediaTypeFromPath(resolution.LocalPath),
            ByteLength = byteLength,
            Sha256 = sha256,
            CreatedAtUtc = now,
            UploadState = CaptureArtifactUploadState.Downloaded,
        };

        var queueItem = new RecordingQueue
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = resolution.LocalPath,
            Status = QueueStatus.Pending,
            ProcessingStage = QueueProcessingStage.ReadyForTranscription,
            FileSizeBytes = byteLength,
            CreatedAt = now,
            SourceCaptureId = captureEntity.CaptureId,
            SourceDeviceId = capture.SourceDeviceId,
            SourcePlatform = captureEntity.SourcePlatform,
            AudioArtifactId = artifact.ArtifactId,
            OriginalFileName = capture.OriginalFileName ?? Path.GetFileName(resolution.LocalPath),
            ContentSha256 = sha256,
            SyncState = QueueSyncState.Received,
            ReceivedAt = now,
        };

        return await NormalizeAndPersistAsync(captureEntity, artifact, queueItem, cancellationToken);
    }

    #region Private Methods

    /// <summary>
    /// Runs the normalization step, then the atomic write. A duplicate outcome
    /// from the write (a race lost to another ingestion of the same capture)
    /// means the normalized file just produced describes rows that were never
    /// written, so it is removed rather than left for the cleanup sweep.
    /// </summary>
    private async Task<IngestionResult> NormalizeAndPersistAsync(
        Capture capture,
        CaptureArtifact original,
        RecordingQueue queueItem,
        CancellationToken cancellationToken)
    {
        var normalized = await TryNormalizeAsync(capture, original, queueItem, cancellationToken);

        var artifacts = normalized == null
            ? new[] { original }
            : new[] { original, normalized };

        var result = await PersistAsync(capture, artifacts, queueItem);

        if (result.AlreadyIngested && normalized != null)
        {
            TryDeleteFile(normalized.StorageLocator);
        }

        return result;
    }

    /// <summary>
    /// Produces the <c>audio_normalized</c> artifact for a source the
    /// transcription engine cannot read as-is, and points the queue item at it.
    /// Returns null — leaving the queue item on the original — when the source
    /// is already transcription-ready, when no normalizer is registered, or
    /// when the normalizer cannot convert; the last two are logged, because the
    /// transcription step will fail on that item and the log is where the
    /// reason lives. Only cancellation propagates.
    /// </summary>
    private async Task<CaptureArtifact?> TryNormalizeAsync(
        Capture capture,
        CaptureArtifact original,
        RecordingQueue queueItem,
        CancellationToken cancellationToken)
    {
        var header = WavHeader.TryRead(original.StorageLocator);
        if (header is { IsTranscriptionReady: true })
        {
            // The recorder's own output. Fill in a duration the caller did not
            // know (imports and remote captures arrive without one).
            queueItem.DurationSeconds ??= PositiveSeconds(header.Duration);
            return null;
        }

        if (_audioNormalizer == null)
        {
            _logger.LogWarning(
                "Capture {CaptureId} ({MediaType}) is not transcription-ready WAV and no audio normalizer is registered; transcription will reject it",
                capture.CaptureId, original.MediaType);
            return null;
        }

        var destination = _storagePaths.BuildNormalizedPath(capture.CaptureId, original.ArtifactId);

        AudioNormalizationResult outcome;
        try
        {
            outcome = await _audioNormalizer.NormalizeAsync(original.StorageLocator, destination, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(destination);
            throw;
        }
        catch (Exception ex)
        {
            // The port's contract is "never throw for a format problem", but a
            // head implementation that does must not fail the ingestion.
            _logger.LogWarning(ex,
                "Normalizing capture {CaptureId} threw; the queue item keeps the original file", capture.CaptureId);
            TryDeleteFile(destination);
            return null;
        }

        if (!outcome.Succeeded || string.IsNullOrEmpty(outcome.OutputPath) || !File.Exists(outcome.OutputPath))
        {
            _logger.LogWarning(
                "Capture {CaptureId} ({MediaType}) could not be normalized for transcription: {Reason}",
                capture.CaptureId, original.MediaType, outcome.Error ?? "no output was produced");
            TryDeleteFile(destination);
            return null;
        }

        string sha256;
        long byteLength;
        try
        {
            (sha256, byteLength) = await ComputeSha256Async(outcome.OutputPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Normalized audio for capture {CaptureId} could not be read back; the queue item keeps the original file",
                capture.CaptureId);
            TryDeleteFile(outcome.OutputPath);
            return null;
        }

        var normalizedHeader = WavHeader.TryRead(outcome.OutputPath);
        queueItem.AudioFilePath = outcome.OutputPath;
        queueItem.DurationSeconds ??= normalizedHeader == null ? null : PositiveSeconds(normalizedHeader.Duration);

        _logger.LogInformation(
            "Normalized capture {CaptureId} ({MediaType}, {OriginalBytes} bytes) to 16 kHz WAV ({NormalizedBytes} bytes)",
            capture.CaptureId, original.MediaType, original.ByteLength, byteLength);

        return new CaptureArtifact
        {
            ArtifactId = Guid.NewGuid().ToString(),
            CaptureId = capture.CaptureId,
            ArtifactType = CaptureArtifactType.AudioNormalized,
            StorageLocator = outcome.OutputPath,
            MediaType = "audio/wav",
            ByteLength = byteLength,
            Sha256 = sha256,
            CreatedAtUtc = DateTime.UtcNow,
            // Produced here, for this machine's transcriber; it is never
            // uploaded, so it is "local" whatever the original's state is.
            UploadState = CaptureArtifactUploadState.Local,
        };
    }

    private static double? PositiveSeconds(TimeSpan duration) =>
        duration > TimeSpan.Zero ? duration.TotalSeconds : null;

    private void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}; the recordings cleanup will sweep it", path);
        }
    }

    private async Task<IngestionResult> PersistAsync(
        Capture capture,
        IReadOnlyList<CaptureArtifact> artifacts,
        RecordingQueue queueItem)
    {
        // The outbox describes the ORIGINAL artifact: that is the one the
        // capturing device knows, and the one its hash and length refer to.
        var artifact = artifacts[0];

        var outboxEntry = new SyncOutbox
        {
            EntityType = SyncEntityType.Capture,
            EntityId = capture.CaptureId,
            Operation = SyncOutboxOperation.Upsert,
            // Metadata only — never transcripts, notes, audio bytes, or location
            // (privacy rule §14.5).
            PayloadJson = JsonSerializer.Serialize(new
            {
                captureId = capture.CaptureId,
                queueId = queueItem.QueueId,
                artifactId = artifact.ArtifactId,
                sha256 = artifact.Sha256,
                byteLength = artifact.ByteLength,
                capturedAtUtc = capture.CapturedAtUtc,
                sourcePlatform = capture.SourcePlatform,
                status = capture.Status,
            }),
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            await _captureRepository.IngestAsync(capture, artifacts, queueItem, outboxEntry);
        }
        catch (DbUpdateException ex)
        {
            // A constraint conflict here means another row already explains this
            // capture; prefer the idempotent-duplicate outcome over a failure.

            // Remote-path race: both racers share the capture ID, so the loser
            // hits the source_capture_id unique index.
            var winner = await _captureRepository.GetQueueItemBySourceCaptureIdAsync(capture.CaptureId);
            if (winner != null)
            {
                _logger.LogInformation(
                    "Concurrent ingestion of capture {CaptureId} detected; returning existing queue item {QueueId}",
                    capture.CaptureId, winner.QueueId);
                return IngestionResult.Duplicate(capture.CaptureId, winner.AudioArtifactId, winner.QueueId);
            }

            // Local-path race: both racers share the pre-generated queue ID but
            // mint distinct capture IDs, so the conflict is the queue primary key.
            var byQueueId = await _queueRepository.GetByIdAsync(queueItem.QueueId);
            if (byQueueId != null)
            {
                _logger.LogInformation(
                    "Concurrent ingestion of recording {QueueId} detected; returning existing queue item",
                    queueItem.QueueId);
                return IngestionResult.Duplicate(
                    byQueueId.SourceCaptureId ?? capture.CaptureId, byQueueId.AudioArtifactId, byQueueId.QueueId);
            }

            // Orphaned capture row (queue item deleted between the pre-check and
            // this insert): the captures primary key rejected the re-insert.
            // Deletions are never resurrected (§12.2).
            if (await _captureRepository.ExistsAsync(capture.CaptureId))
            {
                _logger.LogInformation(
                    "Capture {CaptureId} already exists without a queue item; not resurrecting", capture.CaptureId);
                return IngestionResult.Duplicate(capture.CaptureId, null, null);
            }

            _logger.LogError(ex, "Failed to persist ingestion of capture {CaptureId}", capture.CaptureId);
            return IngestionResult.Failed(
                IngestionFailureKind.StorageFailure, $"Could not persist capture: {ex.Message}", capture.CaptureId);
        }

        _logger.LogInformation(
            "Ingested capture {CaptureId} ({Platform}) as queue item {QueueId}, artifact {ArtifactId}, {Bytes} bytes",
            capture.CaptureId, capture.SourcePlatform, queueItem.QueueId, artifact.ArtifactId, artifact.ByteLength);

        // First status the capturing device hears from Windows: received, and
        // waiting for the queue (design §19 Phase 3). Windows-local recordings
        // are filtered out by the publisher — nothing is waiting on those.
        // Publishing failure must never fail an ingestion that already
        // committed; the first stage transition republishes.
        if (_statusPublisher != null)
        {
            try
            {
                await _statusPublisher.PublishAsync(queueItem);
            }
            catch (Exception publishEx)
            {
                _logger.LogWarning(publishEx,
                    "Failed to publish received status for capture {CaptureId}", capture.CaptureId);
            }
        }

        return IngestionResult.Ingested(capture.CaptureId, artifact.ArtifactId, queueItem.QueueId);
    }

    private static string? ValidateEnvelope(RemoteCaptureEnvelope capture)
    {
        if (string.IsNullOrWhiteSpace(capture.CaptureId))
        {
            return "Capture ID is required.";
        }

        if (string.IsNullOrWhiteSpace(capture.ExpectedSha256))
        {
            return "Expected SHA-256 is required.";
        }

        if (capture.ExpectedByteLength <= 0)
        {
            return "Expected byte length must be positive.";
        }

        if (string.IsNullOrWhiteSpace(capture.StorageLocator))
        {
            return "Storage locator is required.";
        }

        return null;
    }

    private static async Task<(string Sha256, long ByteLength)> ComputeSha256Async(
        string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static string MediaTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".mp3" => "audio/mpeg",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            _ => "application/octet-stream",
        };

    #endregion
}
