using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Queue service implementation for managing recording queue and processing.
/// Registered as a SINGLETON: the processing semaphore and status/progress events
/// must be shared app-wide, so all injected dependencies must be singleton-safe
/// (repositories create a DbContext per operation via IDbContextFactory).
/// </summary>
public class QueueService : IQueueService
{
    private readonly IRecordingQueueRepository _queueRepository;
    private readonly IEventExtractionService _extractionService;
    private readonly INotificationService _notificationService;
    private readonly ICaptureStatusPublisher? _statusPublisher;
    private readonly IRecordingStorage? _recordingStorage;
    private readonly ILogger<QueueService> _logger;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);

    public event EventHandler<QueueItemStatusChangedEventArgs>? QueueItemStatusChanged;
    public event EventHandler<QueueProcessingProgressEventArgs>? ProcessingProgressChanged;

    /// <summary>
    /// Creates the service. <paramref name="recordingStorage"/> is optional and
    /// trailing like the status publisher: with it, removing a queue row also
    /// releases the recording files the row owned; without it the rows go and
    /// the files stay for the cache sweep.
    /// </summary>
    public QueueService(
        IRecordingQueueRepository queueRepository,
        IEventExtractionService extractionService,
        ILogger<QueueService> logger,
        INotificationService notificationService,
        ICaptureStatusPublisher? statusPublisher = null,
        IRecordingStorage? recordingStorage = null)
    {
        _queueRepository = queueRepository;
        _extractionService = extractionService;
        _logger = logger;
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _statusPublisher = statusPublisher;
        _recordingStorage = recordingStorage;
    }

    /// <summary>
    /// Adds a new recording to the queue.
    /// </summary>
    public async Task<RecordingQueue> AddToQueueAsync(AudioRecordingDto recording, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var queueItem = new RecordingQueue
            {
                QueueId = recording.QueueId,
                AudioFilePath = recording.AudioFilePath,
                Status = QueueStatus.Pending,
                ProcessingStage = QueueProcessingStage.ReadyForTranscription,
                SyncState = QueueSyncState.LocalOnly,
                DurationSeconds = recording.DurationSeconds,
                FileSizeBytes = recording.FileSizeBytes,
                CreatedAt = recording.CreatedAt
            };

            var added = await _queueRepository.AddAsync(queueItem);

            _logger.LogInformation("Added recording to queue: {QueueId}", added.QueueId);
            RaiseStatusChanged(added.QueueId, "", QueueStatus.Pending);

            return added;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding recording to queue");
            throw;
        }
    }

    /// <summary>
    /// Enqueues pasted/typed text as a Text source. No audio file exists for
    /// these rows: AudioFilePath stores string.Empty (NOT NULL column
    /// convention, see <see cref="RecordingQueue.AudioFilePath"/>) and the
    /// text itself is persisted in <see cref="RecordingQueue.Transcript"/> so
    /// processing skips speech-to-text entirely.
    /// </summary>
    public async Task<string> EnqueueTextAsync(string text, string? label = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // Clear, user-facing message: callers surface this in an InfoBar.
            throw new ArgumentException("Text to capture cannot be empty.");
        }

        try
        {
            var trimmedLabel = label?.Trim();
            if (string.IsNullOrEmpty(trimmedLabel))
            {
                trimmedLabel = null;
            }
            else if (trimmedLabel.Length > 200)
            {
                trimmedLabel = trimmedLabel[..200];
            }

            var queueItem = new RecordingQueue
            {
                QueueId = Guid.NewGuid().ToString(),
                SourceType = QueueSourceType.Text,
                SourceLabel = trimmedLabel,
                Transcript = text,
                AudioFilePath = string.Empty,
                Status = QueueStatus.Pending,
                DurationSeconds = null,
                FileSizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
                CreatedAt = DateTime.UtcNow
            };

            ct.ThrowIfCancellationRequested();

            var added = await _queueRepository.AddAsync(queueItem);

            _logger.LogInformation("Added text source to queue: {QueueId} ({Bytes} bytes)",
                added.QueueId, queueItem.FileSizeBytes);
            RaiseStatusChanged(added.QueueId, "", QueueStatus.Pending);

            return added.QueueId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding text source to queue");
            throw;
        }
    }

    /// <summary>
    /// Gets all recordings in the queue.
    /// </summary>
    public async Task<IEnumerable<AudioRecordingDto>> GetAllQueueItemsAsync()
    {
        try
        {
            var items = await _queueRepository.GetAllAsync();
            return items.Select(AudioRecordingDto.FromRecordingQueue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queue items");
            throw;
        }
    }

    /// <summary>
    /// Gets a specific queue item by ID.
    /// </summary>
    public async Task<RecordingQueue?> GetQueueItemAsync(string queueId)
    {
        return await _queueRepository.GetByIdAsync(queueId);
    }

    /// <summary>
    /// Updates queue item status and, when provided, its processing stage.
    /// </summary>
    public async Task UpdateQueueItemStatusAsync(string queueId, string status, string? errorMessage = null, string? processingStage = null)
    {
        try
        {
            var item = await _queueRepository.GetByIdAsync(queueId);
            if (item == null)
            {
                _logger.LogWarning("Queue item not found: {QueueId}", queueId);
                return;
            }

            var oldStatus = item.Status;
            item.Status = status;
            item.ErrorMessage = errorMessage;

            if (processingStage != null)
            {
                item.ProcessingStage = processingStage;
            }

            if (status == QueueStatus.Completed || status == QueueStatus.Failed)
            {
                item.ProcessedAt = DateTime.UtcNow;
            }

            await _queueRepository.UpdateAsync(item);

            _logger.LogInformation("Updated queue item {QueueId} status: {OldStatus} -> {NewStatus}",
                queueId, oldStatus, status);

            RaiseStatusChanged(queueId, oldStatus, status, errorMessage);

            // Every stage transition of a device capture is news on the phone
            // that recorded it (design §19 Phase 3). This is the single funnel
            // for stage changes — the import service routes through here too.
            await TryPublishCaptureStatusAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating queue item status");
            throw;
        }
    }

    /// <summary>
    /// Removes a queue item.
    /// </summary>
    public async Task RemoveQueueItemAsync(string queueId)
    {
        try
        {
            var item = await _queueRepository.GetByIdAsync(queueId);
            if (item != null)
            {
                await _queueRepository.DeleteAsync(item);
                _logger.LogInformation("Removed queue item: {QueueId}", queueId);

                // The row is gone; its files go too, unless an approved event
                // or another row still plays them.
                await TryReleaseRecordingAsync(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing queue item");
            throw;
        }
    }

    /// <summary>
    /// Gets the count of items by status.
    /// </summary>
    public async Task<int> GetQueueCountByStatusAsync(string status)
    {
        return await _queueRepository.GetCountByStatusAsync(status);
    }

    /// <summary>
    /// Processes the next pending item in the queue.
    /// </summary>
    public async Task ProcessNextItemAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ensure only one item is processed at a time
        if (!await _processingSemaphore.WaitAsync(0))
        {
            _logger.LogDebug("Queue processing already in progress");
            return;
        }

        try
        {
            var pendingItems = await _queueRepository.GetByStatusAsync(QueueStatus.Pending);
            var nextItem = pendingItems.FirstOrDefault();

            if (nextItem == null)
            {
                _logger.LogDebug("No pending items in queue");
                return;
            }

            var result = await ProcessQueueItemAsync(nextItem, cancellationToken);

            // Show notification for single item processing.
            // Notification failure must never break processing.
            if (result.success)
            {
                try
                {
                    await _notificationService.ShowSuccessAsync(
                        "Processing Complete",
                        $"Extracted {result.eventCount} event{(result.eventCount != 1 ? "s" : "")}");
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, "Failed to show processing-complete notification");
                }
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    /// <summary>
    /// Processes all pending items in the queue.
    /// </summary>
    public async Task ProcessAllPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await _processingSemaphore.WaitAsync(0))
        {
            _logger.LogWarning("Queue processing already in progress");
            return;
        }

        try
        {
            var pendingItems = await _queueRepository.GetByStatusAsync(QueueStatus.Pending);
            var pendingCount = pendingItems.Count();
            _logger.LogInformation("Processing {Count} pending items", pendingCount);

            var successCount = 0;
            var totalEvents = 0;

            foreach (var item in pendingItems)
            {
                // Between items: a cancelled batch stops here, leaving the
                // remaining items Pending for a later run.
                cancellationToken.ThrowIfCancellationRequested();

                var result = await ProcessQueueItemAsync(item, cancellationToken);
                if (result.success)
                {
                    successCount++;
                    totalEvents += result.eventCount;
                }
            }

            _logger.LogInformation("Finished processing all pending items: {Success}/{Total} succeeded",
                successCount, pendingCount);

            // Show completion notification when the batch finishes.
            // Notification failure must never break processing.
            if (successCount > 0)
            {
                try
                {
                    await _notificationService.ShowProcessingCompleteAsync(successCount, totalEvents);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, "Failed to show batch processing-complete notification");
                }
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    /// <summary>
    /// Processes one specific queue item, e.g. a just-ingested mobile capture.
    /// Unlike ProcessNextItemAsync this WAITS for in-flight processing to finish
    /// instead of returning immediately.
    /// </summary>
    public async Task ProcessCaptureAsync(string queueId, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);

        try
        {
            var item = await _queueRepository.GetByIdAsync(queueId);
            if (item == null)
            {
                _logger.LogWarning("Cannot process capture: queue item {QueueId} not found", queueId);
                return;
            }

            if (item.Status != QueueStatus.Pending && item.Status != QueueStatus.Failed)
            {
                _logger.LogInformation("Skipping queue item {QueueId}: status is {Status}", queueId, item.Status);
                return;
            }

            await ProcessQueueItemAsync(item, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    /// <summary>
    /// Retries a failed queue item.
    /// </summary>
    public async Task RetryFailedItemAsync(string queueId)
    {
        try
        {
            var item = await _queueRepository.GetByIdAsync(queueId);
            if (item == null || item.Status != QueueStatus.Failed)
            {
                _logger.LogWarning("Cannot retry item {QueueId}: not found or not failed", queueId);
                return;
            }

            item.Status = QueueStatus.Pending;
            item.ProcessingStage = QueueProcessingStage.ReadyForTranscription;
            item.ErrorMessage = null;
            item.ProcessedAt = null;

            await _queueRepository.UpdateAsync(item);
            _logger.LogInformation("Retrying failed item: {QueueId}", queueId);

            RaiseStatusChanged(queueId, QueueStatus.Failed, QueueStatus.Pending);

            // A retry moves the capture back out of "failed" — the phone must
            // stop showing the failure it was told about.
            await TryPublishCaptureStatusAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying failed item");
            throw;
        }
    }

    /// <summary>
    /// Clears completed items from the queue.
    /// </summary>
    public async Task ClearCompletedItemsAsync()
    {
        try
        {
            var completedItems = await _queueRepository.GetByStatusAsync(QueueStatus.Completed);
            var count = 0;

            foreach (var item in completedItems)
            {
                await _queueRepository.DeleteAsync(item);
                count++;
                await TryReleaseRecordingAsync(item);
            }

            _logger.LogInformation("Cleared {Count} completed items from queue", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing completed items");
            throw;
        }
    }

    #region Private Methods

    /// <summary>
    /// Releases the recording files a removed row owned. Best-effort: the row
    /// delete has already committed, and a file that would not delete is a
    /// warning for the log and a candidate for the next cache sweep, never a
    /// failed removal for the user.
    /// </summary>
    private async Task TryReleaseRecordingAsync(RecordingQueue removedItem)
    {
        if (_recordingStorage == null)
        {
            return;
        }

        try
        {
            await _recordingStorage.ReleaseAsync(removedItem);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not release the recording files for removed queue item {QueueId}; the cache sweep will collect them",
                removedItem.QueueId);
        }
    }

    /// <summary>
    /// Processes a single queue item with retry logic.
    /// </summary>
    /// <returns>Tuple of (success, eventCount)</returns>
    private async Task<(bool success, int eventCount)> ProcessQueueItemAsync(
        RecordingQueue item, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing queue item: {QueueId}", item.QueueId);

        await UpdateQueueItemStatusAsync(item.QueueId, QueueStatus.Processing,
            processingStage: QueueProcessingStage.Transcribing);

        try
        {
            return await ProcessQueueItemCoreAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failure: put the item back so it can be
            // picked up again, then let the caller observe the cancellation.
            await UpdateQueueItemStatusAsync(item.QueueId, QueueStatus.Pending,
                processingStage: QueueProcessingStage.ReadyForTranscription);
            throw;
        }
    }

    /// <summary>
    /// Retry loop for a single queue item. Cancellation propagates to the caller.
    /// </summary>
    private async Task<(bool success, int eventCount)> ProcessQueueItemCoreAsync(
        RecordingQueue item, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Process recording: transcribe + extract events
                var progress = new Progress<(int percentage, string message)>(p =>
                {
                    RaiseProgressChanged(item.QueueId, p.percentage, p.message);
                });

                var eventCount = await _extractionService.ProcessRecordingAsync(item.QueueId, progress, cancellationToken);

                RaiseProgressChanged(item.QueueId, 100, $"Completed - {eventCount} events extracted");

                // Items that produced pending events are awaiting review; items
                // with nothing to review are simply done.
                await UpdateQueueItemStatusAsync(item.QueueId, QueueStatus.Completed,
                    processingStage: eventCount > 0
                        ? QueueProcessingStage.ReviewReady
                        : QueueProcessingStage.Completed);

                _logger.LogInformation("Successfully processed queue item: {QueueId} - {EventCount} events",
                    item.QueueId, eventCount);

                return (true, eventCount);
            }
            catch (OperationCanceledException)
            {
                // Handled by the outer catch; must not fall into the generic
                // retry path below.
                throw;
            }
            catch (ConfigurationException configEx)
            {
                // Non-retryable: missing/invalid configuration (e.g. no API key).
                // Retrying cannot succeed until the user changes settings.
                _logger.LogError(configEx, "Configuration error processing queue item {QueueId}; not retrying",
                    item.QueueId);

                await UpdateQueueItemStatusAsync(item.QueueId, QueueStatus.Failed, configEx.Message,
                    processingStage: QueueProcessingStage.FailedConfiguration);

                try
                {
                    await _notificationService.ShowErrorAsync("Processing Failed", configEx.Message);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, "Failed to show configuration-error notification");
                }

                return (false, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing queue item {QueueId}, attempt {Attempt}/{MaxRetries}",
                    item.QueueId, attempt, maxRetries);

                if (attempt < maxRetries)
                {
                    // Exponential backoff
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    _logger.LogInformation("Retrying in {Delay}ms...", delay);
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    // Max retries exceeded, mark as failed
                    await UpdateQueueItemStatusAsync(item.QueueId, QueueStatus.Failed, ex.Message,
                        processingStage: QueueProcessingStage.FailedRetryable);
                    _logger.LogError("Failed to process queue item {QueueId} after {MaxRetries} attempts",
                        item.QueueId, maxRetries);

                    // Show error notification; failure to notify must not throw
                    try
                    {
                        await _notificationService.ShowErrorAsync(
                            "Processing Failed",
                            $"Failed to process recording after {maxRetries} attempts");
                    }
                    catch (Exception notifyEx)
                    {
                        _logger.LogWarning(notifyEx, "Failed to show processing-failed notification");
                    }
                }
            }
        }

        return (false, 0);
    }

    /// <summary>
    /// Projects the item's new state toward the device that captured it. The
    /// publisher itself decides what is worth publishing (device captures only,
    /// unchanged projections dropped). Publishing failure must never break
    /// processing — the next transition republishes.
    /// </summary>
    private async Task TryPublishCaptureStatusAsync(RecordingQueue item)
    {
        if (_statusPublisher == null)
        {
            return;
        }

        try
        {
            await _statusPublisher.PublishAsync(item);
        }
        catch (Exception publishEx)
        {
            _logger.LogWarning(publishEx, "Failed to publish capture status for queue item {QueueId}", item.QueueId);
        }
    }

    private void RaiseStatusChanged(string queueId, string oldStatus, string newStatus, string? errorMessage = null)
    {
        QueueItemStatusChanged?.Invoke(this, new QueueItemStatusChangedEventArgs
        {
            QueueId = queueId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            ErrorMessage = errorMessage
        });
    }

    private void RaiseProgressChanged(string queueId, int percentage, string message)
    {
        ProcessingProgressChanged?.Invoke(this, new QueueProcessingProgressEventArgs
        {
            QueueId = queueId,
            ProgressPercentage = percentage,
            StatusMessage = message
        });
    }

    #endregion
}
