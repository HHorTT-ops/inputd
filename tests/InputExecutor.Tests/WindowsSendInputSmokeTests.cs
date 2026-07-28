using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Forms;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Recording;
using InputExecutor.Dashboard;
using InputExecutor.Scheduler;
using InputExecutor.Windows;

namespace InputExecutor.Tests;

public sealed class WindowsSendInputSmokeTests
{
    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RelativeMovement_HasCorrectSignedDirections_AndRestoresPointer()
    {
        Assert.True(GetCursorPos(out var original));
        var anchor = new Point { X = Math.Clamp(original.X, 200, GetSystemMetrics(0) - 200), Y = Math.Clamp(original.Y, 200, GetSystemMetrics(1) - 200) };
        Assert.True(SetCursorPos(anchor.X, anchor.Y));
        try
        {
            await using var scheduler = new InputScheduler(new WindowsSendInputBackend());
            await scheduler.EnqueueCommandAsync(new CommandRequest
            {
                Sequence = 1,
                Actions = new[] { new ActionCommand { Type = "mouse_move_relative", Dx = 80, Dy = -60 } }
            }, () => { }, default);
            await Task.Delay(30);
            Assert.True(GetCursorPos(out var moved));
            Assert.True(moved.X > anchor.X, $"Expected rightward movement, got {anchor.X} -> {moved.X}.");
            Assert.True(moved.Y < anchor.Y, $"Expected upward movement, got {anchor.Y} -> {moved.Y}.");

            await scheduler.EnqueueCommandAsync(new CommandRequest
            {
                Sequence = 2,
                Actions = new[] { new ActionCommand { Type = "mouse_move_relative", Dx = -80, Dy = 60 } }
            }, () => { }, default);
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
        }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task LegacyGameReplayMovement_PreservesSignedRelativeCounts()
    {
        Assert.True(GetCursorPos(out var original));
        var anchor = new Point
        {
            X = Math.Clamp(original.X, 200, GetSystemMetrics(0) - 200),
            Y = Math.Clamp(original.Y, 200, GetSystemMetrics(1) - 200)
        };
        Assert.True(SetCursorPos(anchor.X, anchor.Y));
        try
        {
            await using var backend = new WindowsLegacyMouseReplayBackend();
            var emitted = await backend.MoveMouseRelativeAsync(64, -48, true, default);
            await Task.Delay(30);
            Assert.True(GetCursorPos(out var moved));
            Assert.True(moved.X > anchor.X, $"Expected rightward legacy movement, got {anchor.X} -> {moved.X}.");
            Assert.True(moved.Y < anchor.Y, $"Expected upward legacy movement, got {anchor.Y} -> {moved.Y}.");
            Assert.Contains("api=mouse_event", emitted.Detail);
        }
        finally { SetCursorPos(original.X, original.Y); }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task SendInputRelativeMovement_IsVisibleToOrdinaryRawInputWindow()
    {
        Assert.True(GetCursorPos(out var original));
        await using var recorder = new WindowsRawInputRecorder(new NullTrajectorySink(), new SystemMonotonicClock());
        var observed = new TaskCompletionSource<HumanInputFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.TaggedRawFrameObserved += (_, frame) =>
        {
            if (frame.Device == "tagged_raw_mouse" && (frame.Mouse.Dx != 0 || frame.Mouse.Dy != 0))
                observed.TrySetResult(frame);
        };
        try
        {
            var backend = new WindowsSendInputBackend();
            await backend.MoveMouseRelativeAsync(16, -12, true, default);
            var completed = await Task.WhenAny(observed.Task, Task.Delay(300));
            Assert.Same(observed.Task, completed);
            var frame = await observed.Task;
            Assert.Equal(16, frame.Mouse.Dx);
            Assert.Equal(-12, frame.Mouse.Dy);
            Assert.True(frame.InjectedTagged);
        }
        finally { SetCursorPos(original.X, original.Y); }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task ScanCodeShiftRight_DownAndUp_AreVisibleToWindows()
    {
        await using var scheduler = new InputScheduler(new WindowsSendInputBackend());
        try
        {
            await scheduler.EnqueueCommandAsync(new CommandRequest
            {
                Sequence = 1,
                Actions = new[] { new ActionCommand { Type = "key", Code = "ShiftRight", Action = "down" } }
            }, () => { }, default);
            await Task.Delay(20);
            Assert.NotEqual(0, GetAsyncKeyState(0xA1) & 0x8000);
        }
        finally
        {
            try
            {
                await scheduler.EnqueueCommandAsync(new CommandRequest
                {
                    Sequence = 2,
                    Actions = new[] { new ActionCommand { Type = "key", Code = "ShiftRight", Action = "up" } }
                }, () => { }, default);
            }
            catch { }
            scheduler.BestEffortEmergencyRelease("smoke_test_finally", TimeSpan.FromSeconds(1));
        }
        await Task.Delay(20);
        Assert.Equal(0, GetAsyncKeyState(0xA1) & 0x8000);
    }

    [Fact]
    public void PhysicalKeyMap_UsesExpectedSet1ScanCodes()
    {
        Assert.True(PhysicalKeyMap.TryGet("KeyW", out var w));
        Assert.Equal(0x11, w.Value);
        Assert.False(w.Extended);
        Assert.True(PhysicalKeyMap.TryGet("ControlRight", out var rightControl));
        Assert.Equal(0x1D, rightControl.Value);
        Assert.True(rightControl.Extended);
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public void GlobalHotkey_CanRegisterDetectConflictUnregisterAndRegisterAgain()
    {
        using var window = new Form();
        var handle = window.Handle;
        const int firstId = 0x4A01;
        const int secondId = 0x4A02;
        var modifiers = WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt | WindowsHotkeyModifiers.Shift;
        try
        {
            WindowsGlobalHotkeys.Register(handle, firstId, modifiers, (int)Keys.F24);
            Assert.Throws<Win32Exception>(() => WindowsGlobalHotkeys.Register(handle, secondId, modifiers, (int)Keys.F24));
            WindowsGlobalHotkeys.Unregister(handle, firstId);
            WindowsGlobalHotkeys.Register(handle, secondId, modifiers, (int)Keys.F24);
        }
        finally
        {
            WindowsGlobalHotkeys.Unregister(handle, firstId);
            WindowsGlobalHotkeys.Unregister(handle, secondId);
        }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RawInputRecorder_RegisteredHotkeyNeverPersistsAnOrphanKeyUp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inputd-hotkey-boundary-{Guid.NewGuid():N}.jsonl");
        const int hotkeyId = 0x4A03;
        using var window = new Form();
        var handle = window.Handle;
        JsonLinesTrajectorySink? sink = null;
        WindowsRawInputRecorder? recorder = null;
        try
        {
            sink = new JsonLinesTrajectorySink(path);
            recorder = new WindowsRawInputRecorder(sink, new InputExecutor.Core.Abstractions.SystemMonotonicClock());
            recorder.Start("hotkey-boundary-test", new TargetBinding
            {
                WindowHandle = $"0x{handle.ToInt64():X}",
                ProcessId = Environment.ProcessId,
                ProcessName = "test"
            }, includeExecutorEvents: false);
            WindowsGlobalHotkeys.Register(handle, hotkeyId,
                WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt | WindowsHotkeyModifiers.Shift, (int)Keys.F11);

            SendUntaggedKey(0x11, 0x1D, false);
            SendUntaggedKey(0x12, 0x38, false);
            SendUntaggedKey(0x10, 0x2A, false);
            SendUntaggedKey(0x7A, 0x57, false);
            SendUntaggedKey(0x7A, 0x57, true);
            SendUntaggedKey(0x10, 0x2A, true);
            SendUntaggedKey(0x12, 0x38, true);
            SendUntaggedKey(0x11, 0x1D, true);
            await Task.Delay(150);

            var status = await recorder.StopAsync("hotkey-boundary-test", default);
            Assert.Equal(0, status.EventsDropped);
            Assert.Equal(0, status.EventsPending);
            await recorder.DisposeAsync();
            recorder = null;
            await sink.DisposeAsync();
            sink = null;

            var lines = File.ReadAllLines(path);
            static int Count(IReadOnlyList<string> source, string key, bool down) => source.Count(line =>
                line.Contains($"\"changed_key\":\"{key}\"", StringComparison.Ordinal) &&
                line.Contains($"\"key_down\":{down.ToString().ToLowerInvariant()}", StringComparison.Ordinal));
            Assert.Equal(Count(lines, "F11", true), Count(lines, "F11", false));
            Assert.Equal(Count(lines, "ControlLeft", true), Count(lines, "ControlLeft", false));
            Assert.Equal(Count(lines, "AltLeft", true), Count(lines, "AltLeft", false));
            Assert.Equal(Count(lines, "ShiftLeft", true), Count(lines, "ShiftLeft", false));
            Assert.Contains("\"held\":[]", lines[^1]);
        }
        finally
        {
            SendUntaggedKey(0x7A, 0x57, true);
            SendUntaggedKey(0x10, 0x2A, true);
            SendUntaggedKey(0x12, 0x38, true);
            SendUntaggedKey(0x11, 0x1D, true);
            WindowsGlobalHotkeys.Unregister(handle, hotkeyId);
            if (recorder is not null) await recorder.DisposeAsync();
            if (sink is not null) await sink.DisposeAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RawInputRecorder_KeyHeldBeforeStartIsCapturedAsInitialState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inputd-preheld-boundary-{Guid.NewGuid():N}.jsonl");
        JsonLinesTrajectorySink? sink = null;
        WindowsRawInputRecorder? recorder = null;
        try
        {
            sink = new JsonLinesTrajectorySink(path);
            recorder = new WindowsRawInputRecorder(sink, new SystemMonotonicClock());

            SendUntaggedKey(0x57, 0x11, false);
            await Task.Delay(100);
            recorder.Start("preheld-boundary", new TargetBinding
            {
                WindowHandle = "0x1",
                ProcessId = Environment.ProcessId,
                ProcessName = "test"
            }, includeExecutorEvents: false);
            SendUntaggedKey(0x57, 0x11, true);
            await Task.Delay(100);

            var status = await recorder.StopAsync("preheld-boundary", default);
            Assert.Equal(0, status.EventsDropped);
            Assert.Equal(0, status.EventsPending);
            await recorder.DisposeAsync();
            recorder = null;
            await sink.DisposeAsync();
            sink = null;

            var lines = await File.ReadAllLinesAsync(path);
            Assert.NotEmpty(lines);
            using var first = System.Text.Json.JsonDocument.Parse(lines[0]);
            var action = first.RootElement.GetProperty("action_requested");
            Assert.Equal("state_snapshot", action.GetProperty("device").GetString());
            Assert.Contains("KeyW", action.GetProperty("keyboard").GetProperty("held").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains(lines, line => line.Contains("\"changed_key\":\"KeyW\"", StringComparison.Ordinal) &&
                                           line.Contains("\"key_down\":false", StringComparison.Ordinal));
        }
        finally
        {
            SendUntaggedKey(0x57, 0x11, true);
            if (recorder is not null) await recorder.DisposeAsync();
            if (sink is not null) await sink.DisposeAsync();
            try { File.Delete(path); } catch { }
        }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task DashboardPipeline_StopHotkeyIsAbsentFromDurableRawInputTrajectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inputd-hotkey-redacted-live-{Guid.NewGuid():N}.jsonl");
        var clock = new SystemMonotonicClock();
        await using var sink = new SwitchableTrajectorySink(TimeSpan.FromSeconds(3));
        await using var recorder = new WindowsRawInputRecorder(sink, clock);
        try
        {
            await sink.StartAsync(path, includeInjected: false);
            recorder.Start("live-hotkey-redaction", new TargetBinding
            {
                WindowHandle = "0x1",
                ProcessId = Environment.ProcessId,
                ProcessName = "test"
            }, includeExecutorEvents: false);

            SendUntaggedKey(0x11, 0x1D, false);
            SendUntaggedKey(0x12, 0x38, false);
            SendUntaggedKey(0x79, 0x44, false);
            var hotkeyUs = clock.NowMicroseconds;
            await sink.BeginShortcutSuppressionAsync(["ControlLeft", "ControlRight", "AltLeft", "AltRight", "F10"], hotkeyUs);
            SendUntaggedKey(0x79, 0x44, true);
            SendUntaggedKey(0x12, 0x38, true);
            SendUntaggedKey(0x11, 0x1D, true);
            await Task.Delay(150);
            await sink.CompleteShortcutSuppressionAsync(hotkeyUs, clock.NowMicroseconds);

            var status = await recorder.StopAsync("live-hotkey-redaction", default);
            await sink.StopAsync();
            Assert.Equal(0, status.EventsDropped);
            Assert.Equal(0, status.EventsPending);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.NotEmpty(lines);
            var json = string.Join('\n', lines);
            Assert.DoesNotContain("ControlLeft", json);
            Assert.DoesNotContain("ControlRight", json);
            Assert.DoesNotContain("AltLeft", json);
            Assert.DoesNotContain("AltRight", json);
            Assert.DoesNotContain("F10", json);
            var sequences = lines.Select(line =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                return document.RootElement.GetProperty("sequence").GetInt64();
            }).ToArray();
            Assert.Equal(Enumerable.Range(1, lines.Length).Select(value => (long)value), sequences);
        }
        finally
        {
            SendUntaggedKey(0x79, 0x44, true);
            SendUntaggedKey(0x12, 0x38, true);
            SendUntaggedKey(0x11, 0x1D, true);
            try { File.Delete(path); } catch { }
        }
    }

    [DiagnosticHookIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RawInputRecorder_DiagnosticHooks_PersistActualTaggedWindowsEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inputd-live-recording-{Guid.NewGuid():N}.jsonl");
        Assert.True(GetCursorPos(out var original));
        JsonLinesTrajectorySink? sink = null;
        WindowsRawInputRecorder? recorder = null;
        try
        {
            sink = new JsonLinesTrajectorySink(path);
            recorder = new WindowsRawInputRecorder(sink, new InputExecutor.Core.Abstractions.SystemMonotonicClock());
            Assert.False(recorder.Status.DiagnosticHooksActive);
            recorder.Start("live-recorder-test", new TargetBinding
            {
                WindowHandle = "0x1",
                ProcessId = Environment.ProcessId,
                ProcessName = "test"
            }, includeExecutorEvents: true);
            Assert.True(recorder.Status.DiagnosticHooksActive);
            var backend = new WindowsSendInputBackend();
            await backend.MoveMouseRelativeAsync(7, -5, true, default);
            await backend.SetKeyAsync("ShiftRight", true, default);
            await backend.SetKeyAsync("ShiftRight", false, default);
            await backend.TypeTextAsync("R", default);
            await Task.Delay(250);
            var status = await recorder.StopAsync("live-recorder-test", default);
            Assert.True(status.EventsRecorded >= 4, $"Expected tagged hook events, got {status.EventsRecorded}.");
            Assert.Equal(0, status.EventsDropped);
            Assert.Equal(0, status.EventsPending);
            Assert.False(status.Faulted, status.LastError);
            Assert.False(status.DiagnosticHooksActive);
            await recorder.DisposeAsync();
            recorder = null;
            await sink.DisposeAsync();
            sink = null;
            var jsonl = await File.ReadAllTextAsync(path);
            Assert.Contains("hook_injected", jsonl);
            Assert.Contains("\"injected_tagged\":true", jsonl);
            Assert.Contains("ShiftRight", jsonl);
        }
        finally
        {
            if (recorder is not null) await recorder.DisposeAsync();
            if (sink is not null) await sink.DisposeAsync();
            SetCursorPos(original.X, original.Y);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [DiagnosticHookIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RawInputRecorder_HighFrequencyMouseBurst_PersistsEveryTaggedPacket()
    {
        const int burstSize = 5_000;
        var path = Path.Combine(Path.GetTempPath(), $"inputd-burst-recording-{Guid.NewGuid():N}.jsonl");
        JsonLinesTrajectorySink? sink = null;
        WindowsRawInputRecorder? recorder = null;
        Assert.True(GetCursorPos(out var original));
        try
        {
            sink = new JsonLinesTrajectorySink(path);
            recorder = new WindowsRawInputRecorder(sink, new InputExecutor.Core.Abstractions.SystemMonotonicClock());
            recorder.Start("burst-recorder-test", new TargetBinding
            {
                WindowHandle = "0x1",
                ProcessId = Environment.ProcessId,
                ProcessName = "test"
            }, includeExecutorEvents: true);

            var backend = new WindowsSendInputBackend();
            for (var i = 0; i < burstSize; i++)
                await backend.MoveMouseRelativeAsync((i & 1) == 0 ? 1 : -1, 0, true, default);

            var status = await recorder.StopAsync("burst-recorder-test", default);
            Assert.Equal(0, status.EventsDropped);
            Assert.Equal(0, status.EventsPending);
            Assert.False(status.Faulted, status.LastError);
            await recorder.DisposeAsync();
            recorder = null;
            await sink.DisposeAsync();
            sink = null;

            var taggedMousePackets = File.ReadLines(path).Count(line =>
                line.Contains("\"device\":\"mouse_hook_injected\"", StringComparison.Ordinal) &&
                line.Contains("\"injected_tagged\":true", StringComparison.Ordinal));
            Assert.Equal(burstSize, taggedMousePackets);
        }
        finally
        {
            if (recorder is not null) await recorder.DisposeAsync();
            if (sink is not null) await sink.DisposeAsync();
            SetCursorPos(original.X, original.Y);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [DiagnosticHookIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RawInputRecorder_StartStopStart_PersistsBothRecordingsIndependently()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"inputd-repeat-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.jsonl");
        var secondPath = Path.Combine(directory, "second.jsonl");
        Assert.True(GetCursorPos(out var original));
        await using var sink = new SwitchableTrajectorySink();
        await using var recorder = new WindowsRawInputRecorder(sink, new InputExecutor.Core.Abstractions.SystemMonotonicClock());
        var backend = new WindowsSendInputBackend();
        try
        {
            async Task RecordCycle(string path, int dx, string key)
            {
                await sink.StartAsync(path, includeInjected: true);
                recorder.Start("repeat-test", new TargetBinding
                {
                    WindowHandle = "0x1",
                    ProcessId = Environment.ProcessId,
                    ProcessName = "test"
                }, includeExecutorEvents: true);
                await backend.MoveMouseRelativeAsync(dx, 0, true, default);
                await backend.SetKeyAsync(key, true, default);
                await backend.SetKeyAsync(key, false, default);
                await Task.Delay(100);
                var status = await recorder.StopAsync("repeat-test", default);
                await sink.StopAsync();
                Assert.True(status.EventsRecorded >= 3, $"Expected mouse and key edges in {path}, got {status.EventsRecorded}.");
                Assert.Equal(0, status.EventsDropped);
                Assert.Equal(0, status.EventsPending);
                Assert.False(status.Faulted, status.LastError);
            }

            await RecordCycle(firstPath, 3, "ShiftRight");
            await RecordCycle(secondPath, -3, "ControlRight");

            var first = await File.ReadAllTextAsync(firstPath);
            var second = await File.ReadAllTextAsync(secondPath);
            Assert.Contains("ShiftRight", first);
            Assert.DoesNotContain("ControlRight", first);
            Assert.Contains("ControlRight", second);
            Assert.DoesNotContain("ShiftRight", second);
            Assert.Contains("\"sequence\":1", first);
            Assert.Contains("\"sequence\":1", second);
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [WindowsIntegrationFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task RawInputRecorder_ProductionMode_NeverInstallsGlobalHooks()
    {
        await using var sink = new NullTrajectorySink();
        await using var recorder = new WindowsRawInputRecorder(sink, new InputExecutor.Core.Abstractions.SystemMonotonicClock());
        Assert.False(recorder.Status.DiagnosticHooksActive);
        var started = recorder.Start("production-hook-test", new TargetBinding
        {
            WindowHandle = "0x1",
            ProcessId = Environment.ProcessId,
            ProcessName = "test"
        }, includeExecutorEvents: false);
        Assert.False(started.DiagnosticHooksActive);
        var status = await recorder.StopAsync("production-hook-test", default);
        Assert.False(status.DiagnosticHooksActive);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static void SendUntaggedKey(byte virtualKey, byte scanCode, bool up) =>
        KeybdEvent(virtualKey, scanCode, up ? 0x0002u : 0u, UIntPtr.Zero);
}

public sealed class WindowsIntegrationFactAttribute : FactAttribute
{
    public WindowsIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("INPUTD_RUN_WIN32_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set INPUTD_RUN_WIN32_TESTS=1 on an interactive test desktop.";
    }
}

public sealed class DiagnosticHookIntegrationFactAttribute : FactAttribute
{
    public DiagnosticHookIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("INPUTD_RUN_WIN32_TESTS"), "1", StringComparison.Ordinal) ||
            !string.Equals(Environment.GetEnvironmentVariable("INPUTD_RUN_DIAGNOSTIC_HOOK_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set INPUTD_RUN_WIN32_TESTS=1 and INPUTD_RUN_DIAGNOSTIC_HOOK_TESTS=1 on an interactive desktop without a protected game/anti-cheat process.";
    }
}
