using System.Text.Json;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="ICaptureStatusPublisher"/> (design §19 Phase 3): projects a queue
/// item's processing state into a <c>capture_status</c> outbox row, which
/// <see cref="LocalOutboxPublisher"/> pushes to the sync service and the
/// originating phone pulls. Windows is the only writer of this entity type.
///
/// Two rules keep the stream honest:
/// <list type="bullet">
/// <item>only captures that came from another device are published — a
/// Windows-local recording has no phone waiting on it;</item>
/// <item>a projection identical to the last published one is dropped, so the
/// pipeline can call this after every transition without producing noise.</item>
/// </list>
///
/// Privacy (§14.5): the payload carries a bounded transcript preview and never
/// a file path, credential, or raw exception text; failure reasons come from a
/// fixed catalog keyed by the failure stage rather than from
/// <see cref="RecordingQueue.ErrorMessage"/>, whose contents are unbounded.
/// Nothing here logs transcript text.
/// </summary>
public class CaptureStatusPublisher : ICaptureStatusPublisher
{
    private readonly ISyncOutboxRepository _outboxRepository;
    private readonly IPendingEventRepository _pendingEventRepository;
    private readonly ILogger<CaptureStatusPublisher> _logger;

    public CaptureStatusPublisher(
        ISyncOutboxRepository outboxRepository,
        IPendingEventRepository pendingEventRepository,
        ILogger<CaptureStatusPublisher> logger)
    {
        _outboxRepository = outboxRepository;
        _pendingEventRepository = pendingEventRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(RecordingQueue queueItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queueItem);

        if (!IsDeviceCapture(queueItem))
        {
            _logger.LogDebug(
                "Queue item {QueueId} is not a device capture; no status is published", queueItem.QueueId);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var captureId = queueItem.SourceCaptureId!;
        var payload = await BuildPayloadAsync(queueItem, captureId, cancellationToken);

        var previous = await ReadLastPublishedAsync(captureId);
        if (previous != null && IsUnchanged(previous, payload))
        {
            _logger.LogDebug(
                "Capture {CaptureId} status {Status}/{Stage} is unchanged; not republishing",
                captureId, payload.Status, payload.ProcessingStage);
            return;
        }

        await _outboxRepository.AddAsync(new SyncOutbox
        {
            EntityType = SyncEntityType.CaptureStatus,
            EntityId = captureId,
            Operation = SyncOutboxOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(payload, SyncJson.Options),
            CreatedAt = payload.UpdatedAtUtc,
        }, cancellationToken);

        _logger.LogInformation(
            "Published capture status {Status} ({Stage}) for capture {CaptureId} from queue item {QueueId}",
            payload.Status, payload.ProcessingStage ?? "-", captureId, queueItem.QueueId);
    }

    #region Private Methods

    /// <summary>
    /// A capture is worth publishing only when it came from another device.
    /// Ingestion stamps remote captures with both a device ID and a non-Windows
    /// platform (design §7.1/§7.2); Windows-local recordings and text sources
    /// carry neither, so either signal is enough to recognize one.
    /// </summary>
    private static bool IsDeviceCapture(RecordingQueue queueItem) =>
        !string.IsNullOrWhiteSpace(queueItem.SourceCaptureId) &&
        (!string.IsNullOrWhiteSpace(queueItem.SourceDeviceId) ||
         !string.Equals(queueItem.SourcePlatform, CapturePlatform.Windows, StringComparison.OrdinalIgnoreCase));

    private async Task<CaptureStatusChangePayload> BuildPayloadAsync(
        RecordingQueue queueItem, string captureId, CancellationToken cancellationToken)
    {
        var status = MapStatus(queueItem);
        var transcript = queueItem.Transcript;
        var hasTranscript = !string.IsNullOrWhiteSpace(transcript);

        var payload = new CaptureStatusChangePayload
        {
            CaptureId = captureId,
            Status = status,
            ProcessingStage = queueItem.ProcessingStage,
            UpdatedAtUtc = DateTime.UtcNow,
            TranscriptAvailable = hasTranscript,
            TranscriptPreview = hasTranscript ? BuildPreview(transcript!) : null,
            TranscriptCharCount = hasTranscript ? transcript!.Length : null,
            FailureReason = status == SyncCaptureStatus.Failed ? FailureReasonFor(queueItem.ProcessingStage) : null,
            FailureRetryable = status == SyncCaptureStatus.Failed ? RetryableFor(queueItem.ProcessingStage) : null,
        };

        // Review counts exist only once extraction has run; before that they
        // would be a guess, and the contract prefers null over an invented zero.
        if (status is SyncCaptureStatus.ReviewReady or SyncCaptureStatus.Completed)
        {
            var (pending, approved) = await CountReviewStateAsync(queueItem.QueueId, cancellationToken);
            payload.PendingEventCount = pending;
            payload.ApprovedEventCount = approved;
        }

        return payload;
    }

    /// <summary>
    /// Maps the queue's stage vocabulary (design §7.3) onto the coarser
    /// <see cref="SyncCaptureStatus"/> the phone displays. Falls back to the
    /// four-state status column for rows written before stages existed.
    /// </summary>
    private static string MapStatus(RecordingQueue queueItem) => queueItem.ProcessingStage switch
    {
        QueueProcessingStage.PendingDownload => SyncCaptureStatus.Received,
        QueueProcessingStage.Verifying => SyncCaptureStatus.Received,
        QueueProcessingStage.ReadyForTranscription => SyncCaptureStatus.Received,
        QueueProcessingStage.Transcribing => SyncCaptureStatus.Processing,
        QueueProcessingStage.Extracting => SyncCaptureStatus.Processing,
        QueueProcessingStage.ReviewReady => SyncCaptureStatus.ReviewReady,
        QueueProcessingStage.Completed => SyncCaptureStatus.Completed,
        QueueProcessingStage.FailedRetryable => SyncCaptureStatus.Failed,
        QueueProcessingStage.FailedConfiguration => SyncCaptureStatus.Failed,
        QueueProcessingStage.FailedPermanent => SyncCaptureStatus.Failed,
        _ => queueItem.Status switch
        {
            QueueStatus.Processing => SyncCaptureStatus.Processing,
            QueueStatus.Completed => SyncCaptureStatus.Completed,
            QueueStatus.Failed => SyncCaptureStatus.Failed,
            _ => SyncCaptureStatus.Received,
        },
    };

    /// <summary>
    /// Fixed user-facing failure text. Deliberately NOT the queue's error
    /// message: that is raw exception text and may contain file paths or
    /// provider detail the phone must never receive (§14.5).
    /// </summary>
    private static string FailureReasonFor(string? stage) => stage switch
    {
        QueueProcessingStage.FailedConfiguration =>
            "Windows needs to be configured before this capture can be processed.",
        QueueProcessingStage.FailedPermanent =>
            "This capture could not be processed.",
        _ => "Processing failed on the Windows machine; it can be retried there.",
    };

    /// <summary>
    /// Retry classification (§16.2). Null when the stage does not say — a
    /// pre-stage row that only knows status=failed is not evidence either way.
    /// </summary>
    private static bool? RetryableFor(string? stage) => stage switch
    {
        QueueProcessingStage.FailedRetryable => true,
        QueueProcessingStage.FailedConfiguration => false,
        QueueProcessingStage.FailedPermanent => false,
        _ => null,
    };

    /// <summary>
    /// Leading excerpt of at most
    /// <see cref="CaptureStatusChangePayload.TranscriptPreviewMaxChars"/>
    /// characters. Cuts one character short rather than splitting a surrogate
    /// pair, so the preview is always well-formed text.
    /// </summary>
    private static string BuildPreview(string transcript)
    {
        const int max = CaptureStatusChangePayload.TranscriptPreviewMaxChars;
        if (transcript.Length <= max)
        {
            return transcript;
        }

        var length = char.IsHighSurrogate(transcript[max - 1]) ? max - 1 : max;
        return transcript[..length];
    }

    private async Task<(int Pending, int Approved)> CountReviewStateAsync(
        string queueId, CancellationToken cancellationToken)
    {
        try
        {
            var pendingEvents = await _pendingEventRepository.GetByQueueIdAsync(queueId, cancellationToken);

            var pending = 0;
            var approved = 0;
            foreach (var pendingEvent in pendingEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (PendingStatusExtensions.FromString(pendingEvent.Status))
                {
                    case PendingStatus.PendingReview:
                        pending++;
                        break;
                    case PendingStatus.Approved:
                        approved++;
                        break;
                }
            }

            return (pending, approved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Counts are decoration on the status, never the reason to lose it.
            _logger.LogWarning(ex, "Could not count review state for queue item {QueueId}", queueId);
            return (0, 0);
        }
    }

    private async Task<CaptureStatusChangePayload?> ReadLastPublishedAsync(string captureId)
    {
        var latest = await _outboxRepository.GetLatestForEntityAsync(SyncEntityType.CaptureStatus, captureId);
        if (string.IsNullOrWhiteSpace(latest?.PayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CaptureStatusChangePayload>(latest.PayloadJson, SyncJson.Options);
        }
        catch (JsonException ex)
        {
            // An unreadable previous payload cannot prove anything is unchanged;
            // publishing again is the safe direction.
            _logger.LogWarning(
                ex, "Last published status for capture {CaptureId} could not be read; publishing anyway", captureId);
            return null;
        }
    }

    /// <summary>
    /// Compares everything except <see cref="CaptureStatusChangePayload.UpdatedAtUtc"/>,
    /// which moves on every call. Status and stage dominate in practice; the
    /// remaining fields are compared so a real change (a transcript appearing,
    /// an event being approved) still reaches the phone at an unchanged stage.
    /// </summary>
    private static bool IsUnchanged(CaptureStatusChangePayload previous, CaptureStatusChangePayload next) =>
        previous.Status == next.Status &&
        previous.ProcessingStage == next.ProcessingStage &&
        previous.TranscriptAvailable == next.TranscriptAvailable &&
        previous.TranscriptCharCount == next.TranscriptCharCount &&
        previous.TranscriptPreview == next.TranscriptPreview &&
        previous.PendingEventCount == next.PendingEventCount &&
        previous.ApprovedEventCount == next.ApprovedEventCount &&
        previous.FailureReason == next.FailureReason &&
        previous.FailureRetryable == next.FailureRetryable;

    #endregion
}
