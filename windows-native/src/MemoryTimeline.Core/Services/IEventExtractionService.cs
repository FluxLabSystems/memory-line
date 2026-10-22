using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for extracting events from transcripts.
/// </summary>
public interface IEventExtractionService
{
    /// <summary>
    /// Processes a recording queue item: transcribe and extract events.
    /// </summary>
    /// <param name="queueId">Queue item ID</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">
    /// Aborts the in-flight speech-to-text or LLM call; propagates as
    /// <see cref="OperationCanceledException"/> (never as a failed result).
    /// </param>
    /// <returns>Number of events extracted</returns>
    Task<int> ProcessRecordingAsync(
        string queueId,
        IProgress<(int percentage, string message)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts events from a transcript and creates pending events.
    /// </summary>
    /// <param name="queueId">Queue item ID</param>
    /// <param name="transcript">Transcript text</param>
    /// <param name="cancellationToken">Aborts the in-flight LLM call</param>
    /// <returns>List of created pending events</returns>
    Task<List<PendingEvent>> ExtractAndCreatePendingEventsAsync(
        string queueId, string transcript, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending event and creates it as a real event, then republishes
    /// the owning capture's status so the phone sees the new review counts.
    /// </summary>
    /// <param name="pendingEventId">Pending event ID</param>
    /// <param name="publishCaptureStatus">
    /// False for bulk review, where the caller publishes once per affected
    /// capture via <see cref="PublishCaptureStatusAsync"/> instead of once per
    /// event.
    /// </param>
    /// <returns>Created event</returns>
    Task<Event> ApprovePendingEventAsync(string pendingEventId, bool publishCaptureStatus = true);

    /// <summary>
    /// Updates a pending event before approval.
    /// </summary>
    /// <param name="pendingEvent">Updated pending event</param>
    /// <returns>Updated pending event</returns>
    Task<PendingEvent> UpdatePendingEventAsync(PendingEvent pendingEvent);

    /// <summary>
    /// Rejects and deletes a pending event, then republishes the owning
    /// capture's status so the phone sees the new review counts.
    /// </summary>
    /// <param name="pendingEventId">Pending event ID</param>
    /// <param name="publishCaptureStatus">
    /// False for bulk review, where the caller publishes once per affected
    /// capture via <see cref="PublishCaptureStatusAsync"/> instead of once per
    /// event.
    /// </param>
    Task RejectPendingEventAsync(string pendingEventId, bool publishCaptureStatus = true);

    /// <summary>
    /// Republishes one capture's processing status after its review moved:
    /// refreshed review counts, and `completed` once no event is left to review.
    /// A no-op when no capture-status publisher is registered; never throws
    /// (design §19 Phase 3).
    /// </summary>
    /// <param name="queueId">Queue item that owns the reviewed events</param>
    Task PublishCaptureStatusAsync(string queueId);

    /// <summary>
    /// Gets all pending events for a queue item.
    /// </summary>
    /// <param name="queueId">Queue item ID</param>
    /// <returns>List of pending events</returns>
    Task<List<PendingEvent>> GetPendingEventsForQueueAsync(string queueId);

    /// <summary>
    /// Gets all pending events awaiting review.
    /// </summary>
    /// <returns>List of pending events</returns>
    Task<List<PendingEvent>> GetAllPendingEventsAsync();

    /// <summary>
    /// Gets count of pending events by status.
    /// </summary>
    /// <param name="isApproved">Approval status</param>
    /// <returns>Count</returns>
    Task<int> GetPendingEventCountAsync(bool? isApproved = null);

    /// <summary>Computes create/update suggestions for the people mentioned in a pending event.</summary>
    /// <param name="pendingEventId">Pending event ID</param>
    /// <returns>One suggestion per distinct person name found in the extraction payload</returns>
    Task<List<PersonSuggestionDto>> GetPersonSuggestionsAsync(string pendingEventId);

    /// <summary>Applies one suggestion (creates the person or merges suggested details). Returns the updated suggestion (IsApplied=true).</summary>
    /// <param name="suggestion">The suggestion to apply</param>
    /// <returns>The same suggestion instance with IsApplied set to true</returns>
    Task<PersonSuggestionDto> ApplyPersonSuggestionAsync(PersonSuggestionDto suggestion);
}
