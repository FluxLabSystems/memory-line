using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="ICaptureService"/> over the sync database. Stateless over the
/// context factory; registered as a singleton. Logs identifiers only — never
/// titles, notes, or location content (design §14.5).
/// </summary>
public sealed class CaptureService : ICaptureService
{
    private static readonly string[] AllowedCaptureTypes =
        ["memory", "idea", "trip_log", "reminder", "question", "unclassified"];

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly ILogger<CaptureService> _logger;

    public CaptureService(IDbContextFactory<SyncDbContext> contextFactory, ILogger<CaptureService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<ServiceResult<CaptureWriteResult>> CreateAsync(
        Device caller,
        CaptureCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.CaptureId, out var captureGuid))
        {
            return ServiceResult<CaptureWriteResult>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError, "captureId must be a UUID.");
        }

        if (request.CapturedAtUtc == default)
        {
            return ServiceResult<CaptureWriteResult>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError, "capturedAtUtc is required.");
        }

        var captureType = string.IsNullOrWhiteSpace(request.CaptureType)
            ? "unclassified"
            : request.CaptureType.Trim().ToLowerInvariant();
        if (!AllowedCaptureTypes.Contains(captureType))
        {
            return ServiceResult<CaptureWriteResult>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                "captureType must be one of: memory, idea, trip_log, reminder, question, unclassified.");
        }

        // The snapshot is stored verbatim and handed to every reader (v1.5),
        // so it is range-checked here with the route-context rule; the
        // optional accuracy/heading/speed fields are passed through as the
        // device reported them. Coordinates are never logged (§14.5).
        if (request.LocationContext is { } location)
        {
            if (location.Latitude is < -90 or > 90 || double.IsNaN(location.Latitude))
            {
                return ServiceResult<CaptureWriteResult>.Fail(
                    StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                    "locationContext.latitude must be between -90 and 90.");
            }

            if (location.Longitude is < -180 or > 180 || double.IsNaN(location.Longitude))
            {
                return ServiceResult<CaptureWriteResult>.Fail(
                    StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                    "locationContext.longitude must be between -180 and 180.");
            }
        }

        var captureId = captureGuid.ToString("D");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Captures.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CaptureId == captureId, cancellationToken);
        if (existing is not null)
        {
            if (existing.OwnerId != caller.OwnerId)
            {
                return ServiceResult<CaptureWriteResult>.Fail(
                    StatusCodes.Status409Conflict, SyncApiErrorCodes.CaptureConflict,
                    "A capture with this ID already exists for a different owner.");
            }

            var existingArtifacts = await db.Artifacts.AsNoTracking()
                .Where(a => a.CaptureId == captureId)
                .ToListAsync(cancellationToken);
            _logger.LogInformation("Capture {CaptureId} create replayed idempotently.", captureId);
            return ServiceResult<CaptureWriteResult>.Ok(
                new CaptureWriteResult(EntityMappers.ToCaptureResponse(existing, existingArtifacts), Created: false));
        }

        var capture = new ServerCapture
        {
            CaptureId = captureId,
            OwnerId = caller.OwnerId,
            SourceDeviceId = caller.DeviceId,
            SourcePlatform = caller.Platform,
            CaptureType = captureType,
            CapturedAtUtc = request.CapturedAtUtc,
            TimezoneId = request.TimezoneId,
            LocalOffsetMinutes = request.LocalOffsetMinutes,
            TripId = request.TripId,
            TitleHint = request.TitleHint,
            UserNote = request.UserNote,
            LocationJson = request.LocationContext is null
                ? null
                : JsonSerializer.Serialize(request.LocationContext, SyncJson.Options),
            ClientSchemaVersion = request.ClientSchemaVersion,
            Status = SyncCaptureStatus.Received,
            Revision = 1,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Captures.Add(capture);
        db.SyncChanges.Add(EntityMappers.CreateCaptureChange(capture, audioArtifact: null, caller.DeviceId));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a concurrent create of the same captureId: replay the winner.
            await using var retryDb = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var winner = await retryDb.Captures.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CaptureId == captureId && c.OwnerId == caller.OwnerId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            var winnerArtifacts = await retryDb.Artifacts.AsNoTracking()
                .Where(a => a.CaptureId == captureId)
                .ToListAsync(cancellationToken);
            return ServiceResult<CaptureWriteResult>.Ok(
                new CaptureWriteResult(EntityMappers.ToCaptureResponse(winner, winnerArtifacts), Created: false));
        }

        _logger.LogInformation(
            "Capture {CaptureId} created by device {DeviceId} (type {CaptureType}).",
            captureId, caller.DeviceId, captureType);
        return ServiceResult<CaptureWriteResult>.Ok(
            new CaptureWriteResult(EntityMappers.ToCaptureResponse(capture, []), Created: true));
    }

    public async Task<ServiceResult<CaptureResponse>> GetAsync(
        Device caller,
        string captureId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (capture, error) = await LoadCaptureAsync(db, caller, captureId, track: false, cancellationToken);
        if (capture is null)
        {
            return ServiceResult<CaptureResponse>.Fail(error!);
        }

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.CaptureId == capture.CaptureId)
            .ToListAsync(cancellationToken);
        return ServiceResult<CaptureResponse>.Ok(EntityMappers.ToCaptureResponse(capture, artifacts));
    }

    public async Task<ServiceResult<CaptureResponse>> UpdateAsync(
        Device caller,
        string captureId,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (capture, error) = await LoadCaptureAsync(db, caller, captureId, track: true, cancellationToken);
        if (capture is null)
        {
            return ServiceResult<CaptureResponse>.Fail(error!);
        }

        var changed = false;

        if (body.TryGetPropertyValue("captureType", out var captureTypeNode))
        {
            if (!TryGetString(captureTypeNode, out var rawCaptureType) || rawCaptureType is null)
            {
                return ValidationError("captureType must be a string.");
            }

            var captureType = rawCaptureType.Trim().ToLowerInvariant();
            if (!AllowedCaptureTypes.Contains(captureType))
            {
                return ValidationError(
                    "captureType must be one of: memory, idea, trip_log, reminder, question, unclassified.");
            }

            changed |= capture.CaptureType != captureType;
            capture.CaptureType = captureType;
        }

        if (body.TryGetPropertyValue("titleHint", out var titleHintNode))
        {
            if (!TryGetString(titleHintNode, out var titleHint))
            {
                return ValidationError("titleHint must be a string or null.");
            }

            changed |= capture.TitleHint != titleHint;
            capture.TitleHint = titleHint;
        }

        if (body.TryGetPropertyValue("userNote", out var userNoteNode))
        {
            if (!TryGetString(userNoteNode, out var userNote))
            {
                return ValidationError("userNote must be a string or null.");
            }

            changed |= capture.UserNote != userNote;
            capture.UserNote = userNote;
        }

        if (body.TryGetPropertyValue("tripId", out var tripIdNode))
        {
            if (!TryGetString(tripIdNode, out var tripId) || (tripId is not null && !Guid.TryParse(tripId, out _)))
            {
                return ValidationError("tripId must be a UUID or null.");
            }

            changed |= capture.TripId != tripId;
            capture.TripId = tripId;
        }

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.CaptureId == capture.CaptureId)
            .ToListAsync(cancellationToken);

        if (changed)
        {
            capture.Revision++;
            var completedAudio = artifacts.FirstOrDefault(
                a => a.State == ArtifactState.Complete && EntityMappers.IsAudioArtifact(a));
            db.SyncChanges.Add(EntityMappers.CreateCaptureChange(
                capture,
                completedAudio is null ? null : EntityMappers.ToSummary(completedAudio),
                caller.DeviceId));
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Capture {CaptureId} updated by device {DeviceId}; revision {Revision}.",
                capture.CaptureId, caller.DeviceId, capture.Revision);
        }

        return ServiceResult<CaptureResponse>.Ok(EntityMappers.ToCaptureResponse(capture, artifacts));
    }

    public async Task<ServiceResult<CaptureResponse>> CompleteAsync(
        Device caller,
        string captureId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (capture, error) = await LoadCaptureAsync(db, caller, captureId, track: true, cancellationToken);
        if (capture is null)
        {
            return ServiceResult<CaptureResponse>.Fail(error!);
        }

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.CaptureId == capture.CaptureId)
            .ToListAsync(cancellationToken);

        // "Fully uploaded" needs something to have been uploaded: a capture
        // with no declared artifact has no content for Windows to process.
        if (artifacts.Count == 0)
        {
            return ServiceResult<CaptureResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.CaptureArtifactsIncomplete,
                "No artifact has been declared for this capture; initiate and upload one before completing it.",
                retryable: true);
        }

        // Purged counts as done: the upload finished long ago and retention
        // took the bytes, which must not make a replayed complete start failing.
        var incomplete = artifacts.FirstOrDefault(
            a => a.State != ArtifactState.Complete && a.State != ArtifactState.Purged);
        if (incomplete is not null)
        {
            return ServiceResult<CaptureResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.CaptureArtifactsIncomplete,
                $"Artifact {incomplete.ArtifactId} is {incomplete.State}; every declared artifact must be complete.",
                retryable: true);
        }

        // Only a capture still in a client-authored pre-upload status moves:
        // anything at or past received was already handed to Windows (artifact
        // completion appends the ingestion-triggering change), and republishing
        // it would be a second change saying the same thing.
        if (IsPreUploadStatus(capture.Status))
        {
            capture.Status = SyncCaptureStatus.Received;
            capture.Revision++;
            db.SyncChanges.Add(EntityMappers.CreateCaptureChange(
                capture, CompletedAudioSummary(artifacts), caller.DeviceId));
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Capture {CaptureId} completed by device {DeviceId}; revision {Revision}.",
                capture.CaptureId, caller.DeviceId, capture.Revision);
        }

        await TouchDeviceAsync(db, caller, cancellationToken);
        return ServiceResult<CaptureResponse>.Ok(EntityMappers.ToCaptureResponse(capture, artifacts));
    }

    public async Task<ServiceResult<CaptureResponse>> RetryAsync(
        Device caller,
        string captureId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (capture, error) = await LoadCaptureAsync(db, caller, captureId, track: true, cancellationToken);
        if (capture is null)
        {
            return ServiceResult<CaptureResponse>.Fail(error!);
        }

        if (!string.Equals(capture.Status, SyncCaptureStatus.Failed, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<CaptureResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.CaptureNotFailed,
                $"The capture is {capture.Status}; only a failed capture can be retried.");
        }

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.CaptureId == capture.CaptureId)
            .ToListAsync(cancellationToken);

        // A retry re-publishes the audio for Windows to download again; audio
        // the retention sweep has already purged cannot be re-processed from
        // here, and saying so beats re-queueing a capture nothing can fetch.
        var audio = CompletedAudioSummary(artifacts);
        if (audio is null && artifacts.Any(a => a.State == ArtifactState.Purged && EntityMappers.IsAudioArtifact(a)))
        {
            return ServiceResult<CaptureResponse>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.ArtifactPurged,
                "The capture's audio was purged by the retention sweep; upload it again before retrying.");
        }

        capture.Status = SyncCaptureStatus.Received;
        capture.Revision++;
        db.SyncChanges.Add(EntityMappers.CreateCaptureChange(capture, audio, caller.DeviceId));
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Capture {CaptureId} re-queued by device {DeviceId}; revision {Revision}.",
            capture.CaptureId, caller.DeviceId, capture.Revision);

        await TouchDeviceAsync(db, caller, cancellationToken);
        return ServiceResult<CaptureResponse>.Ok(EntityMappers.ToCaptureResponse(capture, artifacts));
    }

    /// <summary>Statuses a client may author before its upload is done (SyncCaptureStatus vocabulary).</summary>
    private static bool IsPreUploadStatus(string status)
        => string.Equals(status, SyncCaptureStatus.LocalOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SyncCaptureStatus.Uploading, StringComparison.OrdinalIgnoreCase);

    /// <summary>The complete audio artifact's summary — what a capture change carries to trigger ingestion — or null.</summary>
    private static ArtifactSummary? CompletedAudioSummary(IEnumerable<ServerArtifact> artifacts)
    {
        var audio = artifacts.FirstOrDefault(
            a => a.State == ArtifactState.Complete && EntityMappers.IsAudioArtifact(a));
        return audio is null ? null : EntityMappers.ToSummary(audio);
    }

    /// <summary>
    /// Last-seen bookkeeping for the calling device. Saved after the capture
    /// write and through the concurrency helper, because the device row's
    /// refresh hash is a concurrency token and a racing token refresh must not
    /// fail a capture operation that already succeeded.
    /// </summary>
    private static Task TouchDeviceAsync(SyncDbContext db, Device caller, CancellationToken cancellationToken)
        => DeviceRowConcurrency.SaveAsync(db, caller.DeviceId, d => d.LastSeenAtUtc = DateTime.UtcNow, cancellationToken);

    /// <summary>Reads a JSON node expected to be a string or null; false when it is another type.</summary>
    private static bool TryGetString(JsonNode? node, out string? value)
    {
        if (node is null)
        {
            value = null;
            return true;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    private static ServiceResult<CaptureResponse> ValidationError(string message)
        => ServiceResult<CaptureResponse>.Fail(
            StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError, message);

    private static async Task<(ServerCapture? Capture, ServiceError? Error)> LoadCaptureAsync(
        SyncDbContext db,
        Device caller,
        string captureId,
        bool track,
        CancellationToken cancellationToken)
    {
        var notFound = new ServiceError(
            StatusCodes.Status404NotFound, SyncApiErrorCodes.CaptureNotFound, "No such capture.");

        if (!Guid.TryParse(captureId, out var captureGuid))
        {
            return (null, notFound);
        }

        var normalizedId = captureGuid.ToString("D");
        var query = track ? db.Captures : db.Captures.AsNoTracking();
        var capture = await query.FirstOrDefaultAsync(
            c => c.CaptureId == normalizedId && c.OwnerId == caller.OwnerId, cancellationToken);
        return capture is null ? (null, notFound) : (capture, null);
    }
}
