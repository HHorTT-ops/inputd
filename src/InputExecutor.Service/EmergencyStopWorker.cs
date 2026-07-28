using InputExecutor.Scheduler;
using InputExecutor.Windows;

namespace InputExecutor.Service;

public sealed class EmergencyStopWorker : IHostedService, IDisposable
{
    private readonly InputCoordinator _coordinator;
    private readonly ILogger<EmergencyStopWorker> _logger;
    private EmergencyHotkeyListener? _listener;

    public EmergencyStopWorker(InputCoordinator coordinator, ILogger<EmergencyStopWorker> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new EmergencyHotkeyListener(() =>
        {
            _logger.LogWarning("Emergency stop hotkey triggered");
            _ = _coordinator.EmergencyStopAsync();
        });
        if (!_listener.RegistrationSucceeded)
            _logger.LogError("Could not register Ctrl+Alt+F12 emergency hotkey: {Failure}", _listener.Failure);
        else
            _logger.LogInformation("Emergency stop hotkey registered: Ctrl+Alt+F12");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _coordinator.EmergencyStopAsync("service_stopping");
        _listener?.Dispose();
    }

    public void Dispose() => _listener?.Dispose();
}
