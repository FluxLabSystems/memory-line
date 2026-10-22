using MemoryTimeline.SyncApi.Application;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// Runs <see cref="IAssistantSessionReaper.ReapAsync"/> at startup and then
/// every <see cref="SyncApiOptions.AssistantReaperIntervalMinutes"/>. A failed
/// reap is logged and retried on the next tick; it never takes the host down.
/// </summary>
public sealed class AssistantSessionReaperHostedService : BackgroundService
{
    private readonly IAssistantSessionReaper _reaper;
    private readonly SyncApiOptions _options;
    private readonly ILogger<AssistantSessionReaperHostedService> _logger;

    public AssistantSessionReaperHostedService(
        IAssistantSessionReaper reaper,
        IOptions<SyncApiOptions> options,
        ILogger<AssistantSessionReaperHostedService> logger)
    {
        _reaper = reaper;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_options.AssistantReaperIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _reaper.ReapAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assistant session reap failed; the next attempt is in {Interval}.", interval);
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
