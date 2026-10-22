using MemoryTimeline.Data.Repositories;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="ILocalOutboxPublisher"/> (design §7.4/§7.6): drains the local
/// sync_outbox in strict outbox_id order via <c>POST /api/v1/sync/push</c>.
/// Accepted and duplicate entries are marked delivered (the server's
/// client-sequence idempotency makes replays safe); per-entry rejections are
/// recorded without consuming the record so they stay visible and retryable.
/// </summary>
public class LocalOutboxPublisher : ILocalOutboxPublisher
{
    private const int BatchSize = 100;

    private readonly ISyncOutboxRepository _outboxRepository;
    private readonly ISyncClient _syncClient;
    private readonly ISyncSettingsStore _settingsStore;
    private readonly ILogger<LocalOutboxPublisher> _logger;

    public LocalOutboxPublisher(
        ISyncOutboxRepository outboxRepository,
        ISyncClient syncClient,
        ISyncSettingsStore settingsStore,
        ILogger<LocalOutboxPublisher> logger)
    {
        _outboxRepository = outboxRepository;
        _syncClient = syncClient;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!await _settingsStore.IsSyncEnabledAsync())
        {
            _logger.LogDebug("Sync is disabled; not publishing the outbox");
            return 0;
        }

        var delivered = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = await _outboxRepository.GetPendingAsync(BatchSize, cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                var request = new SyncPushRequest
                {
                    Entries = batch.Select(entry => new SyncPushEntry
                    {
                        // The outbox ID doubles as the per-device idempotency
                        // sequence (design §12.1: local outbox sequence).
                        ClientSequence = entry.OutboxId,
                        EntityType = entry.EntityType,
                        EntityId = entry.EntityId,
                        Operation = entry.Operation,
                        PayloadJson = entry.PayloadJson,
                        ChangedAtUtc = entry.CreatedAt,
                    }).ToList(),
                };

                var response = await _syncClient.PushChangesAsync(request, cancellationToken);

                var resultsBySequence = new Dictionary<long, SyncPushEntryResult>();
                foreach (var result in response.Results)
                {
                    resultsBySequence[result.ClientSequence] = result;
                }

                var fullyDelivered = true;
                foreach (var entry in batch)
                {
                    if (resultsBySequence.TryGetValue(entry.OutboxId, out var result) &&
                        (result.Accepted || result.Duplicate))
                    {
                        await _outboxRepository.MarkDeliveredAsync(entry.OutboxId, cancellationToken);
                        delivered++;
                    }
                    else
                    {
                        var error = result?.Error ?? "The sync service returned no result for this entry.";
                        await _outboxRepository.RecordFailedAttemptAsync(entry.OutboxId, error, cancellationToken);
                        _logger.LogWarning(
                            "Outbox entry {OutboxId} ({EntityType} {EntityId}) was rejected by the sync service: {Error}",
                            entry.OutboxId, entry.EntityType, entry.EntityId, error);
                        fullyDelivered = false;
                    }
                }

                // Continue only when a FULL batch went through completely —
                // otherwise we either drained the queue or must not spin on
                // rejected entries.
                if (!fullyDelivered || batch.Count < BatchSize)
                {
                    break;
                }
            }
        }
        catch (SyncApiException ex)
        {
            // The remaining entries stay pending and are retried next cycle; when
            // the service is down the worker's pull failure surfaces the outage
            // in its own cycle result.
            _logger.LogWarning(
                ex,
                "Outbox publishing aborted after {Delivered} deliveries (retryable: {Retryable})",
                delivered, ex.Retryable);
        }

        if (delivered > 0)
        {
            _logger.LogInformation("Published {Delivered} outbox entries to the sync service", delivered);
        }

        return delivered;
    }
}
