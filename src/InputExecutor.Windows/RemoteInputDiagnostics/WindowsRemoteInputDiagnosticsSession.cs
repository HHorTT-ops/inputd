using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

/// <summary>
/// Independent dual-channel mouse input diagnostics (Raw Input + WH_MOUSE_LL).
/// Producers only copy POD samples via non-blocking TryEnqueue; JSON is written on background writers.
/// Hook callbacks always call CallNextHookEx and never block on the channel or write files.
/// </summary>
public sealed class WindowsRemoteInputDiagnosticsSession : IAsyncDisposable
{
    public const int DefaultCapacity = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IMonotonicClock _clock;
    private readonly string _outputDirectory;
    private readonly string _stimulusLabel;
    private readonly MouseAbsoluteCoordinateTracker _absoluteTracker = new();
    private readonly CoordinateSpaceConverter _coordinateAligner;
    private readonly DiagnosticChannel<RawSample> _rawChannel;
    private readonly DiagnosticChannel<HookSample> _hookChannel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ManualResetEventSlim _rawReady = new(false);
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly object _statsSync = new();

    private Thread? _rawThread;
    private Thread? _hookThread;
    private Task? _rawWriter;
    private Task? _hookWriter;
    private NativeMethods.WindowProcedure? _rawWndProc;
    private NativeMethods.HookProcedure? _mouseHookProc;
    private IntPtr _rawWindow;
    private IntPtr _mouseHook;
    private uint _rawThreadId;
    private uint _hookThreadId;
    private Exception? _rawError;
    private Exception? _hookError;
    private long _rawAbsolute;
    private long _rawRelative;
    private long _rawHDeviceZero;
    private long _rawHDeviceNonZero;
    private long _hookInjected;
    private long _hookLowerIlInjected;
    private long _hookExtraInfoExecutorTag;

    public WindowsRemoteInputDiagnosticsSession(
        IMonotonicClock clock,
        string outputDirectory,
        string stimulusLabel = "unspecified",
        int capacity = DefaultCapacity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _clock = clock;
        _outputDirectory = outputDirectory;
        _stimulusLabel = stimulusLabel;
        Directory.CreateDirectory(outputDirectory);
        _coordinateAligner = CoordinateSpaceConverter.FromLiveSystem();
        _rawChannel = new DiagnosticChannel<RawSample>(capacity);
        _hookChannel = new DiagnosticChannel<HookSample>(capacity);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var env = SessionEnvironmentSnapshot.Capture();
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "session-environment.json"),
            JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        var coords = DisplayCoordinateDiagnostics.Capture();
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "coordinate-diagnostics.json"),
            JsonSerializer.Serialize(coords, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        _rawWriter = Task.Run(() => WriteRawLoopAsync(Path.Combine(_outputDirectory, "raw-input.jsonl"), _lifetime.Token));
        _hookWriter = Task.Run(() => WriteHookLoopAsync(Path.Combine(_outputDirectory, "ll-hook.jsonl"), _lifetime.Token));

        _rawThread = new Thread(RawInputLoop) { IsBackground = true, Name = "inputd-uu-raw-diag" };
        _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "inputd-uu-llhook-diag" };
        _rawThread.Start();
        _hookThread.Start();
        if (!_rawReady.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Raw Input diagnostics window failed to start.", _rawError);
        if (_rawError is not null) throw new InvalidOperationException("Raw Input diagnostics failed.", _rawError);
        if (!_hookReady.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("LL mouse hook diagnostics failed to start.", _hookError);
        if (_hookError is not null) throw new InvalidOperationException("LL mouse hook diagnostics failed.", _hookError);
    }

    public DiagnosticsStats SnapshotStats()
    {
        lock (_statsSync)
        {
            return new DiagnosticsStats(
                _rawChannel.Enqueued, _hookChannel.Enqueued, _rawChannel.Dropped, _hookChannel.Dropped,
                _rawAbsolute, _rawRelative, _rawHDeviceZero, _rawHDeviceNonZero,
                _hookInjected, _hookLowerIlInjected, _hookExtraInfoExecutorTag);
        }
    }

    /// <summary>Test seam: enqueue without starting native capture.</summary>
    internal bool TryEnqueueRawForTests(RawSample sample) => _rawChannel.TryEnqueue(sample);

    /// <summary>Test seam: enqueue without starting native capture.</summary>
    internal bool TryEnqueueHookForTests(HookSample sample) => _hookChannel.TryEnqueue(sample);

    public async Task StopAsync()
    {
        if (_rawThreadId != 0) NativeMethods.PostThreadMessage(_rawThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (_hookThreadId != 0) NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _rawThread?.Join(TimeSpan.FromSeconds(3));
        _hookThread?.Join(TimeSpan.FromSeconds(3));
        _rawChannel.Complete();
        _hookChannel.Complete();
        if (_rawWriter is not null) await _rawWriter.ConfigureAwait(false);
        if (_hookWriter is not null) await _hookWriter.ConfigureAwait(false);

        var stats = SnapshotStats();
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "channel-stats.json"),
            JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        var (correlation, rawEvents, hookEvents) = BuildCorrelationReport(_stimulusLabel, stats);
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "correlation-report.json"),
            JsonSerializer.Serialize(correlation, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        var verdict = StimulusVerdict.Evaluate(_stimulusLabel, rawEvents, hookEvents);
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "stimulus-verdict.json"),
            JsonSerializer.Serialize(new
            {
                label = _stimulusLabel,
                status = verdict.Status,
                reason = verdict.Reason,
                evidence = verdict.Evidence,
                rule = "PASS only when required actions are evidenced; ZERO/contaminated/incomplete never PASS."
            }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch { /* best effort */ }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _rawReady.Dispose();
        _hookReady.Dispose();
    }

    private void RawInputLoop()
    {
        try
        {
            _rawThreadId = NativeMethods.GetCurrentThreadId();
            _rawWndProc ??= RawWindowProcedure;
            var instance = NativeMethods.GetModuleHandle(null);
            var className = $"inputd.UuRawDiag.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = _rawWndProc,
                hInstance = instance,
                lpszClassName = className,
                lpszMenuName = string.Empty
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw diag window class registration failed.");
            _rawWindow = NativeMethods.CreateWindowEx(0, className, "inputd UU Raw Diagnostics", 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (_rawWindow == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw diag window create failed.");
            var devices = new[]
            {
                new NativeMethods.RAWINPUTDEVICE
                {
                    usUsagePage = 0x01, usUsage = 0x02,
                    dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _rawWindow
                }
            };
            if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw diag RegisterRawInputDevices failed.");
            _rawReady.Set();
            int rc;
            while ((rc = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            if (rc < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw diag GetMessage failed.");
        }
        catch (Exception ex) { _rawError = ex; }
        finally
        {
            if (_rawWindow != IntPtr.Zero) NativeMethods.DestroyWindow(_rawWindow);
            _rawWindow = IntPtr.Zero;
            _rawReady.Set();
        }
    }

    private IntPtr RawWindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_INPUT)
            ProcessRawInput(lParam);
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ProcessRawInput(IntPtr rawHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        if (NativeMethods.GetRawInputData(rawHandle, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize)
            return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(rawHandle, NativeMethods.RID_INPUT, buffer, ref size, headerSize) != size)
                return;
            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
            if (header.dwType != NativeMethods.RIM_TYPEMOUSE) return;
            var body = IntPtr.Add(buffer, Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>());
            var mouse = Marshal.PtrToStructure<NativeMethods.RAWMOUSE>(body);
            var motion = _absoluteTracker.Process(
                mouse.usFlags,
                mouse.lLastX,
                mouse.lLastY,
                MouseAbsoluteCoordinateTracker.CaptureFromSystem(),
                _coordinateAligner);
            var absolute = (mouse.usFlags & MouseAbsoluteCoordinateTracker.MouseMoveAbsolute) != 0;
            var sample = new RawSample(
                _clock.NowMicroseconds,
                header.hDevice,
                mouse.usFlags,
                mouse.lLastX,
                mouse.lLastY,
                mouse.ulExtraInformation,
                mouse.buttons.usButtonFlags,
                mouse.buttons.usButtonData,
                motion.Dx,
                motion.Dy,
                motion.ScreenX,
                motion.ScreenY,
                motion.ScreenXMetrics,
                motion.ScreenYMetrics,
                motion.AlignmentMethod,
                motion.AlignmentScaleX,
                motion.AlignmentScaleY,
                motion.EstablishedBaselineOnly,
                absolute,
                (mouse.usFlags & MouseAbsoluteCoordinateTracker.MouseVirtualDesktop) != 0);

            lock (_statsSync)
            {
                if (absolute) _rawAbsolute++; else _rawRelative++;
                if (header.hDevice == IntPtr.Zero) _rawHDeviceZero++; else _rawHDeviceNonZero++;
            }
            _rawChannel.TryEnqueue(sample);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void HookLoop()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            var instance = NativeMethods.GetModuleHandle(null);
            _mouseHookProc ??= MouseHookProcedure;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseHookProc, instance, 0);
            if (_mouseHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LL mouse hook install failed.");
            _hookReady.Set();
            int rc;
            while ((rc = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            if (rc < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Hook diag GetMessage failed.");
        }
        catch (Exception ex) { _hookError = ex; }
        finally
        {
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _hookReady.Set();
        }
    }

    private IntPtr MouseHookProcedure(int code, UIntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var sample = new HookSample(
                    _clock.NowMicroseconds,
                    unchecked((uint)wParam.ToUInt64()),
                    data.pt.X,
                    data.pt.Y,
                    data.mouseData,
                    unchecked((int)data.flags),
                    data.dwExtraInfo.ToUInt64(),
                    data.time);
                _hookChannel.TryEnqueue(sample);
            }
            catch
            {
                // Never fail the hook chain.
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private async Task WriteRawLoopAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await foreach (var sample in _rawChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var deviceHandle = InputSourceClass.FormatDeviceHandle(sample.HDevice);
            var payload = new
            {
                channel = "raw_input",
                timestamp_us = sample.TimestampUs,
                hDevice = deviceHandle,
                usFlags = sample.UsFlags,
                mouse_move_absolute = sample.Absolute,
                mouse_virtual_desktop = sample.VirtualDesktop,
                lLastX = sample.LastX,
                lLastY = sample.LastY,
                ulExtraInformation = sample.ExtraInformation,
                usButtonFlags = sample.ButtonFlags,
                usButtonData = sample.ButtonData,
                dx = sample.Dx,
                dy = sample.Dy,
                screen_x_metrics = sample.ScreenXMetrics,
                screen_y_metrics = sample.ScreenYMetrics,
                screen_x = sample.ScreenX,
                screen_y = sample.ScreenY,
                screen_coordinate_note = sample.Absolute
                    ? "screen_x/y are hook-aligned; screen_x_metrics/y_metrics are GetSystemMetrics-mapped before alignment"
                    : null,
                alignment_method = sample.AlignmentMethod,
                alignment_scale_x = sample.AlignmentScaleX,
                alignment_scale_y = sample.AlignmentScaleY,
                established_baseline_only = sample.EstablishedBaselineOnly,
                source_class = ClassifyRaw(deviceHandle, sample.ExtraInformation)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteHookLoopAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await foreach (var sample in _hookChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var injected = (sample.Flags & NativeMethods.LLMHF_INJECTED) != 0;
            var lowerIl = (sample.Flags & NativeMethods.LLMHF_LOWER_IL_INJECTED) != 0;
            var executorTagged = unchecked((uint)sample.ExtraInfo) == unchecked((uint)WindowsSendInputBackend.ExecutorEventTag);
            lock (_statsSync)
            {
                if (injected) _hookInjected++;
                if (lowerIl) _hookLowerIlInjected++;
                if (executorTagged) _hookExtraInfoExecutorTag++;
            }
            var payload = new
            {
                channel = "ll_hook",
                timestamp_us = sample.TimestampUs,
                message = MessageName(sample.Message),
                message_id = sample.Message,
                pt_x = sample.PtX,
                pt_y = sample.PtY,
                mouseData = sample.MouseData,
                flags = sample.Flags,
                llmhf_injected = injected,
                llmhf_lower_il_injected = lowerIl,
                dwExtraInfo = sample.ExtraInfo,
                time = sample.Time,
                executor_event_tag = executorTagged
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private (object Report, List<DiagnosticsCorrelation.RawEvent> Raw, List<DiagnosticsCorrelation.HookEvent> Hooks) BuildCorrelationReport(
        string stimulusLabel,
        DiagnosticsStats stats)
    {
        var raw = ReadJsonl(Path.Combine(_outputDirectory, "raw-input.jsonl"))
            .Select(DiagnosticsCorrelation.TryParseRaw)
            .Where(x => x is not null)
            .Cast<DiagnosticsCorrelation.RawEvent>()
            .ToList();
        var hooks = ReadJsonl(Path.Combine(_outputDirectory, "ll-hook.jsonl"))
            .Select(DiagnosticsCorrelation.TryParseHook)
            .Where(x => x is not null)
            .Cast<DiagnosticsCorrelation.HookEvent>()
            .ToList();
        return (DiagnosticsCorrelation.BuildReport(raw, hooks, stimulusLabel, stats), raw, hooks);
    }

    private static string ClassifyRaw(string deviceHandle, uint extraInformation)
    {
        if (extraInformation == unchecked((uint)WindowsSendInputBackend.ExecutorEventTag))
            return InputSourceClass.ExecutorInjected;
        if (InputSourceClass.TryParseDeviceHandle(deviceHandle, out var handle) && handle != 0)
            return InputSourceClass.DeviceAttributed;
        return InputSourceClass.Unattributed;
    }

    private static string MessageName(uint message) => message switch
    {
        NativeMethods.WM_MOUSEMOVE => "WM_MOUSEMOVE",
        NativeMethods.WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
        NativeMethods.WM_LBUTTONUP => "WM_LBUTTONUP",
        NativeMethods.WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
        NativeMethods.WM_RBUTTONUP => "WM_RBUTTONUP",
        NativeMethods.WM_MBUTTONDOWN => "WM_MBUTTONDOWN",
        NativeMethods.WM_MBUTTONUP => "WM_MBUTTONUP",
        NativeMethods.WM_MOUSEWHEEL => "WM_MOUSEWHEEL",
        NativeMethods.WM_MOUSEHWHEEL => "WM_MOUSEHWHEEL",
        NativeMethods.WM_XBUTTONDOWN => "WM_XBUTTONDOWN",
        NativeMethods.WM_XBUTTONUP => "WM_XBUTTONUP",
        _ => $"WM_{message:X4}"
    };

    private static List<JsonElement> ReadJsonl(string path)
    {
        var list = new List<JsonElement>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                list.Add(doc.RootElement.Clone());
            }
            catch { /* skip */ }
        }
        return list;
    }

    internal readonly record struct RawSample(
        long TimestampUs,
        IntPtr HDevice,
        ushort UsFlags,
        int LastX,
        int LastY,
        uint ExtraInformation,
        ushort ButtonFlags,
        ushort ButtonData,
        int Dx,
        int Dy,
        int? ScreenX,
        int? ScreenY,
        int? ScreenXMetrics,
        int? ScreenYMetrics,
        string? AlignmentMethod,
        double? AlignmentScaleX,
        double? AlignmentScaleY,
        bool EstablishedBaselineOnly,
        bool Absolute,
        bool VirtualDesktop);

    internal readonly record struct HookSample(
        long TimestampUs,
        uint Message,
        int PtX,
        int PtY,
        uint MouseData,
        int Flags,
        ulong ExtraInfo,
        uint Time);
}

public sealed record DiagnosticsStats(
    long RawEvents,
    long HookEvents,
    long RawDropped,
    long HookDropped,
    long RawAbsolute,
    long RawRelative,
    long RawHDeviceZero,
    long RawHDeviceNonZero,
    long HookInjected,
    long HookLowerIlInjected,
    long HookExtraInfoExecutorTag);
