using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="IArtifactService"/> over the sync database and
/// <see cref="IArtifactStore"/>. Stateless over the context factory;
/// registered as a singleton. Logs identifiers and byte counts only — never
/// file names or content (design §14.5).
/// </summary>
public sealed class ArtifactService : IArtifactService
{
    private static readonly string[] AllowedArtifactTypes =
        ["audio_original", "audio_normalized", "transcript", "waveform", "image", "metadata"];

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly IArtifactStore _store;
    private readonly SyncApiOptions _options;
    private readonly ILogger<ArtifactService> _logger;

    public ArtifactService(
        IDbContextFactory<SyncDbContext> contextFactory,
        IArtifactStore store,
        IOptions<SyncApiOptions> options,
        ILogger<ArtifactService> logger)
    {
        _contextFactory = contextFactory;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<ArtifactInitiateResult>> InitiateAsync(
        Device caller,
        string captureId,
        ArtifactInitiateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedByteLength <= 0)
        {
            return ServiceResult<ArtifactInitiateResult>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                "expectedByteLength must be positive.");
        }

        if (!IsSha256Hex(request.ExpectedSha256))
        {
            return ServiceResult<ArtifactInitiateResult>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                "expectedSha256 must be 64 hex characters.");
        }

        var artifactType = string.IsNullOrWhiteSpace(request.ArtifactType)
            ? "audio_original"
            : request.ArtifactType.Trim().ToLowerInvariant();
        if (!AllowedArtifactTypes.Contains(artifactType))
        {
            return ServiceResult<ArtifactInitiateResult>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                "artifactType must be one of: audio_original, audio_normalized, transcript, waveform, image, metadata.");
        }

        if (!Guid.TryParse(captureId, out var captureGuid))
        {
            return ServiceResult<ArtifactInitiateResult>.Fail(
                StatusCodes.Status404NotFound, SyncApiErrorCodes.CaptureNotFound, "No such capture.");
        }

        var normalizedCaptureId = captureGuid.ToString("D");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var capture = await db.Captures.AsNoTracking().FirstOrDefaultAsync(
            c => c.CaptureId == normalizedCaptureId && c.OwnerId == caller.OwnerId, cancellationToken);
        if (capture is null)
        {
            return ServiceResult<ArtifactInitiateResult>.Fail(
                StatusCodes.Status404NotFound, SyncApiErrorCodes.CaptureNotFound, "No such capture.");
        }

        // SECURITY: the artifact ID names filesystem paths — only a parsed GUID
        // (re-serialized in "D" format) is ever used, never the raw client string.
        var artifactGuid = Guid.TryParse(request.ArtifactId, out var requested) ? requested : Guid.NewGuid();
        var artifactId = artifactGuid.ToString("D");
        var expectedSha = request.ExpectedSha256.ToLowerInvariant();

        var existing = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken);
        if (existing is not null)
        {
            if (existing.CaptureId != normalizedCaptureId)
            {
                return ServiceResult<ArtifactInitiateResult>.Fail(
                    StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactConflict,
                    "This artifact ID is already in use by another capture.");
            }

            if (existing.ExpectedByteLength != request.ExpectedByteLength
                || !string.Equals(existing.ExpectedSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceResult<ArtifactInitiateResult>.Fail(
                    StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactConflict,
                    "This artifact ID was already initiated with different integrity data.");
            }

            _logger.LogInformation("Artifact {ArtifactId} initiate replayed idempotently.", artifactId);
            return ServiceResult<ArtifactInitiateResult>.Ok(new ArtifactInitiateResult(
                BuildInitiateResponse(artifactId, existing.ExpectedByteLength), Created: false));
        }

        db.Artifacts.Add(new ServerArtifact
        {
            ArtifactId = artifactId,
            CaptureId = normalizedCaptureId,
            ArtifactType = artifactType,
            MediaType = request.MediaType,
            OriginalFileName = request.OriginalFileName,
            ExpectedByteLength = request.ExpectedByteLength,
            ExpectedSha256 = expectedSha,
            State = ArtifactState.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a concurrent initiate of the same artifactId: replay the winner if compatible.
            await using var retryDb = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var winner = await retryDb.Artifacts.AsNoTracking().FirstOrDefaultAsync(
                a => a.ArtifactId == artifactId && a.CaptureId == normalizedCaptureId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return ServiceResult<ArtifactInitiateResult>.Ok(new ArtifactInitiateResult(
                BuildInitiateResponse(artifactId, winner.ExpectedByteLength), Created: false));
        }

        _logger.LogInformation(
            "Artifact {ArtifactId} initiated on capture {CaptureId} (type {ArtifactType}, {ByteLength} bytes expected).",
            artifactId, normalizedCaptureId, artifactType, request.ExpectedByteLength);
        return ServiceResult<ArtifactInitiateResult>.Ok(new ArtifactInitiateResult(
            BuildInitiateResponse(artifactId, request.ExpectedByteLength), Created: true));
    }

    public async Task<ServiceResult<bool>> UploadPartAsync(
        Device caller,
        string artifactId,
        int partNumber,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (artifact, error) = await LoadArtifactAsync(db, caller, artifactId, cancellationToken);
        if (artifact is null)
        {
            return ServiceResult<bool>.Fail(error!);
        }

        if (artifact.State is not (ArtifactState.Pending or ArtifactState.Uploading))
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactInvalidState,
                $"Parts cannot be uploaded while the artifact is {artifact.State}.");
        }

        var partSize = _options.MaxPartSizeBytes;
        var expectedPartCount = ComputePartCount(artifact.ExpectedByteLength, partSize);
        if (partNumber < 1 || partNumber > expectedPartCount)
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ArtifactPartInvalid,
                $"partNumber must be between 1 and {expectedPartCount}.");
        }

        if (declaredLength is > 0 && declaredLength > partSize)
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status413PayloadTooLarge, SyncApiErrorCodes.ArtifactPartTooLarge,
                $"Parts must be at most {partSize} bytes.");
        }

        var artifactGuid = Guid.Parse(artifact.ArtifactId);
        long written;
        try
        {
            written = await _store.WritePartAsync(artifactGuid, partNumber, content, partSize, cancellationToken);
        }
        catch (ArtifactPartTooLargeException)
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status413PayloadTooLarge, SyncApiErrorCodes.ArtifactPartTooLarge,
                $"Parts must be at most {partSize} bytes.");
        }

        if (artifact.State == ArtifactState.Pending)
        {
            artifact.State = ArtifactState.Uploading;
            await db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogDebug(
            "Artifact {ArtifactId} part {PartNumber} stored ({ByteLength} bytes).",
            artifact.ArtifactId, partNumber, written);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<ArtifactCompleteResponse>> CompleteAsync(
        Device caller,
        string artifactId,
        ArtifactCompleteRequest request,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (artifact, error) = await LoadArtifactAsync(db, caller, artifactId, cancellationToken);
        if (artifact is null)
        {
            return ServiceResult<ArtifactCompleteResponse>.Fail(error!);
        }

        if (artifact.State == ArtifactState.Complete)
        {
            // Idempotent replay: completing again with the same declared hash succeeds.
            if (artifact.ActualSha256 is not null
                && string.Equals(artifact.ActualSha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceResult<ArtifactCompleteResponse>.Ok(new ArtifactCompleteResponse
                {
                    ArtifactId = artifact.ArtifactId,
                    State = ArtifactState.Complete,
                    ByteLength = artifact.ActualByteLength ?? 0,
                    Sha256 = artifact.ActualSha256,
                });
            }

            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactInvalidState,
                "The artifact is already complete with different content.");
        }

        if (artifact.State is not (ArtifactState.Pending or ArtifactState.Uploading))
        {
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactInvalidState,
                $"The artifact cannot be completed while {artifact.State}.");
        }

        var expectedPartCount = ComputePartCount(artifact.ExpectedByteLength, _options.MaxPartSizeBytes);

        // Validate the completion claim against the initiate declaration first —
        // the assembled bytes are then compared against the same declaration,
        // covering both sources (design §11.4).
        if (request.PartCount != expectedPartCount)
        {
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status422UnprocessableEntity, SyncApiErrorCodes.ArtifactPartCountMismatch,
                $"partCount {request.PartCount} does not match the expected {expectedPartCount}.");
        }

        if (request.TotalByteLength != artifact.ExpectedByteLength)
        {
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status422UnprocessableEntity, SyncApiErrorCodes.ArtifactLengthMismatch,
                $"totalByteLength {request.TotalByteLength} does not match the declared {artifact.ExpectedByteLength}.");
        }

        if (!IsSha256Hex(request.Sha256)
            || !string.Equals(request.Sha256, artifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status422UnprocessableEntity, SyncApiErrorCodes.ArtifactHashMismatch,
                "sha256 does not match the declared expectedSha256.");
        }

        var artifactGuid = Guid.Parse(artifact.ArtifactId);
        var missingPart = _store.FirstMissingPart(artifactGuid, expectedPartCount);
        if (missingPart is not null)
        {
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactIncompleteParts,
                $"Part {missingPart} has not been uploaded.", retryable: true);
        }

        var assembly = await _store.AssembleToTempAsync(artifactGuid, expectedPartCount, cancellationToken);

        if (assembly.ByteLength != artifact.ExpectedByteLength)
        {
            _store.DiscardAssembly(assembly.TempPath);
            _logger.LogWarning(
                "Artifact {ArtifactId} length mismatch: assembled {ActualLength}, declared {ExpectedLength}.",
                artifact.ArtifactId, assembly.ByteLength, artifact.ExpectedByteLength);
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status422UnprocessableEntity, SyncApiErrorCodes.ArtifactLengthMismatch,
                $"Assembled length {assembly.ByteLength} does not match the declared {artifact.ExpectedByteLength}.",
                retryable: true);
        }

        if (!string.Equals(assembly.Sha256Hex, artifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _store.DiscardAssembly(assembly.TempPath);
            _logger.LogWarning("Artifact {ArtifactId} SHA-256 mismatch on completion.", artifact.ArtifactId);
            return ServiceResult<ArtifactCompleteResponse>.Fail(
                StatusCodes.Status422UnprocessableEntity, SyncApiErrorCodes.ArtifactHashMismatch,
                "Assembled SHA-256 does not match the declared expectedSha256.", retryable: true);
        }

        _store.CommitAssembly(artifactGuid, assembly.TempPath);

        artifact.State = ArtifactState.Complete;
        artifact.ActualByteLength = assembly.ByteLength;
        artifact.ActualSha256 = assembly.Sha256Hex;
        artifact.CompletedAtUtc = DateTime.UtcNow;

        var capture = await db.Captures.FirstOrDefaultAsync(
            c => c.CaptureId == artifact.CaptureId && c.OwnerId == caller.OwnerId, cancellationToken);
        if (capture is not null)
        {
            capture.Status = SyncCaptureStatus.Received;
            capture.Revision++;
            db.SyncChanges.Add(EntityMappers.CreateCaptureChange(
                capture,
                EntityMappers.IsAudioArtifact(artifact) ? EntityMappers.ToSummary(artifact) : null,
                caller.DeviceId));
        }

        await db.SaveChangesAsync(cancellationToken);

        // Parts are only removed after the durable state change; a failure above
        // leaves them in place so completion can be retried.
        _store.DeleteParts(artifactGuid);

        _logger.LogInformation(
            "Artifact {ArtifactId} completed ({ByteLength} bytes) on capture {CaptureId}.",
            artifact.ArtifactId, assembly.ByteLength, artifact.CaptureId);
        return ServiceResult<ArtifactCompleteResponse>.Ok(new ArtifactCompleteResponse
        {
            ArtifactId = artifact.ArtifactId,
            State = ArtifactState.Complete,
            ByteLength = assembly.ByteLength,
            Sha256 = assembly.Sha256Hex,
        });
    }

    public async Task<ServiceResult<ArtifactDownloadInfo>> GetDownloadAsync(
        Device caller,
        string artifactId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (artifact, error) = await LoadArtifactAsync(db, caller, artifactId, cancellationToken);
        if (artifact is null)
        {
            return ServiceResult<ArtifactDownloadInfo>.Fail(error!);
        }

        if (artifact.State == ArtifactState.Purged)
        {
            // Permanent by design: the retention sweep removed the bytes after
            // Windows archived the capture. A retryable code here would have a
            // client re-request forever for content that is not coming back.
            return ServiceResult<ArtifactDownloadInfo>.Fail(
                StatusCodes.Status410Gone, SyncApiErrorCodes.ArtifactPurged,
                "The artifact content was removed by the retention sweep.");
        }

        if (artifact.State != ArtifactState.Complete)
        {
            return ServiceResult<ArtifactDownloadInfo>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactNotComplete,
                "The artifact upload has not completed.");
        }

        var artifactGuid = Guid.Parse(artifact.ArtifactId);
        if (!_store.AssembledExists(artifactGuid))
        {
            _logger.LogError("Artifact {ArtifactId} is complete but its blob is missing on disk.", artifact.ArtifactId);
            return ServiceResult<ArtifactDownloadInfo>.Fail(
                StatusCodes.Status500InternalServerError, SyncApiErrorCodes.InternalError,
                "The artifact content is unavailable.", retryable: true);
        }

        return ServiceResult<ArtifactDownloadInfo>.Ok(new ArtifactDownloadInfo(
            _store.GetAssembledPath(artifactGuid),
            string.IsNullOrWhiteSpace(artifact.MediaType) ? "application/octet-stream" : artifact.MediaType));
    }

    private ArtifactInitiateResponse BuildInitiateResponse(string artifactId, long expectedByteLength) => new()
    {
        ArtifactId = artifactId,
        PartSizeBytes = _options.MaxPartSizeBytes,
        ExpectedPartCount = ComputePartCount(expectedByteLength, _options.MaxPartSizeBytes),
    };

    private static int ComputePartCount(long byteLength, long partSize)
        => (int)((byteLength + partSize - 1) / partSize);

    private static bool IsSha256Hex(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Loads an artifact and enforces owner scope via its capture; 404 hides other owners' artifacts.</summary>
    private static async Task<(ServerArtifact? Artifact, ServiceError? Error)> LoadArtifactAsync(
        SyncDbContext db,
        Device caller,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var notFound = new ServiceError(
            StatusCodes.Status404NotFound, SyncApiErrorCodes.ArtifactNotFound, "No such artifact.");

        if (!Guid.TryParse(artifactId, out var artifactGuid))
        {
            return (null, notFound);
        }

        var normalizedId = artifactGuid.ToString("D");
        var artifact = await db.Artifacts.FirstOrDefaultAsync(a => a.ArtifactId == normalizedId, cancellationToken);
        if (artifact is null)
        {
            return (null, notFound);
        }

        var ownsCapture = await db.Captures.AsNoTracking().AnyAsync(
            c => c.CaptureId == artifact.CaptureId && c.OwnerId == caller.OwnerId, cancellationToken);
        return ownsCapture ? (artifact, null) : (null, notFound);
    }
}
