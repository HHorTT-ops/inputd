using InputExecutor.Scheduler;

namespace InputExecutor.Service;

public sealed class WatchdogWorker : BackgroundService
{
    private readonly InputCoordinator _coordinator;
    private readonly ILogger<WatchdogWorker> _logger;

    public WatchdogWorker(InputCoordinator coordinator, ILogger<WatchdogWorker> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await _coordinator.InspectWatchdogsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Input watchdog inspection failed"); }
        }
    }
}
