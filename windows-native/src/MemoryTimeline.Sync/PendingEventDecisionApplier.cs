using System.Text.Json;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="IPendingEventDecisionApplier"/>: turns a companion's
/// <c>pending_event_decision</c> into the same approve/reject Windows performs
/// when the user clicks the button on the Review page.
///
/// <para><b>Why this one inbound write is safe.</b> A decision is not an edit.
/// It carries no field values to merge, applying it twice means the same as
/// applying it once, and two devices sending the same verdict agree by
/// construction — so accepting it does not turn the system into a multi-writer
/// one, which would need per-field revisions and a conflict policy that does not
/// exist.</para>
///
/// <para><b>Windows still decides how.</b> Everything here routes through
/// <see cref="IEventExtractionService"/> — <c>ApprovePendingEventAsync</c>,
/// <c>RejectPendingEventAsync</c> and <c>UpdatePendingEventAsync</c>, the same
/// three calls <c>ReviewViewModel</c> makes. Approval writes the event with its
/// tags, people and locations in one transaction, republishes the owning
/// capture's status, and resolves people alias-aware; none of that is
/// reimplemented here, because a second approval path would be a second set of
/// extraction rules to keep in step.</para>
///
/// <para><b>Two rules keep a remote verdict honest:</b></para>
/// <list type="bullet">
/// <item>a pending event Windows has already resolved is DROPPED, never
/// re-decided — the local review is the one that happened in front of the user,
/// and a stale phone must not resurrect or reverse it;</item>
/// <item>the same decision replayed produces no second event, because the
/// already-resolved check catches the replay before the approve runs.</item>
/// </list>
///
/// <para>Corrections ride along with an approval because they are part of the
/// same single act ("approve, but the title was wrong"). They are deliberately
/// narrow — title, dates, precision, category — and are written through the
/// normal pending-event update before the approve, so the approve sees an
/// ordinary corrected row and behaves identically to a locally edited one.
/// </para>
/// </summary>
public class PendingEventDecisionApplier : IPendingEventDecisionApplier
{
    private readonly IPendingEventRepository _pendingEventRepository;
    private readonly IEventExtractionService _extractionService;
    private readonly ILogger<PendingEventDecisionApplier> _logger;

    public PendingEventDecisionApplier(
        IPendingEventRepository pendingEventRepository,
        IEventExtractionService extractionService,
        ILogger<PendingEventDecisionApplier> logger)
    {
        _pendingEventRepository = pendingEventRepository;
        _extractionService = extractionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChangeApplicationResult> ApplyAsync(
        SyncChangeDto change, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(change.Operation, SyncOperation.Delete, StringComparison.OrdinalIgnoreCase))
            {
                // A verdict is a fact about a moment, not a row to retract.
                _logger.LogDebug(
                    "Change {ChangeId}: a pending event decision cannot be deleted; skipped", change.ChangeId);
                return ChangeApplicationResult.Skipped;
            }

            var payload = ParsePayload(change);
            if (payload == null)
            {
                return ChangeApplicationResult.FailedPermanent;
            }

            // The payload is authoritative; the change's entity id is the
            // fallback for a producer that only populated the envelope.
            var pendingEventId = string.IsNullOrWhiteSpace(payload.PendingEventId)
                ? change.EntityId
                : payload.PendingEventId;

            if (string.IsNullOrWhiteSpace(pendingEventId))
            {
                _logger.LogError(
                    "Change {ChangeId} decides no pending event; permanently skipped", change.ChangeId);
                return ChangeApplicationResult.FailedPermanent;
            }

            var decision = NormalizeDecision(payload.Decision);
            if (decision == null)
            {
                // An unrecognized verdict cannot be guessed at — acting on the
                // wrong one would either invent a memory or destroy one.
                _logger.LogError(
                    "Change {ChangeId} carries an unrecognized decision for pending event {PendingEventId}; permanently skipped",
                    change.ChangeId, pendingEventId);
                return ChangeApplicationResult.FailedPermanent;
            }

            var pendingEvent = await _pendingEventRepository.GetByIdAsync(pendingEventId, cancellationToken);
            if (pendingEvent == null)
            {
                // Rejecting DELETES the row, so a missing pending event is a
                // finished review rather than a lost decision. Dropping it is
                // also what makes a replayed reject idempotent.
                _logger.LogInformation(
                    "Pending event {PendingEventId} no longer exists; the {Decision} decision from device {DeviceId} is dropped",
                    pendingEventId, decision, payload.DecidedByDeviceId);
                return ChangeApplicationResult.Skipped;
            }

            if (IsResolved(pendingEvent))
            {
                // The user already reviewed this on Windows. A device's verdict
                // must never reverse a completed review, and a replay of the
                // decision that CAUSED the resolution stops here — which is what
                // keeps approve idempotent (no second event).
                _logger.LogInformation(
                    "Pending event {PendingEventId} was already resolved on Windows ({Status}); the {Decision} decision from device {DeviceId} is dropped",
                    pendingEventId, pendingEvent.Status, decision, payload.DecidedByDeviceId);
                return ChangeApplicationResult.Skipped;
            }

            if (decision == PendingEventDecision.Reject)
            {
                // Corrections are meaningless for a rejection: there is nothing
                // left to correct once the extraction is thrown away.
                await _extractionService.RejectPendingEventAsync(pendingEventId);
                _logger.LogInformation(
                    "Rejected pending event {PendingEventId} on behalf of device {DeviceId}",
                    pendingEventId, payload.DecidedByDeviceId);
                return ChangeApplicationResult.Applied;
            }

            if (payload.Corrections != null &&
                !await TryApplyCorrectionsAsync(pendingEvent, payload.Corrections))
            {
                return ChangeApplicationResult.FailedPermanent;
            }

            return await ApproveAsync(pendingEventId, payload, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Applying decision change {ChangeId} was cancelled; it will be retried", change.ChangeId);
            return ChangeApplicationResult.FailedRetryable;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Applying decision change {ChangeId} failed unexpectedly; it will be retried", change.ChangeId);
            return ChangeApplicationResult.FailedRetryable;
        }
    }

    #region Private Methods

    /// <summary>
    /// Runs the Windows approval. <c>ApprovePendingEventAsync</c> raises
    /// <see cref="InvalidOperationException"/> for the three states it refuses
    /// to approve from — already approved, no title, no start date — none of
    /// which a retry can change, so they are classified rather than retried
    /// forever. Re-reading the row separates "someone else already approved it"
    /// (a drop) from "this extraction is unusable" (permanent).
    /// </summary>
    private async Task<ChangeApplicationResult> ApproveAsync(
        string pendingEventId, PendingEventDecisionPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            var approved = await _extractionService.ApprovePendingEventAsync(pendingEventId);
            _logger.LogInformation(
                "Approved pending event {PendingEventId} as event {EventId} on behalf of device {DeviceId}",
                pendingEventId, approved.EventId, payload.DecidedByDeviceId);
            return ChangeApplicationResult.Applied;
        }
        catch (InvalidOperationException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = await _pendingEventRepository.GetByIdAsync(pendingEventId, cancellationToken);
            if (current == null || IsResolved(current))
            {
                _logger.LogInformation(
                    "Pending event {PendingEventId} was resolved concurrently; the decision is dropped rather than reapplied",
                    pendingEventId);
                return ChangeApplicationResult.Skipped;
            }

            _logger.LogError(
                ex, "Pending event {PendingEventId} cannot be approved; the decision is permanently skipped",
                pendingEventId);
            return ChangeApplicationResult.FailedPermanent;
        }
    }

    /// <summary>
    /// Applies the reviewer's corrections through the ordinary pending-event
    /// update, so the approve that follows sees a row indistinguishable from one
    /// edited on the Review page.
    ///
    /// Only the four fields the contract allows are touched; every other column
    /// (description, confidence, transcript, extraction payload, the explicit
    /// uncertainty bounds) is carried through untouched — the Windows edit panel
    /// passes the bounds through the same way rather than wiping them when the
    /// precision changes.
    /// </summary>
    /// <returns>False when the corrections describe an event that cannot exist.</returns>
    private async Task<bool> TryApplyCorrectionsAsync(
        PendingEvent pendingEvent, PendingEventCorrections corrections)
    {
        var corrected = false;

        if (!string.IsNullOrWhiteSpace(corrections.Title))
        {
            pendingEvent.Title = corrections.Title.Trim();
            corrected = true;
        }

        if (corrections.StartDate.HasValue)
        {
            pendingEvent.StartDate = corrections.StartDate.Value;
            corrected = true;
        }

        if (corrections.EndDate.HasValue)
        {
            pendingEvent.EndDate = corrections.EndDate.Value;
            corrected = true;
        }

        if (!string.IsNullOrWhiteSpace(corrections.DatePrecision))
        {
            // Defensive parse: an unrecognized precision falls back to Day
            // rather than failing the decision, matching how extraction handles
            // a model that invents a precision word.
            pendingEvent.DatePrecision = DatePrecisionParser.Parse(corrections.DatePrecision);
            corrected = true;
        }

        if (!string.IsNullOrWhiteSpace(corrections.Category))
        {
            var requested = corrections.Category.Trim();
            var category = EventCategory.AllCategories.FirstOrDefault(
                known => string.Equals(known, requested, StringComparison.OrdinalIgnoreCase));
            if (category == null)
            {
                // A category the archive does not have is dropped, not
                // rejected: the reviewer's real intent was to approve, and the
                // extraction's own category is a sound fallback.
                _logger.LogWarning(
                    "Pending event {PendingEventId} correction carried an unknown category; it is ignored",
                    pendingEvent.PendingId);
            }
            else
            {
                pendingEvent.Category = category;
                corrected = true;
            }
        }

        if (!corrected)
        {
            return true;
        }

        if (pendingEvent.EndDate.HasValue && pendingEvent.EndDate < pendingEvent.StartDate)
        {
            // The approve path writes the event row directly, so nothing
            // downstream would catch an inverted range; refuse it here instead
            // of storing a memory that ends before it starts. A retry would
            // carry the same corrections, so this is permanent.
            _logger.LogError(
                "Pending event {PendingEventId} corrections end before they start; the decision is permanently skipped",
                pendingEvent.PendingId);
            return false;
        }

        await _extractionService.UpdatePendingEventAsync(pendingEvent);
        _logger.LogInformation(
            "Applied reviewer corrections to pending event {PendingEventId} before approval", pendingEvent.PendingId);
        return true;
    }

    /// <summary>
    /// True once Windows has finished reviewing this extraction. Both signals
    /// are checked because they are written by different eras of the code: the
    /// boolean column predates the status vocabulary.
    /// </summary>
    private static bool IsResolved(PendingEvent pendingEvent) =>
        pendingEvent.IsApproved ||
        PendingStatusExtensions.FromString(pendingEvent.Status) != PendingStatus.PendingReview;

    /// <summary>
    /// Canonical verdict, or null when the payload names one this build does not
    /// know. Compared case-insensitively because the value crosses a wire from a
    /// client the Windows build does not control.
    /// </summary>
    private static string? NormalizeDecision(string? decision)
    {
        var trimmed = decision?.Trim();
        if (string.Equals(trimmed, PendingEventDecision.Approve, StringComparison.OrdinalIgnoreCase))
        {
            return PendingEventDecision.Approve;
        }

        return string.Equals(trimmed, PendingEventDecision.Reject, StringComparison.OrdinalIgnoreCase)
            ? PendingEventDecision.Reject
            : null;
    }

    private PendingEventDecisionPayload? ParsePayload(SyncChangeDto change)
    {
        if (string.IsNullOrWhiteSpace(change.PayloadJson))
        {
            _logger.LogError(
                "Change {ChangeId} for pending event decision {EntityId} carries no payload; permanently skipped",
                change.ChangeId, change.EntityId);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingEventDecisionPayload>(change.PayloadJson, SyncJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex, "Change {ChangeId} payload could not be parsed as a pending event decision; permanently skipped",
                change.ChangeId);
            return null;
        }
    }

    #endregion
}
