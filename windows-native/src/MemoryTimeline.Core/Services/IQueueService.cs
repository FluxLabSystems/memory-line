using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for recording queue management and processing.
/// </summary>
public interface IQueueService
{
    /// <summary>
    /// Adds a new recording to the queue.
    /// </summary>
    Task<RecordingQueue> AddToQueueAsync(AudioRecordingDto recording, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes one specific queue item (design §7.3). Waits for any in-flight
    /// processing to finish first; skips items that are already processing or
    /// completed.
    /// </summary>
    Task ProcessCaptureAsync(string queueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues pasted/typed text as a <see cref="QueueSourceType.Text"/> source.
    /// The text goes straight to LLM extraction when processed — no audio file,
    /// no speech-to-text. Throws <see cref="ArgumentException"/> for null/empty/
    /// whitespace text (callers surface the message in an InfoBar).
    /// </summary>
    /// <param name="text">The text to extract events from. Stored verbatim.</param>
    /// <param name="label">Optional display label (trimmed, clipped to 200 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new queue item's id.</returns>
    Task<string> EnqueueTextAsync(string text, string? label = null, CancellationToken ct = default);

    /// <summary>
    /// Gets all recordings in the queue.
    /// </summary>
    Task<IEnumerable<AudioRecordingDto>> GetAllQueueItemsAsync();

    /// <summary>
    /// Gets a specific queue item by ID.
    /// </summary>
    Task<RecordingQueue?> GetQueueItemAsync(string queueId);

    /// <summary>
    /// Updates queue item status and, when provided, its fine-grained processing
    /// stage (<see cref="QueueProcessingStage"/>).
    /// </summary>
    Task UpdateQueueItemStatusAsync(string queueId, string status, string? errorMessage = null, string? processingStage = null);

    /// <summary>
    /// Removes a queue item.
    /// </summary>
    Task RemoveQueueItemAsync(string queueId);

    /// <summary>
    /// Gets the count of items by status.
    /// </summary>
    Task<int> GetQueueCountByStatusAsync(string status);

    /// <summary>
    /// Processes the next pending item in the queue. Returns at once when
    /// processing is already in progress. Cancellation aborts the in-flight
    /// transcription/extraction, puts the item back to Pending and propagates
    /// as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task ProcessNextItemAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes all pending items in the queue, one after another. Returns
    /// at once when processing is already in progress. Cancellation aborts
    /// the item in flight (put back to Pending), leaves the rest untouched
    /// and propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task ProcessAllPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries a failed queue item.
    /// </summary>
    Task RetryFailedItemAsync(string queueId);

    /// <summary>
    /// Clears completed items from the queue.
    /// </summary>
    Task ClearCompletedItemsAsync();

    /// <summary>
    /// Event raised when a queue item's status changes.
    /// </summary>
    event EventHandler<QueueItemStatusChangedEventArgs>? QueueItemStatusChanged;

    /// <summary>
    /// Event raised when processing progress changes.
    /// </summary>
    event EventHandler<QueueProcessingProgressEventArgs>? ProcessingProgressChanged;
}

/// <summary>
/// Event args for queue item status change.
/// </summary>
public class QueueItemStatusChangedEventArgs : EventArgs
{
    public string QueueId { get; set; } = string.Empty;
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Event args for queue processing progress.
/// </summary>
public class QueueProcessingProgressEventArgs : EventArgs
{
    public string QueueId { get; set; } = string.Empty;
    public int ProgressPercentage { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Thrown when processing cannot proceed because of missing/invalid app configuration
/// (e.g. no LLM API key). Treated as NON-RETRYABLE by the queue's retry loop:
/// retrying cannot succeed until the user changes settings.
/// </summary>
public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message)
    {
    }

    public ConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
