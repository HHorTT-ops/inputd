using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using InputExecutor.Core.Protocol;
using InputExecutor.Scheduler;
using InputExecutor.Windows;

namespace InputExecutor.Tests;

public sealed class TrajectoryReplayPlayerTests
{
    [Fact]
    public async Task Replay_PreservesEveryDeltaAndHeldStateOnOneSchedulerThread()
    {
        var path = TemporaryPath();
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard();
        await using var scheduler = new InputScheduler(backend);
        var player = new TrajectoryReplayPlayer(scheduler, guard);
        try
        {
            await WriteAsync(path,
                Record(1, 1_000, ["KeyW"], [], 12, -4),
                Record(2, 1_000, ["KeyW", "KeyD"], ["right"], -7, 3),
                Record(3, 1_000, [], [], 0, 0));

            var summary = await player.ReplayAsync(path, guard.Target);

            Assert.Equal(3, summary.EventCount);
            Assert.Contains(backend.Calls, call => call.Detail == "KeyW:down");
            Assert.Contains(backend.Calls, call => call.Detail == "KeyD:down");
            Assert.Contains(backend.Calls, call => call.Detail == "right:down");
            Assert.Contains(backend.Calls, call => call.Detail == "12,-4");
            Assert.Contains(backend.Calls, call => call.Detail == "-7,3");
            Assert.Contains(backend.Calls, call => call.Detail == "KeyW:up");
            Assert.Contains(backend.Calls, call => call.Detail == "KeyD:up");
            Assert.Contains(backend.Calls, call => call.Detail == "right:up");
            Assert.Single(backend.Calls.Select(call => call.ThreadId).Distinct());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Replay_AggregatesTinyMousePacketsAndPreservesTheirExactSignedSum()
    {
        var path = TemporaryPath();
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard();
        await using var scheduler = new InputScheduler(backend);
        var player = new TrajectoryReplayPlayer(scheduler, guard);
        try
        {
            await WriteAsync(path,
                Record(1, 1_000, [], [], 1, 0),
                Record(2, 1_000, [], [], 1, -1),
                Record(3, 1_000, [], [], 1, -1),
                Record(4, 2_000, [], [], 1, -1),
                Record(5, 2_000, [], [], -1, 0),
                Record(6, 3_000, [], [], 1, -1),
                Record(7, 3_000, [], [], 1, 0),
                Record(8, 3_000, [], [], 1, -1));

            var summary = await player.ReplayAsync(path, guard.Target);

            var moves = backend.Calls.Where(call => call.Kind == "mouse_move_relative").ToArray();
            var move = Assert.Single(moves);
            Assert.Equal("6,-5", move.Detail);
            Assert.Equal(8, summary.MouseMoveEventCount);
            Assert.Equal(6, summary.TotalMouseDx);
            Assert.Equal(-5, summary.TotalMouseDy);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Replay_ReemitsRecordedTypematicDownPacketsUntilTheRecordedKeyUp()
    {
        var path = TemporaryPath();
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard();
        await using var scheduler = new InputScheduler(backend);
        var player = new TrajectoryReplayPlayer(scheduler, guard);
        try
        {
            await WriteAsync(path,
                Record(1, 1_000, ["KeyW"], [], 0, 0, "KeyW", true),
                Record(2, 32_000, ["KeyW"], [], 0, 0, "KeyW", true),
                Record(3, 63_000, ["KeyW"], [], 2, -1),
                Record(4, 64_000, ["KeyW"], [], 0, 0, "KeyW", true),
                Record(5, 251_000, [], [], 0, 0, "KeyW", false));

            var stopwatch = Stopwatch.StartNew();
            await player.ReplayAsync(path, guard.Target);
            stopwatch.Stop();

            var wCalls = backend.Calls.Where(call => call.Kind == "key" && call.Detail.StartsWith("KeyW:", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, wCalls.Count(call => call.Detail == "KeyW:down"));
            Assert.Single(wCalls, call => call.Detail == "KeyW:up");
            Assert.Equal("KeyW:down", wCalls[0].Detail);
            Assert.Equal("KeyW:up", wCalls[^1].Detail);
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(220), $"Recorded hold was shortened to {stopwatch.Elapsed}.");
            Assert.Empty(scheduler.Snapshot.HeldKeys);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Replay_RejectsSequenceGapBeforeInjectingAnything()
    {
        var path = TemporaryPath();
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard();
        await using var scheduler = new InputScheduler(backend);
        var player = new TrajectoryReplayPlayer(scheduler, guard);
        try
        {
            await WriteAsync(path,
                Record(1, 1_000, ["KeyW"], [], 0, 0),
                Record(3, 2_000, [], [], 0, 0));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => player.ReplayAsync(path, guard.Target));

            Assert.Contains("不连续", error.Message);
            Assert.Empty(backend.Calls);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Replay_FocusLossDuringQuietGapReleasesHeldKey()
    {
        var path = TemporaryPath();
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard();
        await using var scheduler = new InputScheduler(backend);
        var player = new TrajectoryReplayPlayer(scheduler, guard);
        try
        {
            await WriteAsync(path,
                Record(1, 1_000, ["KeyW"], [], 0, 0),
                Record(2, 501_000, [], [], 0, 0));

            var replay = player.ReplayAsync(path, guard.Target);
            Assert.True(backend.FirstCall.Wait(TimeSpan.FromSeconds(2)));
            guard.Foreground = false;

            await Assert.ThrowsAsync<InputExecutor.Core.Execution.InputExecutorException>(() => replay);
            Assert.Contains(backend.Calls, call => call.Detail == "KeyW:up");
            Assert.Empty(scheduler.Snapshot.HeldKeys);
        }
        finally { TryDelete(path); }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task Replay_RealWindowsTargetMovesPointerAndHoldsThenReleasesScanCodeKey()
    {
        var path = TemporaryPath();
        var monitorPath = FindRepositoryFile(Path.Combine("tests", "InputExecutor.TestMonitor", "bin", "Debug", "net10.0-windows", "input-monitor.exe"));
        using var monitor = Process.Start(new ProcessStartInfo(monitorPath) { UseShellExecute = true })
                            ?? throw new InvalidOperationException("Could not start input-monitor.exe.");
        try
        {
            await WaitForMainWindowAsync(monitor);
            var guard = new WindowsTargetGuard();
            var target = guard.CaptureProcess(monitor.Id);
            Assert.True(guard.TryActivate(target));
            Assert.True(GetCursorPos(out var original));
            var anchor = new NativePoint
            {
                X = Math.Clamp(original.X, 200, GetSystemMetrics(0) - 200),
                Y = Math.Clamp(original.Y, 200, GetSystemMetrics(1) - 200)
            };
            Assert.True(SetCursorPos(anchor.X, anchor.Y));
            var tinyMoves = Enumerable.Range(1, 16)
                .Select(index => Record(index, 1_000, ["KeyW"], [], 1, -1))
                .Append(Record(17, 501_000, [], [], -16, 16))
                .ToArray();
            await WriteAsync(path, tinyMoves);

            await using var scheduler = new InputScheduler(new WindowsSendInputBackend());
            var player = new TrajectoryReplayPlayer(scheduler, guard);
            var replay = player.ReplayAsync(path, target);
            await Task.Delay(120);

            Assert.NotEqual(0, GetAsyncKeyState(0x57) & 0x8000);
            Assert.True(GetCursorPos(out var moved));
            Assert.True(moved.X - anchor.X >= 10, $"Expected aggregated tiny replay counts to move right visibly: {anchor.X} -> {moved.X}.");
            Assert.True(anchor.Y - moved.Y >= 10, $"Expected aggregated tiny replay counts to move up visibly: {anchor.Y} -> {moved.Y}.");

            await replay;
            await Task.Delay(30);
            Assert.Equal(0, GetAsyncKeyState(0x57) & 0x8000);
            SetCursorPos(original.X, original.Y);
        }
        finally
        {
            try { if (!monitor.HasExited) monitor.Kill(entireProcessTree: true); } catch { }
            TryDelete(path);
        }
    }

    private static TrajectoryRecord Record(
        long sequence,
        long timestampUs,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> buttons,
        int dx,
        int dy,
        string? changedKey = null,
        bool? keyDown = null) => new()
    {
        RecordedAtUtc = DateTimeOffset.UtcNow,
        SessionId = "replay-test",
        Source = "human_raw_input",
        Sequence = sequence,
        ExecutorReceiveTimestampUs = timestampUs,
        ExecutorCompleteTimestampUs = timestampUs,
        ActionRequested = new HumanInputFrame
        {
            Device = changedKey is not null ? "keyboard" : dx != 0 || dy != 0 ? "mouse" : "keyboard",
            TimestampUs = timestampUs,
            Keyboard = new KeyboardFrame { Held = keys },
            Mouse = new MouseFrame { Dx = dx, Dy = dy, HeldButtons = buttons },
            ChangedKey = changedKey,
            KeyDown = keyDown
        },
        Backend = "windows-raw-input"
    };

    private static async Task WriteAsync(string path, params TrajectoryRecord[] records)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await File.WriteAllLinesAsync(path, records.Select(record => JsonSerializer.Serialize(record, options)));
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"inputd-replay-{Guid.NewGuid():N}.jsonl");
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static async Task WaitForMainWindowAsync(Process process)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("input-monitor did not create a main window.");
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
