using System.Text.Json;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="ISyncChangeService"/> over the sync database. Stateless over the
/// context factory; registered as a singleton. Logs counts and identifiers
/// only — change payload contents are never logged (design §14.5).
/// </summary>
public sealed class SyncChangeService : ISyncChangeService
{
    private const int DefaultPullLimit = 100;
    private const int MaxPullLimit = 500;

    /// <summary>Device platform (as canonicalized by DeviceService at registration) that owns the archive.</summary>
    private const string WindowsPlatform = "windows";

    private static readonly string[] AllowedEntityTypes =
    [
        SyncChangeEntityType.Capture,
        SyncChangeEntityType.CaptureArtifact,
        SyncChangeEntityType.CaptureStatus,
        SyncChangeEntityType.RecordingQueue,
        SyncChangeEntityType.PendingEvent,
        SyncChangeEntityType.Event,
        SyncChangeEntityType.Era,
        SyncChangeEntityType.Person,
        SyncChangeEntityType.PendingEventDecision,
        SyncChangeEntityType.AssistantTurn,
        SyncChangeEntityType.AssistantTurnChunk,
    ];

    /// <summary>
    /// Read-only projections of the Windows archive (TimelineProjectionContracts).
    /// Grouped because they share one authority rule: the owner's Windows machine
    /// publishes them and every other device consumes them. Nothing else may
    /// author one — a companion that could push an <c>event</c> could write a
    /// memory into someone's timeline that never happened, and because these
    /// carry no revision history to reconcile, the forgery would simply become
    /// the timeline.
    /// </summary>
    private static readonly string[] WindowsAuthoredProjections =
    [
        SyncChangeEntityType.Event,
        SyncChangeEntityType.Era,
        SyncChangeEntityType.Person,
        SyncChangeEntityType.PendingEvent,
    ];

    /// <summary>Verdicts a pending_event_decision may carry (PendingEventDecision vocabulary).</summary>
    private static readonly string[] AllowedPendingEventDecisions =
    [
        PendingEventDecision.Approve,
        PendingEventDecision.Reject,
    ];

    /// <summary>
    /// Date precisions a projected event may declare (EventProjectionPayload
    /// and PendingEventProjectionPayload). Refused rather than defaulted when
    /// unknown: precision is carried on the wire instead of derived precisely
    /// so a consumer never invents an exact day for a memory the user dated to
    /// a summer, and a value nobody understands is how that happens anyway.
    /// </summary>
    private static readonly string[] AllowedDatePrecisions =
    [
        "exact",
        "day",
        "month",
        "season",
        "year",
        "decade",
        "unknown",
    ];

    /// <summary>Grounding values an assistant answer may declare.</summary>
    private static readonly string[] AllowedGroundings =
    [
        AssistantGrounding.Archive,
        AssistantGrounding.ClientContext,
        AssistantGrounding.None,
    ];

    /// <summary>Responder values an answer may name as the one that produced it.</summary>
    private static readonly string[] AllowedResponders =
    [
        AssistantResponder.Windows,
        AssistantResponder.Provider,
        AssistantResponder.OnDevice,
    ];

    /// <summary>Statuses a capture_status payload may carry (SyncCaptureStatus vocabulary).</summary>
    private static readonly string[] AllowedCaptureStatuses =
    [
        SyncCaptureStatus.LocalOnly,
        SyncCaptureStatus.Uploading,
        SyncCaptureStatus.Received,
        SyncCaptureStatus.Processing,
        SyncCaptureStatus.ReviewReady,
        SyncCaptureStatus.Completed,
        SyncCaptureStatus.Failed,
    ];

    /// <summary>
    /// Lifecycle order of the capture statuses, used by the capture_status
    /// ordering guard. <c>failed</c> is deliberately absent: it is off the
    /// ladder in both directions (see <see cref="IsBehindStored"/>).
    /// </summary>
    private static readonly string[] CaptureStatusLadder =
    [
        SyncCaptureStatus.LocalOnly,
        SyncCaptureStatus.Uploading,
        SyncCaptureStatus.Received,
        SyncCaptureStatus.Processing,
        SyncCaptureStatus.ReviewReady,
        SyncCaptureStatus.Completed,
    ];

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly ILogger<SyncChangeService> _logger;

    public SyncChangeService(IDbContextFactory<SyncDbContext> contextFactory, ILogger<SyncChangeService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<ServiceResult<SyncPullResponse>> PullAsync(
        Device caller,
        long cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit <= 0 ? DefaultPullLimit : Math.Clamp(limit, 1, MaxPullLimit);

        // The change log is owner-scoped and fans out to every device of that
        // owner except the one that authored the row. A capture_status change
        // therefore reaches the capture's originating phone: its publisher is
        // Windows, so echo suppression (which keys on the *publishing* device,
        // never the capture's source device) cannot swallow it.
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SyncChanges.AsNoTracking()
            .Where(c => c.OwnerId == caller.OwnerId
                && c.ChangeId > cursor
                && (c.SourceDeviceId == null || c.SourceDeviceId != caller.DeviceId))
            .OrderBy(c => c.ChangeId)
            .Take(effectiveLimit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > effectiveLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var response = new SyncPullResponse
        {
            Changes = rows.Select(ToDto).ToList(),
            NextCursor = rows.Count > 0 ? rows[^1].ChangeId : cursor,
            HasMore = hasMore,
        };

        _logger.LogDebug(
            "Device {DeviceId} pulled {ChangeCount} changes after cursor {Cursor}.",
            caller.DeviceId, rows.Count, cursor);
        return ServiceResult<SyncPullResponse>.Ok(response);
    }

    public async Task<ServiceResult<SyncPushResponse>> PushAsync(
        Device caller,
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        var response = new SyncPushResponse();
        var entries = request.Entries ?? [];
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        foreach (var entry in entries)
        {
            try
            {
                response.Results.Add(await ApplyEntryAsync(db, caller, entry, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad entry must not fail the batch (design §12.1); surface
                // it per-entry, and drop its tracked rows so they cannot leak
                // into the next entry's save on the shared context.
                db.ChangeTracker.Clear();
                _logger.LogError(
                    ex,
                    "Failed to apply pushed change (device {DeviceId}, sequence {ClientSequence}, entity type {EntityType}).",
                    caller.DeviceId, entry.ClientSequence, entry.EntityType);
                response.Results.Add(new SyncPushEntryResult
                {
                    ClientSequence = entry.ClientSequence,
                    Accepted = false,
                    Error = SyncApiErrorCodes.InternalError,
                });
            }
        }

        _logger.LogInformation(
            "Device {DeviceId} pushed {EntryCount} entries ({AcceptedCount} accepted).",
            caller.DeviceId, entries.Count, response.Results.Count(r => r.Accepted));
        return ServiceResult<SyncPushResponse>.Ok(response);
    }

    public async Task<ServiceResult<bool>> AckAsync(
        Device caller,
        SyncAckRequest request,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // The device row's refresh hash is a concurrency token, so an ack that
        // races a token refresh would otherwise fail the save; the helper
        // reloads and re-applies the cursor on top of the rotated row.
        var device = await DeviceRowConcurrency.SaveAsync(
            db,
            caller.DeviceId,
            d =>
            {
                d.AckedCursor = request.Cursor;
                d.LastSeenAtUtc = DateTime.UtcNow;
            },
            cancellationToken);
        if (device is null)
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status401Unauthorized, SyncApiErrorCodes.DeviceUnknown, "No such device.");
        }

        return ServiceResult<bool>.Ok(true);
    }

    private async Task<SyncPushEntryResult> ApplyEntryAsync(
        SyncDbContext db,
        Device caller,
        SyncPushEntry entry,
        CancellationToken cancellationToken)
    {
        var receipt = await db.PushReceipts.FindAsync(
            [caller.DeviceId, entry.ClientSequence], cancellationToken);
        if (receipt is not null)
        {
            return new SyncPushEntryResult
            {
                ClientSequence = entry.ClientSequence,
                Accepted = true,
                Duplicate = true,
                ServerChangeId = receipt.ServerChangeId,
            };
        }

        if (string.IsNullOrWhiteSpace(entry.EntityId) || !AllowedEntityTypes.Contains(entry.EntityType))
        {
            return Rejected(entry, "entityType or entityId is not valid.");
        }

        if (entry.Operation is not (SyncOperation.Upsert or SyncOperation.Delete))
        {
            return Rejected(entry, "operation must be upsert or delete.");
        }

        long revision = 1;
        var payloadJson = entry.PayloadJson;
        if (entry.EntityType == SyncChangeEntityType.Capture)
        {
            var payload = TryParseCapturePayload(entry.PayloadJson);
            if (payload is not null)
            {
                var capture = await db.Captures.FirstOrDefaultAsync(
                    c => c.CaptureId == entry.EntityId && c.OwnerId == caller.OwnerId, cancellationToken);
                if (capture is not null && !string.IsNullOrWhiteSpace(payload.Status))
                {
                    capture.Status = payload.Status;
                    capture.Revision++;
                    revision = capture.Revision;
                }
            }
        }
        else if (entry.EntityType == SyncChangeEntityType.CaptureStatus)
        {
            var outcome = await TryApplyCaptureStatusAsync(db, caller, entry, cancellationToken);
            if (outcome.Rejection is not null)
            {
                return outcome.Rejection;
            }

            if (outcome.Ignored)
            {
                // Behind what the projection already holds: consume the entry
                // (so the publisher marks it delivered instead of re-pushing it
                // forever) but append no change row and mutate nothing, leaving
                // the newer state the phone has already seen intact.
                _logger.LogDebug(
                    "Capture {CaptureId} status push from device {DeviceId} is behind the stored status; ignored.",
                    entry.EntityId, caller.DeviceId);
                return new SyncPushEntryResult
                {
                    ClientSequence = entry.ClientSequence,
                    Accepted = true,
                    Duplicate = true,
                };
            }

            var status = outcome.Applied!;
            revision = status.Revision;
            payloadJson = EntityMappers.SerializeCaptureStatusPayload(status);
            _logger.LogDebug(
                "Capture {CaptureId} status {Status} (revision {Revision}) recorded from device {DeviceId}.",
                status.CaptureId, status.Status, status.Revision, caller.DeviceId);
        }
        else if (entry.EntityType == SyncChangeEntityType.AssistantTurn)
        {
            var outcome = await TryApplyAssistantTurnAsync(db, caller, entry, cancellationToken);
            if (outcome.Rejection is not null)
            {
                return outcome.Rejection;
            }

            if (outcome.Ignored)
            {
                // The turn already finished — most importantly, the user may
                // have cancelled it. Consume the entry so the responder stops
                // re-pushing, but append nothing: a cancelled turn that could
                // still be answered by work already in flight would make the
                // cancel button a suggestion rather than a stop.
                _logger.LogInformation(
                    "Assistant turn {TurnId} answer from device {DeviceId} arrived after the turn finished; discarded.",
                    entry.EntityId, caller.DeviceId);
                return new SyncPushEntryResult
                {
                    ClientSequence = entry.ClientSequence,
                    Accepted = true,
                    Duplicate = true,
                };
            }

            var turn = outcome.Applied!;
            revision = turn.Revision;
            payloadJson = EntityMappers.SerializeAssistantTurnPayload(turn);
            _logger.LogInformation(
                "Assistant turn {TurnId} moved to {Status} by device {DeviceId} " +
                "(responder {Responder}, revision {Revision}).",
                turn.TurnId, turn.Status, caller.DeviceId, turn.ActualResponder ?? "unclaimed", turn.Revision);
        }
        else if (entry.EntityType == SyncChangeEntityType.AssistantTurnChunk)
        {
            var outcome = await TryRelayAssistantChunkAsync(db, caller, entry, cancellationToken);
            if (outcome.Rejection is not null)
            {
                return outcome.Rejection;
            }

            if (outcome.Ignored)
            {
                _logger.LogDebug(
                    "Assistant chunk for turn {TurnId} arrived after the turn finished; dropped.",
                    entry.EntityId);
                return new SyncPushEntryResult
                {
                    ClientSequence = entry.ClientSequence,
                    Accepted = true,
                    Duplicate = true,
                };
            }

            // Chunks have no projection to keep in step, so the publisher's own
            // bytes are relayed unchanged; the revision carries the chunk index
            // so a consumer can spot a gap without parsing the payload.
            revision = outcome.Applied!.ChunkIndex + 1;
        }
        else if (WindowsAuthoredProjections.Contains(entry.EntityType))
        {
            var outcome = TryRelayProjection(caller, entry);
            if (outcome.Rejection is not null)
            {
                return outcome.Rejection;
            }

            // Relayed verbatim, like an assistant chunk and unlike
            // capture_status: the service stores no copy of the archive, so
            // there is no row whose re-serialization could be more
            // authoritative than the publisher's own bytes — and passing them
            // through unchanged is also what lets a newer Windows build add a
            // field to a projection without this service having to learn it
            // first. Revision therefore stays 1: with nothing stored to
            // version, the change ID is the only ordering that means anything,
            // and it already orders these correctly (see the entity-id note on
            // TryRelayProjection).
            _logger.LogDebug(
                "Projection {EntityType} {EntityId} ({Operation}) relayed from device {DeviceId}.",
                entry.EntityType, outcome.Applied, entry.Operation, caller.DeviceId);
        }
        else if (entry.EntityType == SyncChangeEntityType.PendingEventDecision)
        {
            var outcome = TryRelayPendingEventDecision(caller, entry);
            if (outcome.Rejection is not null)
            {
                return outcome.Rejection;
            }

            // Also relayed verbatim. The decision is a message to Windows, not
            // state this service maintains: Windows performs the approval (one
            // transaction over the event, its tags, people and locations) and
            // drops verdicts for reviews it has already resolved, so a copy
            // held here could only ever be a second opinion that disagrees.
            // The verdict is a vocabulary value, like a capture status, so it is
            // logged; the corrections that may ride with it are not (§14.5).
            _logger.LogInformation(
                "Pending event {PendingEventId} decided {Decision} by device {DeviceId}.",
                entry.EntityId, outcome.Applied!.Decision, caller.DeviceId);
        }

        var change = new SyncChangeRow
        {
            OwnerId = caller.OwnerId,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            Operation = entry.Operation,
            Revision = revision,
            SourceDeviceId = caller.DeviceId,
            PayloadJson = payloadJson,
            ChangedAtUtc = entry.ChangedAtUtc == default ? DateTime.UtcNow : entry.ChangedAtUtc,
        };
        db.SyncChanges.Add(change);

        // ServerChangeId carries the change row's temporary key (readable only
        // through the change tracker before save); the FK configured in
        // SyncDbContext replaces it with the store-generated change ID at save
        // time. One SaveChanges commits the change row, the receipt, and any
        // capture update atomically, so a crashed request that the client
        // retries can never leave a receipt-less duplicate change row.
        db.PushReceipts.Add(new PushReceipt
        {
            DeviceId = caller.DeviceId,
            ClientSequence = entry.ClientSequence,
            ServerChangeId = db.Entry(change).Property(c => c.ChangeId).CurrentValue,
            ReceivedAtUtc = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Concurrent push of the same sequence from the same device: the
            // whole save rolled back (no orphan change row); report the
            // winning receipt.
            _logger.LogWarning(
                ex,
                "Concurrent push receipt for device {DeviceId} sequence {ClientSequence}.",
                caller.DeviceId, entry.ClientSequence);
            db.ChangeTracker.Clear();
            var winner = await db.PushReceipts.FindAsync(
                [caller.DeviceId, entry.ClientSequence], cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return new SyncPushEntryResult
            {
                ClientSequence = entry.ClientSequence,
                Accepted = true,
                Duplicate = true,
                ServerChangeId = winner.ServerChangeId,
            };
        }

        return new SyncPushEntryResult
        {
            ClientSequence = entry.ClientSequence,
            Accepted = true,
            Duplicate = false,
            ServerChangeId = change.ChangeId,
        };
    }

    /// <summary>
    /// Outcome of one pushed entry that the service does more with than log:
    /// <see cref="Applied"/> is what the entry produced (a tracked, still
    /// unsaved projection row, or the validated payload for entity types that
    /// keep no projection), <see cref="Rejection"/> the per-entry error to hand
    /// back to the publisher, and <see cref="Ignored"/> marks an entry that
    /// must be consumed without changing anything — because it is behind what
    /// is stored, or because the thing it targets has already finished.
    /// Exactly one of the three is set.
    /// </summary>
    private readonly record struct PushEntryOutcome<T>(
        T? Applied,
        SyncPushEntryResult? Rejection,
        bool Ignored)
        where T : class
    {
        public static PushEntryOutcome<T> Apply(T applied) => new(applied, null, false);

        public static PushEntryOutcome<T> Reject(SyncPushEntryResult rejection) => new(null, rejection, false);

        public static PushEntryOutcome<T> Ignore() => new(null, null, true);
    }

    /// <summary>
    /// Validates a pushed capture_status entry and applies it to the capture's
    /// status projection, returning the tracked (still unsaved) row so the
    /// caller commits it together with the change row and the push receipt.
    /// On failure returns the per-entry rejection to hand back to the
    /// publisher, and on a stale entry an "ignore" outcome (see
    /// <see cref="IsBehindStored"/>). Windows — not the service — owns
    /// truncation of the transcript preview, so an over-long preview is
    /// rejected rather than cut down (design §19 Phase 3).
    /// </summary>
    private static async Task<PushEntryOutcome<CaptureStatusRow>> TryApplyCaptureStatusAsync(
        SyncDbContext db,
        Device caller,
        SyncPushEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Operation != SyncOperation.Upsert)
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(Rejected(entry, "capture_status supports upsert only."));
        }

        var payload = TryParseCaptureStatusPayload(entry.PayloadJson);
        if (payload is null)
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(Rejected(entry, "capture_status requires a well-formed payload."));
        }

        if (!Guid.TryParse(entry.EntityId, out var entityGuid)
            || !Guid.TryParse(payload.CaptureId, out var payloadGuid)
            || entityGuid != payloadGuid)
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(
                Rejected(entry, "payload captureId must be a UUID equal to entityId."));
        }

        var status = string.IsNullOrWhiteSpace(payload.Status)
            ? string.Empty
            : payload.Status.Trim().ToLowerInvariant();
        if (!AllowedCaptureStatuses.Contains(status))
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(Rejected(
                entry,
                "status must be one of: local_only, uploading, received, processing, review_ready, completed, failed."));
        }

        if (payload.TranscriptPreview is { Length: > CaptureStatusChangePayload.TranscriptPreviewMaxChars })
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(Rejected(
                entry,
                $"transcriptPreview must be at most {CaptureStatusChangePayload.TranscriptPreviewMaxChars} characters."));
        }

        if (payload.TranscriptCharCount < 0 || payload.PendingEventCount < 0 || payload.ApprovedEventCount < 0)
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(Rejected(
                entry, "transcriptCharCount, pendingEventCount and approvedEventCount must not be negative."));
        }

        var captureId = entityGuid.ToString("D");
        var capture = await db.Captures.FirstOrDefaultAsync(
            c => c.CaptureId == captureId && c.OwnerId == caller.OwnerId, cancellationToken);
        if (capture is null)
        {
            return PushEntryOutcome<CaptureStatusRow>.Reject(Rejected(entry, SyncApiErrorCodes.CaptureNotFound, "No such capture."));
        }

        var updatedAtUtc = payload.UpdatedAtUtc == default ? DateTime.UtcNow : payload.UpdatedAtUtc;
        var row = await db.CaptureStatuses.FirstOrDefaultAsync(
            s => s.CaptureId == captureId, cancellationToken);
        if (row is not null && IsBehindStored(row, payload, status, updatedAtUtc))
        {
            return PushEntryOutcome<CaptureStatusRow>.Ignore();
        }

        if (row is null)
        {
            row = new CaptureStatusRow { CaptureId = captureId, OwnerId = capture.OwnerId };
            db.CaptureStatuses.Add(row);
        }

        // Latest wins from here on: the projection holds the current state only,
        // and the change log preserves the sequence that produced it.
        row.Status = status;
        row.ProcessingStage = payload.ProcessingStage;
        row.UpdatedAtUtc = updatedAtUtc;
        row.TranscriptAvailable = payload.TranscriptAvailable;
        row.TranscriptPreview = payload.TranscriptPreview;
        row.TranscriptCharCount = payload.TranscriptCharCount;
        row.PendingEventCount = payload.PendingEventCount;
        row.ApprovedEventCount = payload.ApprovedEventCount;
        row.FailureReason = payload.FailureReason;
        row.FailureRetryable = payload.FailureRetryable;
        row.Revision++;
        row.ReceivedAtUtc = DateTime.UtcNow;

        // Keep the capture read model (CaptureResponse.Status) in step with the
        // coarse lifecycle state; a stage-only advance leaves the capture row
        // and its revision alone.
        if (capture.Status != status)
        {
            capture.Status = status;
            capture.Revision++;
        }

        return PushEntryOutcome<CaptureStatusRow>.Apply(row);
    }

    /// <summary>
    /// Ordering guard: true when the pushed payload says less than the
    /// projection already holds, so applying it would walk the capture
    /// backwards on the phone.
    ///
    /// The Windows outbox is drained in order, but only accepted and duplicate
    /// entries are marked delivered — a transient per-entry rejection (a locked
    /// database, say) leaves entry N pending while N+1..M land, and the next
    /// drain re-pushes N under a client sequence that has no receipt yet. Pure
    /// latest-wins would then regress a capture that already reached
    /// <c>completed</c> back to <c>processing</c>.
    ///
    /// <c>failed</c> sits off the ladder in both directions: an incoming
    /// failure must always be recordable, and once one is stored the ladder
    /// cannot order anything against it — so the publisher clock decides
    /// instead, which still lets a real Windows-side retry move the capture on.
    /// </summary>
    private static bool IsBehindStored(
        CaptureStatusRow stored,
        CaptureStatusChangePayload payload,
        string status,
        DateTime updatedAtUtc)
    {
        if (status == SyncCaptureStatus.Failed)
        {
            return false;
        }

        if (stored.Status == SyncCaptureStatus.Failed)
        {
            return updatedAtUtc < stored.UpdatedAtUtc;
        }

        // An unknown stored status (-1) ranks below everything, so a status this
        // build does understand always wins over it.
        var incomingRank = Array.IndexOf(CaptureStatusLadder, status);
        var storedRank = Array.IndexOf(CaptureStatusLadder, stored.Status);
        if (incomingRank != storedRank)
        {
            return incomingRank < storedRank;
        }

        if (updatedAtUtc != stored.UpdatedAtUtc)
        {
            return updatedAtUtc < stored.UpdatedAtUtc;
        }

        // Same rung at the same publisher instant: only a payload that would
        // leave the projection identical is a pure replay. A stage or a count
        // that moved (extraction finishing, an event approved) is new
        // information at an unchanged status and must still reach the phone.
        return LeavesProjectionUnchanged(stored, payload);
    }

    /// <summary>
    /// True when applying <paramref name="payload"/> would leave the stored
    /// projection byte-identical, so the change row it produced would repeat
    /// what the phone already pulled. Status and UpdatedAtUtc are compared by
    /// the caller.
    /// </summary>
    private static bool LeavesProjectionUnchanged(CaptureStatusRow stored, CaptureStatusChangePayload payload) =>
        stored.ProcessingStage == payload.ProcessingStage &&
        stored.TranscriptAvailable == payload.TranscriptAvailable &&
        stored.TranscriptPreview == payload.TranscriptPreview &&
        stored.TranscriptCharCount == payload.TranscriptCharCount &&
        stored.PendingEventCount == payload.PendingEventCount &&
        stored.ApprovedEventCount == payload.ApprovedEventCount &&
        stored.FailureReason == payload.FailureReason &&
        stored.FailureRetryable == payload.FailureRetryable;

    /// <summary>
    /// Validates a pushed assistant_turn entry and applies it to the stored
    /// turn, returning the tracked (still unsaved) row so the caller commits it
    /// together with the change row and the push receipt.
    ///
    /// This is the return leg of the Phase 4 round trip: the service published
    /// the question toward Windows, Windows answered it, and the answer comes
    /// back through the same push path everything else uses. Applying it here
    /// rather than only appending a change row is what lets the asking client
    /// poll <c>GET /assistant/turns/{turnId}</c> — a phone that was suspended
    /// through the whole exchange gets the answer from the turn, not from
    /// change rows it has to have been awake to receive.
    ///
    /// A turn that has already finished is left alone (an "ignore" outcome).
    /// That guard is what makes cancellation mean something: work already in
    /// flight when the user hit stop lands here and is discarded rather than
    /// resurrecting the turn.
    /// </summary>
    private static async Task<PushEntryOutcome<AssistantTurnRow>> TryApplyAssistantTurnAsync(
        SyncDbContext db,
        Device caller,
        SyncPushEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Operation != SyncOperation.Upsert)
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(
                Rejected(entry, "assistant_turn supports upsert only."));
        }

        var payload = TryParsePayload<AssistantTurnChangePayload>(entry.PayloadJson);
        if (payload is null)
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(
                Rejected(entry, "assistant_turn requires a well-formed payload."));
        }

        if (!Guid.TryParse(entry.EntityId, out var entityGuid)
            || !Guid.TryParse(payload.TurnId, out var payloadGuid)
            || entityGuid != payloadGuid)
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(
                Rejected(entry, "payload turnId must be a UUID equal to entityId."));
        }

        var status = Normalize(payload.Status);
        if (!AssistantTurnLifecycle.AllStatuses.Contains(status))
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(Rejected(
                entry,
                "status must be one of: pending, dispatched, answering, completed, failed, cancelled."));
        }

        string? actualResponder = null;
        if (!string.IsNullOrWhiteSpace(payload.ActualResponder))
        {
            actualResponder = Normalize(payload.ActualResponder);
            if (!AllowedResponders.Contains(actualResponder))
            {
                return PushEntryOutcome<AssistantTurnRow>.Reject(
                    Rejected(entry, "actualResponder must be one of: windows, provider, on_device."));
            }
        }

        var resultError = ValidateAssistantResult(payload.Result, status);
        if (resultError is not null)
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(Rejected(entry, resultError));
        }

        if (payload.FailureReason is { Length: > AssistantLimits.FailureReasonMaxChars })
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(Rejected(
                entry, $"failureReason must be at most {AssistantLimits.FailureReasonMaxChars} characters."));
        }

        // Owner-scoped, not device-scoped: any of the owner's devices may
        // answer a turn (that is how Windows responds to a phone's question),
        // even though only the asking device may read it back.
        var turnId = entityGuid.ToString("D");
        var turn = await db.AssistantTurns.FirstOrDefaultAsync(
            t => t.TurnId == turnId && t.OwnerId == caller.OwnerId, cancellationToken);
        if (turn is null)
        {
            return PushEntryOutcome<AssistantTurnRow>.Reject(
                Rejected(entry, SyncApiErrorCodes.AssistantTurnNotFound, "No such assistant turn."));
        }

        if (AssistantTurnLifecycle.IsTerminal(turn.Status))
        {
            return PushEntryOutcome<AssistantTurnRow>.Ignore();
        }

        // Latest wins from here on. Unlike capture status there is no ladder to
        // order against: a responder owns the turn's progress and reports it in
        // order, and the only transition the service refuses is the one out of
        // a terminal state, handled above.
        turn.Status = status;
        turn.ActualResponder = actualResponder ?? turn.ActualResponder;
        turn.FailureReason = payload.FailureReason;
        turn.FailureRetryable = payload.FailureRetryable;
        turn.UpdatedAtUtc = payload.UpdatedAtUtc == default ? DateTime.UtcNow : payload.UpdatedAtUtc;
        turn.Revision++;

        if (payload.Result is { } result)
        {
            turn.Answer = result.Answer ?? string.Empty;
            turn.Grounding = Normalize(result.Grounding);
            turn.CitationsJson = JsonSerializer.Serialize(result.Citations ?? [], SyncJson.Options);
            turn.Model = result.Model;
            turn.ElapsedMs = result.ElapsedMs;
        }

        if (AssistantTurnLifecycle.IsTerminal(status))
        {
            turn.CompletedAtUtc = DateTime.UtcNow;
        }

        return PushEntryOutcome<AssistantTurnRow>.Apply(turn);
    }

    /// <summary>
    /// Validates the answer carried by a pushed assistant_turn. A completed
    /// turn must actually carry an answer — a client that sees
    /// <c>completed</c> stops polling and speaks the result, so completing
    /// without one would leave it silent with nothing to retry. Over-long text
    /// is rejected rather than trimmed, for the same reason the transcript
    /// preview is: the responder, not the service, decides what to leave out.
    /// Returns the rejection message, or null when the result is acceptable.
    /// </summary>
    private static string? ValidateAssistantResult(AssistantTurnResult? result, string status)
    {
        if (result is null)
        {
            return status == AssistantTurnStatus.Completed
                ? "a completed assistant_turn must carry a result."
                : null;
        }

        // Client JSON can null out the non-nullable Answer property, so read it
        // defensively rather than trusting the DTO's declared type.
        var answer = result.Answer ?? string.Empty;
        if (status == AssistantTurnStatus.Completed && answer.Length == 0)
        {
            return "a completed assistant_turn must carry a non-empty result answer.";
        }

        if (answer.Length > AssistantLimits.AnswerMaxChars)
        {
            return $"result answer must be at most {AssistantLimits.AnswerMaxChars} characters.";
        }

        if (!AllowedGroundings.Contains(Normalize(result.Grounding)))
        {
            return "result grounding must be one of: archive, client_context, none.";
        }

        var citations = result.Citations;
        if (citations is null)
        {
            return null;
        }

        if (citations.Count > AssistantLimits.CitationMaxCount)
        {
            return $"result citations must contain at most {AssistantLimits.CitationMaxCount} entries.";
        }

        foreach (var citation in citations)
        {
            if (citation is null || !Guid.TryParse(citation.EventId, out _))
            {
                return "result citation eventId must be a UUID.";
            }

            if (citation.Excerpt is { Length: > AssistantLimits.CitationExcerptMaxChars })
            {
                return $"result citation excerpt must be at most {AssistantLimits.CitationExcerptMaxChars} characters.";
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a pushed assistant_turn_chunk and decides whether to relay it.
    /// Chunks keep no projection — they are ephemeral fragments of an answer
    /// whose authoritative version arrives as the completed turn — so nothing
    /// is applied and the publisher's payload is forwarded verbatim.
    ///
    /// Chunks for a finished turn are dropped for the same reason a late answer
    /// is: after the user cancels, a responder that has not yet noticed must not
    /// still be able to put words on their screen.
    /// </summary>
    private static async Task<PushEntryOutcome<AssistantTurnChunkPayload>> TryRelayAssistantChunkAsync(
        SyncDbContext db,
        Device caller,
        SyncPushEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Operation != SyncOperation.Upsert)
        {
            return PushEntryOutcome<AssistantTurnChunkPayload>.Reject(
                Rejected(entry, "assistant_turn_chunk supports upsert only."));
        }

        var payload = TryParsePayload<AssistantTurnChunkPayload>(entry.PayloadJson);
        if (payload is null)
        {
            return PushEntryOutcome<AssistantTurnChunkPayload>.Reject(
                Rejected(entry, "assistant_turn_chunk requires a well-formed payload."));
        }

        if (!Guid.TryParse(entry.EntityId, out var entityGuid)
            || !Guid.TryParse(payload.TurnId, out var payloadGuid)
            || entityGuid != payloadGuid)
        {
            return PushEntryOutcome<AssistantTurnChunkPayload>.Reject(
                Rejected(entry, "payload turnId must be a UUID equal to entityId."));
        }

        if (payload.ChunkIndex < 0)
        {
            return PushEntryOutcome<AssistantTurnChunkPayload>.Reject(
                Rejected(entry, "chunkIndex must not be negative."));
        }

        if ((payload.Delta ?? string.Empty).Length > AssistantLimits.ChunkDeltaMaxChars)
        {
            return PushEntryOutcome<AssistantTurnChunkPayload>.Reject(Rejected(
                entry, $"delta must be at most {AssistantLimits.ChunkDeltaMaxChars} characters."));
        }

        var turnId = entityGuid.ToString("D");
        var turn = await db.AssistantTurns.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TurnId == turnId && t.OwnerId == caller.OwnerId, cancellationToken);
        if (turn is null)
        {
            return PushEntryOutcome<AssistantTurnChunkPayload>.Reject(
                Rejected(entry, SyncApiErrorCodes.AssistantTurnNotFound, "No such assistant turn."));
        }

        return AssistantTurnLifecycle.IsTerminal(turn.Status)
            ? PushEntryOutcome<AssistantTurnChunkPayload>.Ignore()
            : PushEntryOutcome<AssistantTurnChunkPayload>.Apply(payload);
    }

    /// <summary>
    /// Validates a pushed timeline projection (<c>event</c>, <c>era</c>,
    /// <c>person</c>, <c>pending_event</c>) and returns the canonical entity ID
    /// it targets. Nothing is stored: the entry is relayed onto the change feed
    /// as published (see the call site for why).
    ///
    /// <para><b>Authority.</b> Only the owner's Windows device may publish one.
    /// This is the check that keeps a companion from writing into a timeline it
    /// merely displays — a phone can hold a valid token (it needs one to upload
    /// captures) and would otherwise be able to push an <c>event</c> that every
    /// other device would render as a memory.</para>
    ///
    /// <para><b>Entity IDs.</b> <c>entityId</c> is the projected row's own key —
    /// eventId, eraId, personId or pendingEventId — and must be a UUID in
    /// canonical lowercase "D" form. Consumers key latest-wins state on that
    /// string, so an upsert of <c>{A1B2...}</c> and a later delete of
    /// <c>a1b2...</c> must not read as two different rows: the deletion would
    /// tombstone nothing and the event would stay on the timeline forever.
    /// Pinning one spelling here is cheaper than asking every consumer to
    /// normalize, and refusing the other spellings is what makes it a pin.</para>
    ///
    /// <para><b>Deletes.</b> Accepted, and deliberately not required to carry a
    /// payload: a deleted event has nothing left to describe, and the tombstone
    /// exists precisely so a companion can drop the row it already rendered.</para>
    /// </summary>
    private static PushEntryOutcome<string> TryRelayProjection(Device caller, SyncPushEntry entry)
    {
        if (!string.Equals(caller.Platform, WindowsPlatform, StringComparison.Ordinal))
        {
            return PushEntryOutcome<string>.Reject(Rejected(
                entry,
                SyncApiErrorCodes.ChangeNotPermitted,
                $"{entry.EntityType} is a read-only projection; only the owner's Windows device may publish it."));
        }

        if (CanonicalEntityId(entry.EntityId) is not { } entityId)
        {
            return PushEntryOutcome<string>.Reject(Rejected(
                entry, "entityId must be a UUID in canonical lowercase \"D\" form."));
        }

        if (entry.Operation == SyncOperation.Delete)
        {
            return PushEntryOutcome<string>.Apply(entityId);
        }

        return entry.EntityType switch
        {
            SyncChangeEntityType.Event => ValidateEventProjection(entry, entityId),
            SyncChangeEntityType.Era => ValidateEraProjection(entry, entityId),
            SyncChangeEntityType.Person => ValidatePersonProjection(entry, entityId),
            _ => ValidatePendingEventProjection(entry, entityId),
        };
    }

    /// <summary>
    /// Validates an <c>event</c> upsert. The date fields get the attention
    /// because they are the ones a consumer draws with: an unknown precision
    /// would let it place a bubble on a day the user never claimed.
    /// </summary>
    private static PushEntryOutcome<string> ValidateEventProjection(SyncPushEntry entry, string entityId)
    {
        var parsed = TryParseProjection<EventProjectionPayload>(entry, entityId, p => p.EventId, "eventId");
        if (parsed.Rejection is not null)
        {
            return PushEntryOutcome<string>.Reject(parsed.Rejection);
        }

        var payload = parsed.Applied!;
        if (!AllowedDatePrecisions.Contains(payload.DatePrecision ?? string.Empty))
        {
            return PushEntryOutcome<string>.Reject(Rejected(entry, DatePrecisionMessage("datePrecision")));
        }

        if (payload.MediaCount < 0)
        {
            return PushEntryOutcome<string>.Reject(Rejected(entry, "mediaCount must not be negative."));
        }

        return PushEntryOutcome<string>.Apply(entityId);
    }

    /// <summary>
    /// Validates an <c>era</c> upsert. The colour is checked because it crosses
    /// as a string for the consumer to turn into a brush, and a value that is
    /// not hex at all ("cornflower") throws in that parser. It is checked
    /// loosely on purpose: the contract says <c>#RRGGBB</c>, but the Windows
    /// column is wide enough for <c>#AARRGGBB</c> and both are unambiguous, so
    /// both pass. Dropping a whole era — the background of a period of
    /// someone's life — over an alpha channel would cost far more than the
    /// stricter check buys. A missing colour is fine too; the consumer falls
    /// back to its own default.
    /// </summary>
    private static PushEntryOutcome<string> ValidateEraProjection(SyncPushEntry entry, string entityId)
    {
        var parsed = TryParseProjection<EraProjectionPayload>(entry, entityId, p => p.EraId, "eraId");
        if (parsed.Rejection is not null)
        {
            return PushEntryOutcome<string>.Reject(parsed.Rejection);
        }

        var colorCode = parsed.Applied!.ColorCode;
        if (!string.IsNullOrWhiteSpace(colorCode) && !IsHexColorCode(colorCode))
        {
            return PushEntryOutcome<string>.Reject(
                Rejected(entry, "colorCode must be a hex colour (\"#RRGGBB\")."));
        }

        return PushEntryOutcome<string>.Apply(entityId);
    }

    /// <summary>
    /// Validates a <c>person</c> upsert. <c>mergedIntoId</c> is checked as an ID
    /// rather than ignored: it is a redirection every consumer is expected to
    /// follow, so an unparseable one would leave a duplicate contact on screen
    /// with no way to reach the person it was merged into.
    /// </summary>
    private static PushEntryOutcome<string> ValidatePersonProjection(SyncPushEntry entry, string entityId)
    {
        var parsed = TryParseProjection<PersonProjectionPayload>(entry, entityId, p => p.PersonId, "personId");
        if (parsed.Rejection is not null)
        {
            return PushEntryOutcome<string>.Reject(parsed.Rejection);
        }

        var payload = parsed.Applied!;
        if (!string.IsNullOrWhiteSpace(payload.MergedIntoId) && !Guid.TryParse(payload.MergedIntoId, out _))
        {
            return PushEntryOutcome<string>.Reject(Rejected(entry, "mergedIntoId must be a UUID when present."));
        }

        if (payload.EventCount < 0)
        {
            return PushEntryOutcome<string>.Reject(Rejected(entry, "eventCount must not be negative."));
        }

        return PushEntryOutcome<string>.Apply(entityId);
    }

    /// <summary>
    /// Validates a <c>pending_event</c> upsert. The transcript preview is held
    /// to the same bound as the capture status preview (§14.5): both are
    /// excerpts of the same recording leaving the machine that holds it, so one
    /// bound governs both, and — as there — an over-long preview is refused
    /// rather than trimmed, because deciding what to leave out is the
    /// publisher's job and quietly cutting it here would hide the bug.
    /// </summary>
    private static PushEntryOutcome<string> ValidatePendingEventProjection(SyncPushEntry entry, string entityId)
    {
        var parsed = TryParseProjection<PendingEventProjectionPayload>(
            entry, entityId, p => p.PendingEventId, "pendingEventId");
        if (parsed.Rejection is not null)
        {
            return PushEntryOutcome<string>.Reject(parsed.Rejection);
        }

        var payload = parsed.Applied!;
        if (!AllowedDatePrecisions.Contains(payload.DatePrecision ?? string.Empty))
        {
            return PushEntryOutcome<string>.Reject(Rejected(entry, DatePrecisionMessage("datePrecision")));
        }

        if (payload.ConfidenceScore is < 0 or > 1)
        {
            return PushEntryOutcome<string>.Reject(Rejected(entry, "confidenceScore must be between 0 and 1."));
        }

        if (payload.TranscriptPreview is { Length: > CaptureStatusChangePayload.TranscriptPreviewMaxChars })
        {
            return PushEntryOutcome<string>.Reject(Rejected(
                entry,
                $"transcriptPreview must be at most {CaptureStatusChangePayload.TranscriptPreviewMaxChars} characters."));
        }

        return PushEntryOutcome<string>.Apply(entityId);
    }

    /// <summary>
    /// Validates a pushed <c>pending_event_decision</c> — the one entity a
    /// companion may author — and returns the payload to relay.
    ///
    /// <para><b>Why this one is safe from any device.</b> It is a verdict, not
    /// an edit: it carries no field values to merge, applying it twice is the
    /// same as applying it once, and two devices sending the same answer agree
    /// by construction. Accepting it therefore does not make the system
    /// multi-writer.</para>
    ///
    /// <para><b>Attribution.</b> <c>decidedByDeviceId</c> must be the calling
    /// device. Windows keeps this as the audit trail of who reviewed what, and
    /// a trail a device can write another device's name into records fiction.
    /// It is also the field that would let a companion make a decision look like
    /// it came from the machine that holds the archive.</para>
    ///
    /// <para><b>Append-only.</b> <c>delete</c> is refused. A verdict is a fact
    /// about a review that happened; there is no meaning to retracting it, and
    /// accepting one would hand a companion a way to reach back into a review
    /// Windows has already completed. Changing one's mind is a new decision,
    /// which is exactly what a second upsert is.</para>
    ///
    /// <para><b>No server-side target check.</b> Unlike capture_status and
    /// assistant_turn there is no stored row to validate against — this service
    /// holds no pending events — so an unknown <c>pendingEventId</c> is relayed
    /// and dropped by Windows, which is the only participant that knows whether
    /// the review is still open.</para>
    /// </summary>
    private static PushEntryOutcome<PendingEventDecisionPayload> TryRelayPendingEventDecision(
        Device caller, SyncPushEntry entry)
    {
        if (entry.Operation != SyncOperation.Upsert)
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry, "pending_event_decision is append-only; it supports upsert only."));
        }

        if (CanonicalEntityId(entry.EntityId) is not { } entityId)
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry, "entityId must be a UUID in canonical lowercase \"D\" form."));
        }

        var payload = TryParsePayload<PendingEventDecisionPayload>(entry.PayloadJson);
        if (payload is null)
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry, "pending_event_decision requires a well-formed payload."));
        }

        if (string.IsNullOrWhiteSpace(payload.PendingEventId))
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry, "pendingEventId is required."));
        }

        // The decision is keyed on the pending event it answers, so Windows can
        // match verdict to review without a second identifier and a consumer can
        // collapse repeats of the same answer.
        if (!Guid.TryParse(payload.PendingEventId, out var pendingEventGuid)
            || pendingEventGuid.ToString("D") != entityId)
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry, "payload pendingEventId must be a UUID equal to entityId."));
        }

        if (!AllowedPendingEventDecisions.Contains(payload.Decision ?? string.Empty))
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry, "decision must be one of: approve, reject."));
        }

        if (!Guid.TryParse(payload.DecidedByDeviceId, out var decidedByGuid)
            || decidedByGuid.ToString("D") != caller.DeviceId)
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(Rejected(
                entry,
                SyncApiErrorCodes.ChangeNotPermitted,
                "decidedByDeviceId must be the device pushing the decision."));
        }

        // Corrections ride the decision because fixing a title while approving
        // is one act, not two — but they are still a companion's write, so the
        // one field that changes how a date is *read* is held to the vocabulary.
        var correctedPrecision = payload.Corrections?.DatePrecision;
        if (!string.IsNullOrWhiteSpace(correctedPrecision) && !AllowedDatePrecisions.Contains(correctedPrecision))
        {
            return PushEntryOutcome<PendingEventDecisionPayload>.Reject(
                Rejected(entry, DatePrecisionMessage("corrections.datePrecision")));
        }

        return PushEntryOutcome<PendingEventDecisionPayload>.Apply(payload);
    }

    /// <summary>
    /// Parses a projection payload and pins its identity to the entry. The
    /// payload's own ID must denote the same UUID as <paramref name="entityId"/>
    /// — the two are the same row's key written twice, and a change whose two
    /// spellings disagree would be applied to one row and looked up under
    /// another. The payload copy is compared as a UUID rather than as text: the
    /// key is the string consumers match on, so only it has to be canonical.
    /// </summary>
    private static PushEntryOutcome<TPayload> TryParseProjection<TPayload>(
        SyncPushEntry entry,
        string entityId,
        Func<TPayload, string?> idSelector,
        string idFieldName)
        where TPayload : class
    {
        var payload = TryParsePayload<TPayload>(entry.PayloadJson);
        if (payload is null)
        {
            return PushEntryOutcome<TPayload>.Reject(
                Rejected(entry, $"{entry.EntityType} requires a well-formed payload."));
        }

        if (!Guid.TryParse(idSelector(payload), out var payloadGuid) || payloadGuid.ToString("D") != entityId)
        {
            return PushEntryOutcome<TPayload>.Reject(
                Rejected(entry, $"payload {idFieldName} must be a UUID equal to entityId."));
        }

        return PushEntryOutcome<TPayload>.Apply(payload);
    }

    /// <summary>
    /// The entity ID in canonical lowercase "D" form, or null when it is not a
    /// UUID or was written some other way (braces, "N" form, upper case). Only
    /// the canonical spelling is accepted so every device keys the same row the
    /// same way; see <see cref="TryRelayProjection"/> for what goes wrong
    /// otherwise.
    /// </summary>
    private static string? CanonicalEntityId(string entityId)
        => Guid.TryParse(entityId, out var parsed) && parsed.ToString("D") == entityId ? entityId : null;

    /// <summary>True for "#RRGGBB" (the contract's form) or "#AARRGGBB" (what the Windows column can hold).</summary>
    private static bool IsHexColorCode(string value)
        => value.Length is 7 or 9 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private static string DatePrecisionMessage(string field)
        => $"{field} must be one of: exact, day, month, season, year, decade, unknown.";

    /// <summary>Trims and lowercases a wire vocabulary value; null and blank become empty, which no vocabulary contains.</summary>
    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static SyncPushEntryResult Rejected(SyncPushEntry entry, string message)
        => Rejected(entry, SyncApiErrorCodes.ValidationError, message);

    private static SyncPushEntryResult Rejected(SyncPushEntry entry, string code, string message) => new()
    {
        ClientSequence = entry.ClientSequence,
        Accepted = false,
        Error = $"{code}: {message}",
    };

    private static CaptureChangePayload? TryParseCapturePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CaptureChangePayload>(payloadJson, SyncJson.Options);
        }
        catch (JsonException)
        {
            // Unparseable payloads are still logged to the change stream (the
            // entry is accepted); they simply do not update the capture row.
            return null;
        }
    }

    /// <summary>
    /// Parses a capture_status payload; null when it is missing or malformed,
    /// which rejects the entry (unlike a capture payload, the status change
    /// carries no meaning without one).
    /// </summary>
    private static CaptureStatusChangePayload? TryParseCaptureStatusPayload(string? payloadJson)
        => TryParsePayload<CaptureStatusChangePayload>(payloadJson);

    /// <summary>
    /// Parses a required change payload; null when it is missing or malformed,
    /// which rejects the entry. Used by every entity type whose change is
    /// meaningless without a payload the service can read.
    /// </summary>
    private static TPayload? TryParsePayload<TPayload>(string? payloadJson)
        where TPayload : class
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TPayload>(payloadJson, SyncJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SyncChangeDto ToDto(SyncChangeRow row) => new()
    {
        ChangeId = row.ChangeId,
        EntityType = row.EntityType,
        EntityId = row.EntityId,
        Operation = row.Operation,
        Revision = row.Revision,
        ChangedAtUtc = row.ChangedAtUtc,
        SourceDeviceId = row.SourceDeviceId,
        PayloadJson = row.PayloadJson,
    };
}
