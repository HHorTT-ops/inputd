using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Scheduler;

public sealed record InputSchedulerOptions(
    int CommandQueueCapacity = 64,
    int MaxRealtimeBurst = 8,
    int ReplayQueueCapacity = 2,
    ActionExecutorOptions? ActionOptions = null);

public sealed record InputSchedulerSnapshot(
    int SchedulerThreadId,
    IReadOnlyList<string> HeldKeys,
    IReadOnlyList<string> HeldMouseButtons,
    int PendingCommands,
    bool HasPendingRealtime,
    string? ActiveWorkKind,
    bool Running);

public sealed class InputScheduler : IAsyncDisposable
{
    private interface IWorkItem
    {
        string Kind { get; }
        CancellationToken CancellationToken { get; }
        void Execute(InputScheduler scheduler, CancellationToken cancellationToken);
        void Cancel(Exception exception);
    }

    private sealed class CommandWork : IWorkItem
    {
        private readonly TaskCompletionSource<ExecutionResult> _completion = NewCompletion();
        public required CommandRequest Request { get; init; }
        public required Action ValidateTarget { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Task<ExecutionResult> Task => _completion.Task;
        public string Kind => "command";

        public void Execute(InputScheduler scheduler, CancellationToken cancellationToken)
        {
            if (Request.DeadlineUs is not null && scheduler._clock.NowMicroseconds > Request.DeadlineUs)
            {
                _completion.TrySetResult(scheduler.Dropped(Request.Sequence, "expired"));
                return;
            }
            var events = scheduler._executor.ExecuteAsync(Request.Actions, _ =>
            {
                ValidateTarget();
                return ValueTask.CompletedTask;
            }, cancellationToken).GetAwaiter().GetResult();
            scheduler.PublishSnapshotFromSchedulerThread();
            _completion.TrySetResult(scheduler.Executed(Request.Sequence, events));
        }

        public void Cancel(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class RealtimeWork : IWorkItem
    {
        private readonly TaskCompletionSource<ExecutionResult> _completion = NewCompletion();
        public required RealtimeFrame Frame { get; init; }
        public required Action ValidateTarget { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Task<ExecutionResult> Task => _completion.Task;
        public string Kind => "realtime";

        public void Execute(InputScheduler scheduler, CancellationToken cancellationToken)
        {
            if (Frame.DeadlineUs is not null && scheduler._clock.NowMicroseconds > Frame.DeadlineUs)
            {
                _completion.TrySetResult(scheduler.Dropped(Frame.Sequence, "expired"));
                return;
            }
            ValidateTarget();
            var events = scheduler._state.ApplyRealtimeAsync(scheduler._backend, Frame, true, cancellationToken).GetAwaiter().GetResult();
            scheduler.PublishSnapshotFromSchedulerThread();
            _completion.TrySetResult(scheduler.Executed(Frame.Sequence, events));
        }

        public void Supersede(InputScheduler scheduler) => _completion.TrySetResult(scheduler.Dropped(Frame.Sequence, "superseded"));
        public void Cancel(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class ReplayWork : IWorkItem
    {
        private readonly TaskCompletionSource<ExecutionResult> _completion = NewCompletion();
        public required RealtimeFrame Frame { get; init; }
        public required IReadOnlyCollection<string> RepeatedKeyDowns { get; init; }
        public required Action ValidateTarget { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Task<ExecutionResult> Task => _completion.Task;
        public string Kind => "replay";

        public void Execute(InputScheduler scheduler, CancellationToken cancellationToken)
        {
            if (Frame.DeadlineUs is not null && scheduler._clock.NowMicroseconds > Frame.DeadlineUs)
            {
                _completion.TrySetResult(scheduler.Dropped(Frame.Sequence, "expired"));
                return;
            }
            ValidateTarget();
            var events = scheduler._state.ApplyRealtimeAsync(
                scheduler._backend, Frame, true, cancellationToken, RepeatedKeyDowns).GetAwaiter().GetResult();
            scheduler.PublishSnapshotFromSchedulerThread();
            _completion.TrySetResult(scheduler.Executed(Frame.Sequence, events));
        }

        public void Cancel(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class ControlWork : IWorkItem
    {
        private readonly TaskCompletionSource<bool> _completion = NewCompletion<bool>();
        public required Action<InputScheduler> Operation { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Task Task => _completion.Task;
        public string Kind => "control";
        public void Execute(InputScheduler scheduler, CancellationToken cancellationToken)
        {
            Operation(scheduler);
            _completion.TrySetResult(true);
        }
        public void Cancel(Exception exception) => _completion.TrySetException(exception);
    }

    private readonly object _sync = new();
    private readonly Queue<IWorkItem> _controlQueue = new();
    private readonly Queue<CommandWork> _commandQueue = new();
    private readonly Queue<ReplayWork> _replayQueue = new();
    private RealtimeWork? _realtimeSlot;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly IInputBackend _backend;
    private readonly IMonotonicClock _clock;
    private readonly InputStateMachine _state = new();
    private readonly ActionExecutor _executor;
    private readonly InputSchedulerOptions _options;
    private CancellationTokenSource? _activeWorkCancellation;
    private volatile InputSchedulerSnapshot _snapshot = new(0, Array.Empty<string>(), Array.Empty<string>(), 0, false, null, true);
    private bool _accepting = true;
    private bool _shutdownRequested;
    private int _realtimeBurst;
    private string? _activeWorkKind;

    public InputScheduler(IInputBackend backend, IMonotonicClock? clock = null, InputSchedulerOptions? options = null)
    {
        _backend = backend;
        _clock = clock ?? new SystemMonotonicClock();
        _options = options ?? new InputSchedulerOptions();
        if (_options.CommandQueueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(options), "Command queue capacity must be 1..4096.");
        if (_options.MaxRealtimeBurst is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(options), "Realtime burst must be 1..1024.");
        if (_options.ReplayQueueCapacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options), "Replay queue capacity must be 1..64.");
        _executor = new ActionExecutor(_backend, _state, new SchedulerBlockingDelay(), _options.ActionOptions);
        _thread = new Thread(Run) { IsBackground = false, Name = "inputd-input-scheduler" };
        _thread.Start();
    }

    public InputSchedulerSnapshot Snapshot => _snapshot;
    public int SchedulerThreadId => _snapshot.SchedulerThreadId;
    public string BackendName => _backend.Name;
    public bool SupportsKey(string code) => _backend.SupportsKey(code);

    public Task<ExecutionResult> EnqueueCommandAsync(CommandRequest request, Action validateTarget, CancellationToken cancellationToken)
    {
        _executor.Validate(request.Actions);
        var work = new CommandWork { Request = request, ValidateTarget = validateTarget, CancellationToken = cancellationToken };
        lock (_sync)
        {
            EnsureAccepting();
            if (_commandQueue.Count >= _options.CommandQueueCapacity)
                throw new InputExecutorException("COMMAND_QUEUE_FULL", "The bounded command FIFO is full.", 429);
            _commandQueue.Enqueue(work);
            PublishSnapshotLocked();
        }
        _wake.Set();
        return work.Task;
    }

    public Task<ExecutionResult> EnqueueRealtimeAsync(RealtimeFrame frame, Action validateTarget, CancellationToken cancellationToken)
    {
        foreach (var key in frame.Keyboard.Held)
            if (!_backend.SupportsKey(key)) throw new InputExecutorException("UNSUPPORTED_KEY", $"Unsupported physical key code '{key}'.");

        var work = new RealtimeWork { Frame = frame, ValidateTarget = validateTarget, CancellationToken = cancellationToken };
        lock (_sync)
        {
            EnsureAccepting();
            _realtimeSlot?.Supersede(this);
            _realtimeSlot = work;
            PublishSnapshotLocked();
        }
        _wake.Set();
        return work.Task;
    }

    /// <summary>
    /// Enqueues a lossless recorded state frame. Unlike the live realtime slot this FIFO is
    /// never latest-wins, because superseding a recorded edge would corrupt the trajectory.
    /// </summary>
    public Task<ExecutionResult> EnqueueReplayAsync(
        RealtimeFrame frame,
        IReadOnlyCollection<string> repeatedKeyDowns,
        Action validateTarget,
        CancellationToken cancellationToken)
    {
        foreach (var key in frame.Keyboard.Held.Concat(repeatedKeyDowns))
            if (!_backend.SupportsKey(key)) throw new InputExecutorException("UNSUPPORTED_KEY", $"Unsupported physical key code '{key}'.");

        var work = new ReplayWork
        {
            Frame = frame,
            RepeatedKeyDowns = repeatedKeyDowns,
            ValidateTarget = validateTarget,
            CancellationToken = cancellationToken
        };
        lock (_sync)
        {
            EnsureAccepting();
            if (_replayQueue.Count >= _options.ReplayQueueCapacity)
                throw new InputExecutorException("REPLAY_QUEUE_FULL", "The bounded lossless replay FIFO is full.", 429);
            _replayQueue.Enqueue(work);
            PublishSnapshotLocked();
        }
        _wake.Set();
        return work.Task;
    }

    public Task ReleaseAllAndClearAsync(string reason, CancellationToken cancellationToken = default)
    {
        var work = new ControlWork
        {
            CancellationToken = cancellationToken,
            Operation = scheduler => scheduler.ReleaseAllOnSchedulerThread(reason)
        };
        lock (_sync)
        {
            if (_shutdownRequested) return Task.CompletedTask;
            _activeWorkCancellation?.Cancel();
            ClearPendingLocked(new InputExecutorException("QUEUE_CLEARED", $"Input queue cleared: {reason}.", 409));
            _controlQueue.Enqueue(work);
            PublishSnapshotLocked();
        }
        _wake.Set();
        return work.Task;
    }

    public void CancelActiveWork()
    {
        lock (_sync) _activeWorkCancellation?.Cancel();
    }

    public bool BestEffortEmergencyRelease(string reason, TimeSpan timeout)
    {
        try { return ReleaseAllAndClearAsync(reason).Wait(timeout); }
        catch { return false; }
    }

    private void Run()
    {
        PublishSnapshotFromSchedulerThread();
        while (true)
        {
            IWorkItem? work;
            lock (_sync)
            {
                work = TakeNextLocked();
                if (work is not null)
                {
                    _activeWorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.CancellationToken);
                    _activeWorkKind = work.Kind;
                }
                else if (_shutdownRequested)
                    break;
            }

            if (work is null)
            {
                _wake.WaitOne();
                continue;
            }

            try
            {
                work.Execute(this, _activeWorkCancellation!.Token);
            }
            catch (OperationCanceledException ex)
            {
                TryReleaseAfterFailure();
                work.Cancel(new InputExecutorException("SCHEDULED_WORK_CANCELLED", "Scheduled input work was cancelled.", 409, inner: ex));
            }
            catch (Exception ex)
            {
                TryReleaseAfterFailure();
                work.Cancel(ex);
            }
            finally
            {
                lock (_sync)
                {
                    _activeWorkCancellation?.Dispose();
                    _activeWorkCancellation = null;
                    _activeWorkKind = null;
                }
                PublishSnapshotFromSchedulerThread();
            }
        }
        PublishSnapshotFromSchedulerThread(running: false);
    }

    private IWorkItem? TakeNextLocked()
    {
        if (_controlQueue.TryDequeue(out var control)) return control;
        if (_replayQueue.TryDequeue(out var replay)) return replay;
        if (_realtimeSlot is not null && (_realtimeBurst < _options.MaxRealtimeBurst || _commandQueue.Count == 0))
        {
            var realtime = _realtimeSlot;
            _realtimeSlot = null;
            _realtimeBurst++;
            return realtime;
        }
        if (_commandQueue.TryDequeue(out var command))
        {
            _realtimeBurst = 0;
            return command;
        }
        if (_realtimeSlot is not null)
        {
            var realtime = _realtimeSlot;
            _realtimeSlot = null;
            _realtimeBurst = 1;
            return realtime;
        }
        return null;
    }

    private void ReleaseAllOnSchedulerThread(string reason)
    {
        _state.ReleaseAllAsync(_backend, CancellationToken.None).GetAwaiter().GetResult();
        PublishSnapshotFromSchedulerThread();
    }

    private void TryReleaseAfterFailure()
    {
        try { _state.ReleaseAllAsync(_backend, CancellationToken.None).GetAwaiter().GetResult(); }
        catch { }
        PublishSnapshotFromSchedulerThread();
    }

    private void ClearPendingLocked(Exception exception)
    {
        while (_replayQueue.TryDequeue(out var replay)) replay.Cancel(exception);
        while (_commandQueue.TryDequeue(out var command)) command.Cancel(exception);
        if (_realtimeSlot is not null)
        {
            _realtimeSlot.Supersede(this);
            _realtimeSlot = null;
        }
    }

    private void EnsureAccepting()
    {
        if (!_accepting || _shutdownRequested) throw new InputExecutorException("SCHEDULER_STOPPING", "The input scheduler is stopping.", 503);
    }

    private ExecutionResult Executed(long sequence, IReadOnlyList<EmittedInput> events) => new()
    {
        Accepted = true,
        Sequence = sequence,
        Status = "executed",
        Events = events,
        MonotonicUs = _clock.NowMicroseconds
    };

    private ExecutionResult Dropped(long sequence, string status) => new()
    {
        Accepted = false,
        Sequence = sequence,
        Status = status,
        MonotonicUs = _clock.NowMicroseconds
    };

    private void PublishSnapshotLocked() => _snapshot = new InputSchedulerSnapshot(
        _snapshot.SchedulerThreadId,
        _snapshot.HeldKeys,
        _snapshot.HeldMouseButtons,
        _commandQueue.Count,
        _realtimeSlot is not null,
        _activeWorkKind,
        _snapshot.Running);

    private void PublishSnapshotFromSchedulerThread(bool running = true)
    {
        lock (_sync)
        {
            _snapshot = new InputSchedulerSnapshot(
                Environment.CurrentManagedThreadId,
                _state.HeldKeys,
                _state.HeldButtons,
                _commandQueue.Count,
                _realtimeSlot is not null,
                _activeWorkKind,
                running);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ControlWork? shutdown = null;
        lock (_sync)
        {
            if (_shutdownRequested) return;
            _accepting = false;
            _activeWorkCancellation?.Cancel();
            ClearPendingLocked(new InputExecutorException("SCHEDULER_STOPPING", "The input scheduler is stopping.", 503));
            shutdown = new ControlWork
            {
                Operation = scheduler =>
                {
                    scheduler.ReleaseAllOnSchedulerThread("scheduler_shutdown");
                    lock (scheduler._sync) scheduler._shutdownRequested = true;
                }
            };
            _controlQueue.Enqueue(shutdown);
        }
        _wake.Set();
        try { await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(5));
        _wake.Dispose();
        if (_backend is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (_backend is IDisposable disposable) disposable.Dispose();
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<ExecutionResult> NewCompletion() => NewCompletion<ExecutionResult>();

    private sealed class SchedulerBlockingDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (duration <= TimeSpan.Zero) return Task.CompletedTask;
            if (cancellationToken.WaitHandle.WaitOne(duration)) cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
