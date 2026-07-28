using InputExecutor.Core.Protocol;

namespace InputExecutor.Core.Abstractions;

public interface IMonotonicClock
{
    long NowMicroseconds { get; }
}

public sealed class SystemMonotonicClock : IMonotonicClock
{
    public long NowMicroseconds => Environment.TickCount64 * 1_000;
}

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}

public interface IInputBackend
{
    string Name { get; }
    bool SupportsKey(string code);
    ValueTask<EmittedInput> MoveMouseRelativeAsync(int dx, int dy, bool noCoalesce, CancellationToken cancellationToken);
    ValueTask<EmittedInput> MoveMouseAbsoluteAsync(int x, int y, CancellationToken cancellationToken);
    ValueTask<EmittedInput> SetMouseButtonAsync(string button, bool isDown, CancellationToken cancellationToken);
    ValueTask<EmittedInput> ScrollMouseAsync(int deltaX, int deltaY, CancellationToken cancellationToken);
    ValueTask<EmittedInput> SetKeyAsync(string code, bool isDown, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<EmittedInput>> TypeTextAsync(string text, CancellationToken cancellationToken);
}

public sealed record TargetCheck(bool IsValid, string? ErrorCode = null, string? Message = null);

public interface ITargetGuard
{
    TargetBinding CaptureForeground();
    TargetBinding CaptureProcess(int processId);
    bool TryActivate(TargetBinding target);
    TargetCheck Validate(TargetBinding target, bool requireForeground);
}

public interface ITrajectorySink : IAsyncDisposable
{
    ValueTask WriteAsync(TrajectoryRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Optional high-throughput trajectory contract. Recorders use this when available so
/// many high-polling-rate input packets share one serialized disk flush without being dropped.
/// </summary>
public interface IBatchedTrajectorySink : ITrajectorySink
{
    ValueTask WriteBatchAsync(IReadOnlyList<TrajectoryRecord> records, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
}

public interface IWindowScreenshotCapture
{
    ScreenshotFrame Capture(TargetBinding target);
}

public interface IHumanInputRecorder : IAsyncDisposable
{
    RecordingStatus Status { get; }
    RecordingStatus Start(string sessionId, TargetBinding target, bool includeExecutorEvents);
    Task<RecordingStatus> StopAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class NullTrajectorySink : ITrajectorySink
{
    public ValueTask WriteAsync(TrajectoryRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
