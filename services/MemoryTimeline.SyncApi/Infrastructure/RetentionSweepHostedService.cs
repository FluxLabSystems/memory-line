using MemoryTimeline.SyncApi.Application;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// Runs <see cref="IArtifactRetentionService.SweepAsync"/> at startup and then
/// every <see cref="SyncApiOptions.RetentionSweepIntervalMinutes"/>. A failed
/// sweep is logged and retried on the next tick; it never takes the host down,
/// because the service is more useful serving uploads with a full disk than
/// stopped with a tidy one.
/// </summary>
public sealed class RetentionSweepHostedService : BackgroundService
{
    private readonly IArtifactRetentionService _retention;
    private readonly SyncApiOptions _options;
    private readonly ILogger<RetentionSweepHostedService> _logger;

    public RetentionSweepHostedService(
        IArtifactRetentionService retention,
        IOptions<SyncApiOptions> options,
        ILogger<RetentionSweepHostedService> logger)
    {
        _retention = retention;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_options.RetentionSweepIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _retention.SweepAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention sweep failed; the next attempt is in {Interval}.", interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
