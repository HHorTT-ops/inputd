using InputExecutor.Core.Protocol;
using InputExecutor.Scheduler;

namespace InputExecutor.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task RelativeMouse_PreservesPositiveAndNegativeCounts()
    {
        var backend = new RecordingBackend();
        await using var scheduler = new InputScheduler(backend);

        await scheduler.EnqueueCommandAsync(Command(1,
            new ActionCommand { Type = "mouse_move_relative", Dx = 17, Dy = -9 },
            new ActionCommand { Type = "mouse_move_relative", Dx = -4, Dy = 6 }), Noop, default);

        Assert.Equal(new[] { "17,-9", "-4,6" }, backend.Calls.Where(x => x.Kind == "mouse_move_relative").Select(x => x.Detail));
    }

    [Fact]
    public async Task ScanCodeKey_HasPairedDownAndUp()
    {
        var backend = new RecordingBackend();
        await using var scheduler = new InputScheduler(backend);

        await scheduler.EnqueueCommandAsync(Command(1,
            new ActionCommand { Type = "key", Code = "KeyW", Action = "press", DurationMs = 0 }), Noop, default);

        Assert.Equal(new[] { "KeyW:down", "KeyW:up" }, backend.Calls.Where(x => x.Kind == "key").Select(x => x.Detail));
        Assert.Empty(scheduler.Snapshot.HeldKeys);
    }

    [Fact]
    public async Task RealtimeState_HoldsMultipleKeysThenReleasesDiff()
    {
        var backend = new RecordingBackend();
        await using var scheduler = new InputScheduler(backend);
        await scheduler.EnqueueRealtimeAsync(Frame(1, "KeyW", "ShiftLeft"), Noop, default);
        Assert.Equal(new[] { "KeyW", "ShiftLeft" }, scheduler.Snapshot.HeldKeys.OrderBy(x => x));

        await scheduler.EnqueueRealtimeAsync(Frame(2), Noop, default);

        Assert.Empty(scheduler.Snapshot.HeldKeys);
        Assert.Contains(backend.Calls, x => x.Detail == "KeyW:down");
        Assert.Contains(backend.Calls, x => x.Detail == "ShiftLeft:down");
        Assert.Contains(backend.Calls, x => x.Detail == "KeyW:up");
        Assert.Contains(backend.Calls, x => x.Detail == "ShiftLeft:up");
    }

    [Fact]
    public async Task RealtimeSlot_IsLatestWins()
    {
        var backend = new RecordingBackend();
        await using var scheduler = new InputScheduler(backend);
        var blocker = scheduler.EnqueueCommandAsync(Command(1,
            new ActionCommand { Type = "mouse_move_relative", Dx = 1, Dy = 0 },
            new ActionCommand { Type = "wait", DurationMs = 150 }), Noop, default);
        Assert.True(backend.FirstCall.Wait(TimeSpan.FromSeconds(2)));

        var old = scheduler.EnqueueRealtimeAsync(Frame(10, "KeyW"), Noop, default);
        var latest = scheduler.EnqueueRealtimeAsync(Frame(11, "ShiftLeft"), Noop, default);
        var oldResult = await old;
        await blocker;
        var latestResult = await latest;

        Assert.Equal("superseded", oldResult.Status);
        Assert.Equal("executed", latestResult.Status);
        Assert.DoesNotContain(backend.Calls, x => x.Detail == "KeyW:down");
        Assert.Contains(backend.Calls, x => x.Detail == "ShiftLeft:down");
    }

    [Fact]
    public async Task ExpiredDeadline_IsDroppedWithoutInjection()
    {
        var backend = new RecordingBackend();
        var clock = new FakeClock { NowMicroseconds = 5_000 };
        await using var scheduler = new InputScheduler(backend, clock);
        var frame = Frame(1, "KeyW") with { DeadlineUs = 4_999 };

        var result = await scheduler.EnqueueRealtimeAsync(frame, Noop, default);

        Assert.False(result.Accepted);
        Assert.Equal("expired", result.Status);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task ConcurrentCommands_DoNotInterleaveAndUseOneThread()
    {
        var backend = new RecordingBackend();
        await using var scheduler = new InputScheduler(backend);
        var first = scheduler.EnqueueCommandAsync(Command(1,
            new ActionCommand { Type = "mouse_move_relative", Dx = 1, Dy = 0 },
            new ActionCommand { Type = "wait", DurationMs = 80 },
            new ActionCommand { Type = "mouse_move_relative", Dx = 2, Dy = 0 }), Noop, default);
        Assert.True(backend.FirstCall.Wait(TimeSpan.FromSeconds(2)));
        var second = scheduler.EnqueueCommandAsync(Command(2,
            new ActionCommand { Type = "mouse_move_relative", Dx = 99, Dy = 0 }), Noop, default);

        await Task.WhenAll(first, second);

        Assert.Equal(new[] { "1,0", "2,0", "99,0" }, backend.Calls.Where(x => x.Kind == "mouse_move_relative").Select(x => x.Detail));
        Assert.Single(backend.Calls.Select(x => x.ThreadId).Distinct());
        Assert.All(backend.Calls, call => Assert.Equal(scheduler.SchedulerThreadId, call.ThreadId));
    }

    [Fact]
    public async Task SchedulerShutdown_ReleasesHeldInputs()
    {
        var backend = new RecordingBackend();
        var scheduler = new InputScheduler(backend);
        await scheduler.EnqueueRealtimeAsync(Frame(1, "KeyW"), Noop, default);

        await scheduler.DisposeAsync();

        Assert.EndsWith("KeyW:up", backend.Calls.Last(x => x.Kind == "key").Detail);
        Assert.False(scheduler.Snapshot.Running);
    }

    private static CommandRequest Command(long sequence, params ActionCommand[] actions) => new() { Sequence = sequence, Actions = actions };
    private static RealtimeFrame Frame(long sequence, params string[] keys) => new()
    {
        Sequence = sequence,
        TimestampUs = sequence * 1_000,
        DeadlineUs = long.MaxValue,
        Keyboard = new KeyboardFrame { Held = keys },
        Mouse = new MouseFrame()
    };
    private static void Noop() { }
}
