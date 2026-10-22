using System.Text.Json;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// Answers an <c>assistant_turn</c> pulled from the sync feed (design §19 Phase
/// 4). The service dispatched a companion's question toward Windows; this is
/// the Windows side: run the question through the archive's own Ask feature
/// and publish the answered turn back the way everything else travels — an
/// outbox row that <see cref="LocalOutboxPublisher"/> pushes in the same cycle.
/// </summary>
public interface IAssistantTurnResponder
{
    /// <summary>
    /// Applies one <c>assistant_turn</c> change. Applied when the turn was
    /// answered (or had already been answered — a replay never asks twice),
    /// Skipped when it is not for Windows or not open, FailedPermanent when the
    /// payload is unusable; only cancellation propagates.
    /// </summary>
    Task<ChangeApplicationResult> ApplyAsync(SyncChangeDto change, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAssistantTurnResponder"/> over <see cref="IMemoryQueryService"/>.
///
/// <para><b>What it answers.</b> A turn whose status is <c>pending</c> and
/// whose requested responder is <c>windows</c> (or unset, which the contract
/// defaults to Windows). Anything else — a turn claimed by another responder,
/// a cancelled or finished turn, a turn a provider or the device itself is
/// meant to answer — is skipped, and the cursor moves on.</para>
///
/// <para><b>Idempotence.</b> The pull is replayed when a later change in the
/// same page fails retryably, so the same question can arrive twice. A turn
/// whose latest outbox row already carries a terminal status is reported as
/// applied without asking again; the service discards a second answer anyway,
/// but the user would still be billed for it.</para>
///
/// <para><b>The service's bounds are honoured here.</b> The contract says the
/// bounds are the service's and that it rejects rather than truncates, so the
/// responder is the one that decides what to leave out: the answer is cut to
/// its limit, citations are capped, excerpts are cut, and a citation whose
/// event id is not a UUID is dropped rather than letting one row sink the
/// whole push. The numbers mirror the service's AssistantLimits; they live
/// here because Sync cannot reference the service project.</para>
///
/// <para><b>Privacy (§14.5).</b> Failure reasons are fixed display-safe
/// sentences, never exception text (which can carry paths or endpoints).
/// Nothing here logs a question, an answer or an excerpt — turn and session
/// ids and statuses only.</para>
/// </summary>
public class AssistantTurnResponder : IAssistantTurnResponder
{
    /// <summary>Maximum characters in a pushed answer (service AssistantLimits.AnswerMaxChars).</summary>
    public const int AnswerMaxChars = 20_000;

    /// <summary>Maximum citations one answer may carry (service AssistantLimits.CitationMaxCount).</summary>
    public const int CitationMaxCount = 50;

    /// <summary>Maximum characters in one citation's excerpt (service AssistantLimits.CitationExcerptMaxChars).</summary>
    public const int CitationExcerptMaxChars = 1_000;

    /// <summary>Maximum characters in a failure reason (service AssistantLimits.FailureReasonMaxChars).</summary>
    public const int FailureReasonMaxChars = 500;

    /// <summary>Shown on the phone when Windows has no model to answer with.</summary>
    public const string NotConfiguredReason =
        "Windows has no language model configured. Add an API key or a local endpoint in Settings on the PC, then ask again.";

    /// <summary>Shown on the phone when answering failed for any other reason.</summary>
    public const string AnswerFailedReason =
        "Windows could not answer this question right now. Try again in a moment.";

    private readonly IMemoryQueryService _queryService;
    private readonly ISyncOutboxRepository _outboxRepository;
    private readonly ILogger<AssistantTurnResponder> _logger;
    private readonly ILlmService? _llmService;

    /// <summary>
    /// Creates the responder. <paramref name="llmService"/> is optional and only
    /// names the model in the result for display; the answer itself comes from
    /// the query service.
    /// </summary>
    public AssistantTurnResponder(
        IMemoryQueryService queryService,
        ISyncOutboxRepository outboxRepository,
        ILogger<AssistantTurnResponder> logger,
        ILlmService? llmService = null)
    {
        _queryService = queryService;
        _outboxRepository = outboxRepository;
        _logger = logger;
        _llmService = llmService;
    }

    /// <inheritdoc />
    public async Task<ChangeApplicationResult> ApplyAsync(
        SyncChangeDto change, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(change.Operation, SyncOperation.Upsert, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping change {ChangeId}: assistant turns are upsert-only", change.ChangeId);
            return ChangeApplicationResult.Skipped;
        }

        var payload = ParsePayload(change);
        if (payload == null)
        {
            return ChangeApplicationResult.FailedPermanent;
        }

        var turnId = payload.TurnId;
        var status = Normalize(payload.Status);
        if (status != AssistantTurnStatus.Pending)
        {
            // Claimed, answered, failed or cancelled elsewhere; nothing for
            // Windows to do, and a late answer would be discarded anyway.
            _logger.LogDebug("Skipping assistant turn {TurnId}: status is {Status}", turnId, status);
            return ChangeApplicationResult.Skipped;
        }

        var requested = string.IsNullOrWhiteSpace(payload.RequestedResponder)
            ? AssistantResponder.Windows
            : Normalize(payload.RequestedResponder);
        if (requested != AssistantResponder.Windows)
        {
            _logger.LogDebug(
                "Skipping assistant turn {TurnId}: requested responder is {Responder}", turnId, requested);
            return ChangeApplicationResult.Skipped;
        }

        if (await AlreadyAnsweredAsync(turnId))
        {
            _logger.LogInformation("Assistant turn {TurnId} was already answered; not asking again", turnId);
            return ChangeApplicationResult.Applied;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(payload.Question))
        {
            await PublishFailureAsync(payload, "The question was empty.", retryable: false);
            return ChangeApplicationResult.Applied;
        }

        // Answered whole, never as assistant_turn_chunk changes: the contract
        // defines that change type, but a turn's "stream" request flag lives
        // on AssistantTurnCreateRequest and is not carried by the
        // AssistantTurnChangePayload that reaches Windows, so this responder
        // cannot know whether the client asked for chunks. Chunks are
        // advisory by contract ("a responder that cannot stream never
        // publishes any"), so the completed turn alone is a correct answer.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        MemoryAnswer answer;
        try
        {
            answer = await _queryService.AskAsync(payload.Question, options: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConfigurationException ex)
        {
            _logger.LogWarning(ex, "Assistant turn {TurnId} cannot be answered: no LLM provider is configured", turnId);
            await PublishFailureAsync(payload, NotConfiguredReason, retryable: false);
            return ChangeApplicationResult.Applied;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant turn {TurnId} failed while answering", turnId);
            await PublishFailureAsync(payload, AnswerFailedReason, retryable: true);
            return ChangeApplicationResult.Applied;
        }

        stopwatch.Stop();

        var result = new AssistantTurnResult
        {
            Answer = Bounded(answer.Answer?.Trim(), AnswerMaxChars) ?? string.Empty,
            // Windows always retrieves from the whole archive; an honest "nothing
            // here" is still an archive answer, and arrives with no citations.
            Grounding = AssistantGrounding.Archive,
            Citations = BuildCitations(answer.Citations),
            Model = _llmService?.ModelName,
            ElapsedMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
        };

        if (result.Answer.Length == 0)
        {
            // A completed turn must carry a non-empty answer; an empty one is
            // a failure the phone can retry, not a silent completion.
            _logger.LogWarning("Assistant turn {TurnId} produced an empty answer", turnId);
            await PublishFailureAsync(payload, AnswerFailedReason, retryable: true);
            return ChangeApplicationResult.Applied;
        }

        await PublishAsync(payload, AssistantTurnStatus.Completed, result, failureReason: null, failureRetryable: null);
        _logger.LogInformation(
            "Answered assistant turn {TurnId} (session {SessionId}, sequence {Sequence}) with {Citations} citation(s) in {Elapsed} ms",
            turnId, payload.SessionId, payload.Sequence, result.Citations.Count, result.ElapsedMs);
        return ChangeApplicationResult.Applied;
    }

    #region Private Methods

    private AssistantTurnChangePayload? ParsePayload(SyncChangeDto change)
    {
        if (string.IsNullOrWhiteSpace(change.PayloadJson))
        {
            _logger.LogError(
                "Change {ChangeId} for assistant turn {EntityId} carries no payload; permanently skipped",
                change.ChangeId, change.EntityId);
            return null;
        }

        AssistantTurnChangePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AssistantTurnChangePayload>(change.PayloadJson, SyncJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Change {ChangeId} payload could not be parsed as an assistant turn; permanently skipped", change.ChangeId);
            return null;
        }

        // The service requires payload.turnId to be a UUID equal to the
        // entity id on the way back; a change that violates that on the way
        // in could never be answered acceptably.
        if (payload == null
            || !Guid.TryParse(payload.TurnId, out var payloadGuid)
            || !Guid.TryParse(change.EntityId, out var entityGuid)
            || payloadGuid != entityGuid)
        {
            _logger.LogError(
                "Change {ChangeId} assistant turn id is missing, not a UUID, or differs from the entity id; permanently skipped",
                change.ChangeId);
            return null;
        }

        payload.TurnId = payloadGuid.ToString("D");
        return payload;
    }

    /// <summary>
    /// Whether this installation already published a terminal state for the
    /// turn. The outbox is the only memory of that: the service's copy is not
    /// readable from here, and the replayed change is the pre-answer one.
    /// </summary>
    private async Task<bool> AlreadyAnsweredAsync(string turnId)
    {
        var latest = await _outboxRepository.GetLatestForEntityAsync(SyncEntityType.AssistantTurn, turnId);
        if (latest?.PayloadJson == null)
        {
            return false;
        }

        try
        {
            var published = JsonSerializer.Deserialize<AssistantTurnChangePayload>(latest.PayloadJson, SyncJson.Options);
            var status = Normalize(published?.Status);
            return status is AssistantTurnStatus.Completed or AssistantTurnStatus.Failed or AssistantTurnStatus.Cancelled;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task PublishFailureAsync(AssistantTurnChangePayload incoming, string reason, bool retryable) =>
        PublishAsync(
            incoming, AssistantTurnStatus.Failed, result: null,
            failureReason: Bounded(reason, FailureReasonMaxChars), failureRetryable: retryable);

    /// <summary>
    /// Writes the answered (or failed) turn as an <c>assistant_turn</c> upsert
    /// row: the incoming payload echoed — the service applies the newest
    /// revision whole — with the status, responder and result set by Windows.
    /// </summary>
    private async Task PublishAsync(
        AssistantTurnChangePayload incoming,
        string status,
        AssistantTurnResult? result,
        string? failureReason,
        bool? failureRetryable)
    {
        var now = DateTime.UtcNow;
        var outgoing = new AssistantTurnChangePayload
        {
            TurnId = incoming.TurnId,
            SessionId = incoming.SessionId,
            Sequence = incoming.Sequence,
            Question = incoming.Question,
            Status = status,
            RequestedResponder = string.IsNullOrWhiteSpace(incoming.RequestedResponder)
                ? AssistantResponder.Windows
                : incoming.RequestedResponder,
            ActualResponder = AssistantResponder.Windows,
            OriginDeviceId = incoming.OriginDeviceId,
            ClientContext = incoming.ClientContext,
            Result = result,
            FailureReason = failureReason,
            FailureRetryable = failureRetryable,
            UpdatedAtUtc = now,
        };

        await _outboxRepository.AddAsync(new SyncOutbox
        {
            EntityType = SyncEntityType.AssistantTurn,
            EntityId = incoming.TurnId,
            Operation = SyncOutboxOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(outgoing, SyncJson.Options),
            CreatedAt = now,
        });
    }

    /// <summary>
    /// Citations the service will accept: UUID event ids only, at most
    /// <see cref="CitationMaxCount"/>, excerpts cut to
    /// <see cref="CitationExcerptMaxChars"/>, the display date formatted here
    /// with the same precision-honest formatter every screen uses.
    /// </summary>
    private static List<AssistantCitation> BuildCitations(IEnumerable<CitedEvent> cited)
    {
        return cited
            .Where(c => Guid.TryParse(c.EventId, out _))
            .Take(CitationMaxCount)
            .Select(c => new AssistantCitation
            {
                EventId = c.EventId,
                Title = c.Title,
                DisplayDate = DateDisplay.FormatPrecise(c.StartDate, c.DatePrecision),
                Excerpt = Bounded(c.Snippet, CitationExcerptMaxChars),
            })
            .ToList();
    }

    /// <summary>Leading excerpt of at most <paramref name="maxChars"/>, never splitting a surrogate pair.</summary>
    private static string? Bounded(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..length];
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    #endregion
}
