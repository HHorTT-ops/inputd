using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Service;

/// <summary>
/// Subscribes the singleton Raw Input recorder to the physical-mouse SSE hub for the host lifetime.
/// Does not Start Recording and does not write trajectories.
/// </summary>
public sealed class PhysicalMouseSseHostedService : IHostedService, IDisposable
{
    private readonly IPhysicalInputObservationSource _source;
    private readonly PhysicalMouseEventHub _hub;
    private readonly IMonotonicClock _clock;
    private readonly CancellationTokenSource _heartbeat = new();
    private Task? _heartbeatTask;
    private int _started;

    public PhysicalMouseSseHostedService(
        IPhysicalInputObservationSource source,
        PhysicalMouseEventHub hub,
        IMonotonicClock clock)
    {
        _source = source;
        _hub = hub;
        _clock = clock;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;
        _source.FrameObserved += OnFrameObserved;
        _source.TaggedRawFrameObserved += OnTaggedRawFrameObserved;
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_heartbeat.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;
        _source.FrameObserved -= OnFrameObserved;
        _source.TaggedRawFrameObserved -= OnTaggedRawFrameObserved;
        _heartbeat.Cancel();
        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _hub.Dispose();
    }

    public void Dispose()
    {
        _heartbeat.Cancel();
        _heartbeat.Dispose();
    }

    private void OnFrameObserved(object? sender, HumanInputFrame frame) => _hub.PublishFrame(frame);

    private void OnTaggedRawFrameObserved(object? sender, HumanInputFrame frame) => _hub.ObserveTaggedFrame();

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            _hub.PublishHeartbeat(_clock.NowMicroseconds);
    }
}
