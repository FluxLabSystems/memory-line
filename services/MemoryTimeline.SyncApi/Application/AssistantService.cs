using System.Text.Json;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="IAssistantService"/> over the sync database. Stateless over the
/// context factory; registered as a singleton.
///
/// Logs session IDs, turn IDs, statuses and responder names only — never the
/// question, the answer, a citation excerpt or a client-supplied excerpt
/// (design §14.5 names assistant prompts containing personal memories
/// explicitly). That constraint is why the log lines here look sparse: there is
/// nothing else about a turn that is safe to write down.
/// </summary>
public sealed class AssistantService : IAssistantService
{
    /// <summary>Responder values this build knows how to record and route.</summary>
    private static readonly string[] AllowedResponders =
    [
        AssistantResponder.Windows,
        AssistantResponder.Provider,
        AssistantResponder.OnDevice,
    ];

    /// <summary>
    /// Responders something actually answers for. The contract names three
    /// modes, and all three are accepted as vocabulary, but only the Windows
    /// client (<c>AssistantTurnResponder</c> in <c>MemoryTimeline.Sync</c>)
    /// claims turns off the feed: no LLM provider is configured on the
    /// service and no device pre-processes locally. A turn asking for either
    /// of those is failed at once with a non-retryable reason instead of
    /// sitting <c>pending</c> until the idle reaper cancels it a day later —
    /// the asker is told immediately, which is the honest answer.
    /// </summary>
    private static readonly string[] DeployedResponders =
    [
        AssistantResponder.Windows,
    ];

    /// <summary>
    /// Number of times a submission retries after losing a race for the next
    /// sequence number in a session. Two devices cannot share a session, so the
    /// only contender is the same device submitting concurrently; one retry
    /// after re-reading the session settles that, and a second guards against
    /// an unlucky third.
    /// </summary>
    private const int SequenceRetryAttempts = 3;

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly ILogger<AssistantService> _logger;

    public AssistantService(IDbContextFactory<SyncDbContext> contextFactory, ILogger<AssistantService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<ServiceResult<AssistantSessionResponse>> CreateSessionAsync(
        Device caller,
        AssistantSessionCreateRequest request,
        CancellationToken cancellationToken)
    {
        // Client JSON can null out non-nullable DTO strings; an absent value
        // means "the default", which the contract fixes as Windows.
        var responder = string.IsNullOrWhiteSpace(request.PreferredResponder)
            ? AssistantResponder.Windows
            : request.PreferredResponder.Trim().ToLowerInvariant();
        if (!AllowedResponders.Contains(responder))
        {
            return ServiceResult<AssistantSessionResponse>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError, ResponderMessage("preferredResponder"));
        }

        var surface = string.IsNullOrWhiteSpace(request.Surface) ? null : request.Surface.Trim();
        if (surface is { Length: > AssistantLimits.SurfaceMaxChars })
        {
            return ServiceResult<AssistantSessionResponse>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                $"surface must be at most {AssistantLimits.SurfaceMaxChars} characters.");
        }

        var now = DateTime.UtcNow;
        var session = new AssistantSessionRow
        {
            SessionId = Guid.NewGuid().ToString("D"),
            OwnerId = caller.OwnerId,
            DeviceId = caller.DeviceId,
            PreferredResponder = responder,
            Surface = surface,
            CreatedAtUtc = now,
            LastTurnAtUtc = now,
            TurnCount = 0,
        };

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.AssistantSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Assistant session {SessionId} opened by device {DeviceId} (preferred responder {Responder}).",
            session.SessionId, caller.DeviceId, responder);
        return ServiceResult<AssistantSessionResponse>.Ok(EntityMappers.ToAssistantSessionResponse(session));
    }

    public async Task<ServiceResult<AssistantSessionResponse>> GetSessionAsync(
        Device caller,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (session, error) = await LoadSessionAsync(db, caller, sessionId, track: false, cancellationToken);
        return session is null
            ? ServiceResult<AssistantSessionResponse>.Fail(error!)
            : ServiceResult<AssistantSessionResponse>.Ok(EntityMappers.ToAssistantSessionResponse(session));
    }

    public async Task<ServiceResult<AssistantTurnWriteResult>> SubmitTurnAsync(
        Device caller,
        string sessionId,
        AssistantTurnCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.TurnId, out var turnGuid))
        {
            return ValidationFailure<AssistantTurnWriteResult>("turnId must be a UUID.");
        }

        var question = request.Question ?? string.Empty;
        if (string.IsNullOrWhiteSpace(question))
        {
            return ValidationFailure<AssistantTurnWriteResult>("question is required.");
        }

        if (question.Length > AssistantLimits.QuestionMaxChars)
        {
            return ValidationFailure<AssistantTurnWriteResult>(
                $"question must be at most {AssistantLimits.QuestionMaxChars} characters.");
        }

        // Validated before the session is loaded so a client learns its
        // responder vocabulary is wrong even when it also got the session wrong.
        string? requestedResponder = null;
        if (request.Responder is not null)
        {
            requestedResponder = request.Responder.Trim().ToLowerInvariant();
            if (!AllowedResponders.Contains(requestedResponder))
            {
                return ValidationFailure<AssistantTurnWriteResult>(ResponderMessage("responder"));
            }
        }

        var contextError = ValidateClientContext(request.ClientContext);
        if (contextError is not null)
        {
            return ValidationFailure<AssistantTurnWriteResult>(contextError);
        }

        var turnId = turnGuid.ToString("D");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            var (session, sessionError) = await LoadSessionAsync(db, caller, sessionId, track: true, cancellationToken);
            if (session is null)
            {
                return ServiceResult<AssistantTurnWriteResult>.Fail(sessionError!);
            }

            var replay = await TryReplayTurnAsync(db, session, turnId, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            var now = DateTime.UtcNow;
            var responder = requestedResponder ?? session.PreferredResponder;
            var deployed = DeployedResponders.Contains(responder);
            var turn = new AssistantTurnRow
            {
                TurnId = turnId,
                SessionId = session.SessionId,
                OwnerId = session.OwnerId,
                OriginDeviceId = session.DeviceId,
                Sequence = session.TurnCount + 1,
                Question = question,
                // Pending, not dispatched: publishing a change is not delivery.
                // Windows may be asleep for hours, and claiming the turn reached
                // a responder the moment it reached the change log would tell a
                // polling client something the service does not actually know.
                // A responder moves the turn on when it genuinely picks it up.
                //
                // A mode nothing answers for is failed here and now instead
                // (see DeployedResponders): terminal, non-retryable, with a
                // display-safe reason the asker reads straight off the reply.
                Status = deployed ? AssistantTurnStatus.Pending : AssistantTurnStatus.Failed,
                RequestedResponder = responder,
                ActualResponder = null,
                ClientContextJson = request.ClientContext is null or { Count: 0 }
                    ? null
                    : JsonSerializer.Serialize(request.ClientContext, SyncJson.Options),
                Stream = request.Stream,
                FailureReason = deployed ? null : NoResponderReason(responder),
                FailureRetryable = deployed ? null : false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = deployed ? null : now,
                Revision = 1,
            };

            session.TurnCount = turn.Sequence;
            session.LastTurnAtUtc = now;

            db.AssistantTurns.Add(turn);

            if (deployed)
            {
                // The change row rides the same SaveChanges as the turn itself, so
                // a turn can never exist without having been published (nothing to
                // answer it) and a published turn can never be missing (nowhere for
                // the answer to land). The caller is the change's source device, so
                // echo suppression keeps the asking phone from pulling back its own
                // question while every other device — Windows — sees it.
                //
                // A turn failed at birth is not published: there is nothing for a
                // responder to do with it, and the asker already holds the answer.
                db.SyncChanges.Add(EntityMappers.CreateAssistantTurnChange(turn, caller.DeviceId));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // Either the same turnId landed concurrently (replay it) or two
                // submissions claimed the same sequence (re-read and retry).
                db.ChangeTracker.Clear();
                if (attempt >= SequenceRetryAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Assistant turn {TurnId} could not be accepted into session {SessionId} after {Attempts} attempts.",
                        turnId, sessionId, attempt);
                    throw;
                }

                continue;
            }

            if (deployed)
            {
                _logger.LogInformation(
                    "Assistant turn {TurnId} accepted into session {SessionId} as sequence {Sequence} " +
                    "(requested responder {Responder}).",
                    turn.TurnId, turn.SessionId, turn.Sequence, turn.RequestedResponder);
            }
            else
            {
                _logger.LogInformation(
                    "Assistant turn {TurnId} in session {SessionId} (sequence {Sequence}) failed at once: " +
                    "no responder for mode {Responder} is deployed.",
                    turn.TurnId, turn.SessionId, turn.Sequence, turn.RequestedResponder);
            }

            return ServiceResult<AssistantTurnWriteResult>.Ok(
                new AssistantTurnWriteResult(EntityMappers.ToAssistantTurnResponse(turn), Created: true));
        }
    }

    public async Task<ServiceResult<AssistantTurnResponse>> GetTurnAsync(
        Device caller,
        string turnId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (turn, error) = await LoadTurnAsync(db, caller, turnId, track: false, cancellationToken);
        return turn is null
            ? ServiceResult<AssistantTurnResponse>.Fail(error!)
            : ServiceResult<AssistantTurnResponse>.Ok(EntityMappers.ToAssistantTurnResponse(turn));
    }

    public async Task<ServiceResult<AssistantTurnResponse>> CancelTurnAsync(
        Device caller,
        string turnId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (turn, error) = await LoadTurnAsync(db, caller, turnId, track: true, cancellationToken);
        if (turn is null)
        {
            return ServiceResult<AssistantTurnResponse>.Fail(error!);
        }

        // Already finished, one way or another. Cancelling is idempotent and
        // never rewrites a terminal state: re-cancelling a cancelled turn must
        // not append a second change, and cancelling a turn that completed
        // while the request was in flight must not erase the answer the user
        // may already have heard.
        if (AssistantTurnLifecycle.IsTerminal(turn.Status))
        {
            return ServiceResult<AssistantTurnResponse>.Ok(EntityMappers.ToAssistantTurnResponse(turn));
        }

        var now = DateTime.UtcNow;
        turn.Status = AssistantTurnStatus.Cancelled;
        turn.UpdatedAtUtc = now;
        turn.CompletedAtUtc = now;
        turn.Revision++;

        // Published so a responder mid-answer learns to stop; the caller is the
        // source device, so the cancellation reaches Windows and not the phone
        // that already knows it cancelled.
        db.SyncChanges.Add(EntityMappers.CreateAssistantTurnChange(turn, caller.DeviceId));
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Assistant turn {TurnId} cancelled by device {DeviceId} (revision {Revision}).",
            turn.TurnId, caller.DeviceId, turn.Revision);
        return ServiceResult<AssistantTurnResponse>.Ok(EntityMappers.ToAssistantTurnResponse(turn));
    }

    /// <summary>
    /// Returns the stored turn when <paramref name="turnId"/> was already
    /// accepted, or null when submission should proceed. A turn ID that belongs
    /// to a different session is a conflict rather than a replay: the client
    /// reused an identifier across conversations, and answering the earlier
    /// question would be worse than refusing.
    /// </summary>
    private async Task<ServiceResult<AssistantTurnWriteResult>?> TryReplayTurnAsync(
        SyncDbContext db,
        AssistantSessionRow session,
        string turnId,
        CancellationToken cancellationToken)
    {
        var existing = await db.AssistantTurns.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TurnId == turnId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.SessionId != session.SessionId || existing.OriginDeviceId != session.DeviceId)
        {
            return ServiceResult<AssistantTurnWriteResult>.Fail(
                StatusCodes.Status409Conflict, SyncApiErrorCodes.AssistantTurnConflict,
                "A turn with this ID already exists in another session.");
        }

        _logger.LogInformation(
            "Assistant turn {TurnId} submission replayed idempotently (status {Status}).",
            existing.TurnId, existing.Status);
        return ServiceResult<AssistantTurnWriteResult>.Ok(
            new AssistantTurnWriteResult(EntityMappers.ToAssistantTurnResponse(existing), Created: false));
    }

    /// <summary>
    /// Validates device-supplied grounding against <see cref="AssistantLimits"/>,
    /// returning the rejection message or null when it is acceptable. Oversized
    /// context is refused whole: trimming it would answer the user on a
    /// fraction of what they sent, and only the device knows which of its
    /// retrieval hits it can afford to drop.
    /// </summary>
    private static string? ValidateClientContext(List<AssistantContextItem>? context)
    {
        if (context is null or { Count: 0 })
        {
            return null;
        }

        if (context.Count > AssistantLimits.ClientContextMaxItems)
        {
            return $"clientContext must contain at most {AssistantLimits.ClientContextMaxItems} items.";
        }

        var totalChars = 0;
        foreach (var item in context)
        {
            if (item is null)
            {
                return "clientContext items must not be null.";
            }

            if (!Guid.TryParse(item.EventId, out _))
            {
                return "clientContext eventId must be a UUID.";
            }

            var title = item.Title ?? string.Empty;
            if (title.Length > AssistantLimits.ClientContextTitleMaxChars)
            {
                return $"clientContext title must be at most {AssistantLimits.ClientContextTitleMaxChars} characters.";
            }

            if (item.Excerpt is { Length: > AssistantLimits.ClientContextExcerptMaxChars })
            {
                return "clientContext excerpt must be at most " +
                    $"{AssistantLimits.ClientContextExcerptMaxChars} characters.";
            }

            totalChars += title.Length + (item.Excerpt?.Length ?? 0);
        }

        return totalChars > AssistantLimits.ClientContextMaxTotalChars
            ? $"clientContext must be at most {AssistantLimits.ClientContextMaxTotalChars} characters in total."
            : null;
    }

    /// <summary>
    /// Loads a session the caller is allowed to use. A session that exists for
    /// another device — even another device of the same owner — is reported as
    /// missing, matching how an unknown device is reported on revoke: a
    /// distinct "forbidden" would confirm the identifier is real.
    /// </summary>
    private static async Task<(AssistantSessionRow? Session, ServiceError? Error)> LoadSessionAsync(
        SyncDbContext db,
        Device caller,
        string sessionId,
        bool track,
        CancellationToken cancellationToken)
    {
        var notFound = new ServiceError(
            StatusCodes.Status404NotFound, SyncApiErrorCodes.AssistantSessionNotFound, "No such assistant session.");

        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return (null, notFound);
        }

        var normalizedId = sessionGuid.ToString("D");
        var query = track ? db.AssistantSessions : db.AssistantSessions.AsNoTracking();
        var session = await query.FirstOrDefaultAsync(
            s => s.SessionId == normalizedId
                && s.OwnerId == caller.OwnerId
                && s.DeviceId == caller.DeviceId,
            cancellationToken);
        return session is null ? (null, notFound) : (session, null);
    }

    /// <summary>
    /// Loads a turn the caller is allowed to read. Scoped to the asking device
    /// via <see cref="AssistantTurnRow.OriginDeviceId"/>, so another device of
    /// the same owner sees a plain not-found rather than the question text.
    /// </summary>
    private static async Task<(AssistantTurnRow? Turn, ServiceError? Error)> LoadTurnAsync(
        SyncDbContext db,
        Device caller,
        string turnId,
        bool track,
        CancellationToken cancellationToken)
    {
        var notFound = new ServiceError(
            StatusCodes.Status404NotFound, SyncApiErrorCodes.AssistantTurnNotFound, "No such assistant turn.");

        if (!Guid.TryParse(turnId, out var turnGuid))
        {
            return (null, notFound);
        }

        var normalizedId = turnGuid.ToString("D");
        var query = track ? db.AssistantTurns : db.AssistantTurns.AsNoTracking();
        var turn = await query.FirstOrDefaultAsync(
            t => t.TurnId == normalizedId
                && t.OwnerId == caller.OwnerId
                && t.OriginDeviceId == caller.DeviceId,
            cancellationToken);
        return turn is null ? (null, notFound) : (turn, null);
    }

    /// <summary>
    /// The rejection message for an unrecognized responder, worded like
    /// DeviceService's unknown-platform rejection (same code, same shape) so
    /// clients handle one style of vocabulary error, not two.
    /// </summary>
    private static string ResponderMessage(string field)
        => $"{field} must be one of: windows, provider, on_device.";

    /// <summary>
    /// The display-safe, non-retryable reason a turn for an undeployed mode
    /// fails with. Fixed text keyed on the mode — no paths, no credentials,
    /// nothing from the question (design §14.5) — and it names the mode that
    /// would work, so a client can fall back without guessing.
    /// </summary>
    internal static string NoResponderReason(string responder)
        => $"No responder for mode {responder} is deployed on this sync service; ask again with the windows responder.";

    private static ServiceResult<T> ValidationFailure<T>(string message)
        => ServiceResult<T>.Fail(StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError, message);
}

/// <summary>
/// Turn lifecycle rules shared by the assistant service (which cancels turns)
/// and <see cref="SyncChangeService"/> (which applies pushed answers to them).
/// Both have to agree on what "finished" means, so the rule lives in one place:
/// a divergence would let a cancelled turn be resurrected by a late answer.
/// </summary>
internal static class AssistantTurnLifecycle
{
    /// <summary>Every status a turn may be in.</summary>
    public static readonly string[] AllStatuses =
    [
        AssistantTurnStatus.Pending,
        AssistantTurnStatus.Dispatched,
        AssistantTurnStatus.Answering,
        AssistantTurnStatus.Completed,
        AssistantTurnStatus.Failed,
        AssistantTurnStatus.Cancelled,
    ];

    /// <summary>
    /// Whether a turn has finished for good. Terminal states never change
    /// again — not to another terminal state and not back to an active one.
    /// Cancellation is the reason this is absolute: a user who interrupted the
    /// assistant must not be answered anyway when the responder's work lands a
    /// moment later.
    /// </summary>
    public static bool IsTerminal(string status) => status
        is AssistantTurnStatus.Completed
        or AssistantTurnStatus.Failed
        or AssistantTurnStatus.Cancelled;
}
