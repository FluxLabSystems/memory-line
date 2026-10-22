using MemoryTimeline.Core.Services;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="ISyncBackgroundWorker"/> (design §7.4): a periodic pull → apply →
/// ack → publish-outbox loop. Cycles are serialized by a non-blocking guard —
/// a timer tick that lands while a cycle runs is skipped, while
/// <see cref="SyncNowAsync"/> waits its turn. The cursor only advances past
/// changes that were applied, skipped, or failed permanently; a retryable
/// failure stops the pull so the change is retried next cycle (design §12.1).
/// Safe to start unconditionally at app startup: cycles no-op while sync is
/// disabled or the device is unregistered.
/// </summary>
public class SyncBackgroundWorker : ISyncBackgroundWorker
{
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(30);
    private const int PullPageSize = 100;

    private readonly ISyncClient _syncClient;
    private readonly IRemoteChangeApplier _changeApplier;
    private readonly ILocalOutboxPublisher _outboxPublisher;
    private readonly ISyncCursorStore _cursorStore;
    private readonly ISyncSettingsStore _settingsStore;
    private readonly ITimelineProjectionPublisher? _projectionPublisher;
    private readonly ILogger<SyncBackgroundWorker> _logger;

    private readonly SemaphoreSlim _cycleGuard = new(1, 1);
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private DateTime? _lastSuccessAtUtc;

    /// <summary>
    /// Creates the worker. <paramref name="projectionPublisher"/> is optional
    /// and trailing so existing constructions keep compiling; without it an
    /// owed backfill stays owed (and logged) rather than failing the cycle.
    /// </summary>
    public SyncBackgroundWorker(
        ISyncClient syncClient,
        IRemoteChangeApplier changeApplier,
        ILocalOutboxPublisher outboxPublisher,
        ISyncCursorStore cursorStore,
        ISyncSettingsStore settingsStore,
        ILogger<SyncBackgroundWorker> logger,
        ITimelineProjectionPublisher? projectionPublisher = null)
    {
        _syncClient = syncClient;
        _changeApplier = changeApplier;
        _outboxPublisher = outboxPublisher;
        _cursorStore = cursorStore;
        _settingsStore = settingsStore;
        _projectionPublisher = projectionPublisher;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<SyncStatusChangedEventArgs>? SyncStatusChanged;

    /// <inheritdoc />
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _loopTask = Task.Run(() => RunLoopAsync(cts.Token), CancellationToken.None);
            _logger.LogInformation("Sync background worker started");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Task? loopTask;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            cts = _loopCts;
            loopTask = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        if (cts == null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            if (loopTask != null)
            {
                await loopTask.ConfigureAwait(false);
            }
        }
        finally
        {
            cts.Dispose();
        }

        _logger.LogInformation("Sync background worker stopped");
    }

    /// <inheritdoc />
    public async Task<SyncCycleResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        // Unlike a timer tick, an explicit sync waits for any in-flight cycle
        // instead of skipping.
        await _cycleGuard.WaitAsync(cancellationToken);
        try
        {
            return await RunCycleAsync(cancellationToken);
        }
        finally
        {
            _cycleGuard.Release();
        }
    }

    #region Private Methods

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CycleInterval);
        try
        {
            // Immediate first cycle so a fresh registration syncs right away.
            await RunGuardedCycleAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunGuardedCycleAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via StopAsync.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync background loop terminated unexpectedly");
        }
    }

    private async Task RunGuardedCycleAsync(CancellationToken cancellationToken)
    {
        if (!await _cycleGuard.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("A sync cycle is already running; skipping this tick");
            return;
        }

        try
        {
            await RunCycleAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // RunCycleAsync converts failures into the cycle result; this guard
            // only exists so an unexpected bug cannot kill the periodic loop.
            _logger.LogError(ex, "Sync cycle threw unexpectedly");
        }
        finally
        {
            _cycleGuard.Release();
        }
    }

    private async Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!await IsReadyAsync())
        {
            return new SyncCycleResult { Ran = false };
        }

        RaiseStatusChanged(isSyncing: true, statusText: "Syncing...", lastError: null);

        var applied = 0;
        var failedPermanent = 0;
        var published = 0;
        var backfilled = 0;
        var retryableStop = false;
        string? error = null;
        long cursor = 0;

        try
        {
            cursor = await _cursorStore.GetCursorAsync();

            bool hasMore;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await _syncClient.PullChangesAsync(cursor, PullPageSize, cancellationToken);
                hasMore = page.HasMore;

                foreach (var change in page.Changes)
                {
                    var result = await _changeApplier.ApplyAsync(change, cancellationToken);

                    if (result == ChangeApplicationResult.FailedRetryable)
                    {
                        // Do not advance past a retryable failure — the change is
                        // pulled and retried next cycle.
                        _logger.LogWarning(
                            "Change {ChangeId} ({EntityType}) failed retryably; stopping this pull at cursor {Cursor}",
                            change.ChangeId, change.EntityType, cursor);
                        retryableStop = true;
                        break;
                    }

                    if (result == ChangeApplicationResult.FailedPermanent)
                    {
                        failedPermanent++;
                        _logger.LogError(
                            "Change {ChangeId} ({EntityType}, entity {EntityId}) is permanently unappliable and was SKIPPED; the cursor advances past it",
                            change.ChangeId, change.EntityType, change.EntityId);
                    }
                    else if (result == ChangeApplicationResult.Applied)
                    {
                        applied++;
                    }

                    cursor = change.ChangeId;
                    await _cursorStore.SetCursorAsync(cursor);
                }

                if (page.Changes.Count == 0 && hasMore)
                {
                    _logger.LogWarning("Sync pull returned no changes but signalled more; stopping to avoid a spin");
                    hasMore = false;
                }
            }
            while (hasMore && !retryableStop);

            // Best-effort server-side ack (design §12.1: the local cursor is
            // authoritative, the ack is diagnostic only).
            try
            {
                await _syncClient.AckAsync(cursor, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync ack at cursor {Cursor} failed; the local cursor is authoritative", cursor);
            }

            // An owed backfill (new registration, restored archive) is queued
            // BEFORE the drain, so its rows ride this same cycle's push.
            backfilled = await RunOwedBackfillAsync(cancellationToken);

            published = await _outboxPublisher.PublishPendingAsync(cancellationToken);

            if (retryableStop)
            {
                error = "Some changes could not be applied and will be retried.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncApiException ex)
        {
            _logger.LogError(ex, "Sync cycle failed at cursor {Cursor}", cursor);
            error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync cycle failed unexpectedly at cursor {Cursor}", cursor);
            error = $"Sync failed: {ex.Message}";
        }

        if (error == null)
        {
            _lastSuccessAtUtc = DateTime.UtcNow;
        }

        var statusText = error ?? $"Synced {applied} changes at {DateTime.Now:HH:mm}";
        RaiseStatusChanged(isSyncing: false, statusText, error);

        return new SyncCycleResult
        {
            Ran = true,
            ChangesApplied = applied,
            ChangesFailedPermanent = failedPermanent,
            OutboxPublished = published,
            ProjectionsBackfilled = backfilled,
            Cursor = cursor,
            Error = error,
        };
    }

    /// <summary>
    /// Runs the full projection backfill when one is owed and clears the flag
    /// only once it has completed, so a run that fails or is cancelled midway
    /// is simply retried next cycle (each row it did queue is deduplicated
    /// against the outbox, so the retry costs nothing twice). A failure is
    /// logged and does not fail the cycle: the drain that follows still pushes
    /// whatever was queued.
    /// </summary>
    private async Task<int> RunOwedBackfillAsync(CancellationToken cancellationToken)
    {
        if (!await _settingsStore.IsProjectionBackfillPendingAsync())
        {
            return 0;
        }

        if (_projectionPublisher == null)
        {
            _logger.LogWarning(
                "A timeline projection backfill is owed but no projection publisher is registered; companions will not receive the existing archive");
            return 0;
        }

        try
        {
            _logger.LogInformation("Running the owed timeline projection backfill");
            var result = await _projectionPublisher.PublishAllAsync(
                TimelineProjectionBackfillMode.Everything, cancellationToken);
            await _settingsStore.SetProjectionBackfillPendingAsync(false);
            return result.Published;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Timeline projection backfill failed; it stays owed and is retried next cycle");
            return 0;
        }
    }

    private async Task<bool> IsReadyAsync()
    {
        if (!await _settingsStore.IsSyncEnabledAsync())
        {
            _logger.LogDebug("Sync is disabled; skipping cycle");
            return false;
        }

        if (string.IsNullOrWhiteSpace(await _settingsStore.GetServerUrlAsync()) ||
            string.IsNullOrWhiteSpace(await _settingsStore.GetDeviceIdAsync()))
        {
            _logger.LogDebug("Sync is enabled but the device is not registered; skipping cycle");
            return false;
        }

        return true;
    }

    private void RaiseStatusChanged(bool isSyncing, string statusText, string? lastError)
    {
        var handler = SyncStatusChanged;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(this, new SyncStatusChangedEventArgs
            {
                IsSyncing = isSyncing,
                LastSuccessAtUtc = _lastSuccessAtUtc,
                StatusText = statusText,
                LastError = lastError,
            });
        }
        catch (Exception ex)
        {
            // A faulty UI subscriber must not break the sync loop.
            _logger.LogError(ex, "A sync status event subscriber threw");
        }
    }

    #endregion
}
