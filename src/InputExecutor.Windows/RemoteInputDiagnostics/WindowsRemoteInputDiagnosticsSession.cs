using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

/// <summary>
/// Independent dual-channel mouse input diagnostics (Raw Input + WH_MOUSE_LL).
/// Hook callbacks only enqueue; JSONL is written asynchronously. CallNextHookEx always runs.
/// </summary>
public sealed class WindowsRemoteInputDiagnosticsSession : IAsyncDisposable
{
    private const int DiagnosticChannelCapacity = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IMonotonicClock _clock;
    private readonly string _outputDirectory;
    private readonly MouseAbsoluteCoordinateTracker _absoluteTracker = new();
    private readonly Channel<string> _rawLines;
    private readonly Channel<string> _hookLines;
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
    private long _rawEvents;
    private long _hookEvents;
    private long _rawDropped;
    private long _hookDropped;
    private long _rawAbsolute;
    private long _rawRelative;
    private long _rawHDeviceZero;
    private long _rawHDeviceNonZero;
    private long _hookInjected;
    private long _hookLowerIlInjected;
    private long _hookExtraInfoExecutorTag;

    public WindowsRemoteInputDiagnosticsSession(IMonotonicClock clock, string outputDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _clock = clock;
        _outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
        _rawLines = Channel.CreateBounded<string>(new BoundedChannelOptions(DiagnosticChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _hookLines = Channel.CreateBounded<string>(new BoundedChannelOptions(DiagnosticChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var env = SessionEnvironmentSnapshot.Capture();
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "session-environment.json"),
            JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        _rawWriter = Task.Run(() => WriteLoopAsync(_rawLines.Reader, Path.Combine(_outputDirectory, "raw-input.jsonl"), _lifetime.Token));
        _hookWriter = Task.Run(() => WriteLoopAsync(_hookLines.Reader, Path.Combine(_outputDirectory, "ll-hook.jsonl"), _lifetime.Token));

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
                _rawEvents, _hookEvents, _rawDropped, _hookDropped,
                _rawAbsolute, _rawRelative, _rawHDeviceZero, _rawHDeviceNonZero,
                _hookInjected, _hookLowerIlInjected, _hookExtraInfoExecutorTag);
        }
    }

    public async Task StopAsync()
    {
        if (_rawThreadId != 0) NativeMethods.PostThreadMessage(_rawThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (_hookThreadId != 0) NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _rawThread?.Join(TimeSpan.FromSeconds(3));
        _hookThread?.Join(TimeSpan.FromSeconds(3));
        _rawLines.Writer.TryComplete();
        _hookLines.Writer.TryComplete();
        if (_rawWriter is not null) await _rawWriter.ConfigureAwait(false);
        if (_hookWriter is not null) await _hookWriter.ConfigureAwait(false);

        var stats = SnapshotStats();
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "channel-stats.json"),
            JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        var correlation = BuildCorrelationReport();
        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "correlation-report.json"),
            JsonSerializer.Serialize(correlation, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
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
            var deviceHandle = InputSourceClass.FormatDeviceHandle(header.hDevice);
            var motion = _absoluteTracker.Process(mouse.usFlags, mouse.lLastX, mouse.lLastY, MouseAbsoluteCoordinateTracker.CaptureFromSystem());
            var absolute = (mouse.usFlags & MouseAbsoluteCoordinateTracker.MouseMoveAbsolute) != 0;
            var virtualDesktop = (mouse.usFlags & MouseAbsoluteCoordinateTracker.MouseVirtualDesktop) != 0;
            var buttonFlags = mouse.buttons.usButtonFlags;
            var buttonData = mouse.buttons.usButtonData;
            var payload = new
            {
                channel = "raw_input",
                timestamp_us = _clock.NowMicroseconds,
                hDevice = deviceHandle,
                usFlags = mouse.usFlags,
                mouse_move_absolute = absolute,
                mouse_virtual_desktop = virtualDesktop,
                lLastX = mouse.lLastX,
                lLastY = mouse.lLastY,
                ulExtraInformation = mouse.ulExtraInformation,
                usButtonFlags = buttonFlags,
                usButtonData = buttonData,
                dx = motion.Dx,
                dy = motion.Dy,
                screen_x = motion.ScreenX,
                screen_y = motion.ScreenY,
                established_baseline_only = motion.EstablishedBaselineOnly,
                source_class = ClassifyRaw(deviceHandle, mouse.ulExtraInformation)
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            lock (_statsSync)
            {
                _rawEvents++;
                if (absolute) _rawAbsolute++; else _rawRelative++;
                if (header.hDevice == IntPtr.Zero) _rawHDeviceZero++; else _rawHDeviceNonZero++;
                if (!_rawLines.Writer.TryWrite(json)) _rawDropped++;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void HookLoop()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            // Message-only window keeps a message pump for the hook thread.
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
                var message = unchecked((uint)wParam.ToUInt64());
                var flags = unchecked((int)data.flags);
                var injected = (flags & NativeMethods.LLMHF_INJECTED) != 0;
                var lowerIl = (flags & NativeMethods.LLMHF_LOWER_IL_INJECTED) != 0;
                var extra = data.dwExtraInfo.ToUInt64();
                var executorTagged = unchecked((uint)extra) == unchecked((uint)WindowsSendInputBackend.ExecutorEventTag);
                var payload = new
                {
                    channel = "ll_hook",
                    timestamp_us = _clock.NowMicroseconds,
                    message = MessageName(message),
                    message_id = message,
                    pt_x = data.pt.X,
                    pt_y = data.pt.Y,
                    mouseData = data.mouseData,
                    flags,
                    llmhf_injected = injected,
                    llmhf_lower_il_injected = lowerIl,
                    dwExtraInfo = extra,
                    time = data.time,
                    executor_event_tag = executorTagged
                };
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                lock (_statsSync)
                {
                    _hookEvents++;
                    if (injected) _hookInjected++;
                    if (lowerIl) _hookLowerIlInjected++;
                    if (executorTagged) _hookExtraInfoExecutorTag++;
                    if (!_hookLines.Writer.TryWrite(json)) _hookDropped++;
                }
            }
            catch
            {
                // Never fail the hook chain.
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
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

    private static async Task WriteLoopAsync(ChannelReader<string> reader, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await foreach (var line in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private object BuildCorrelationReport()
    {
        var rawPath = Path.Combine(_outputDirectory, "raw-input.jsonl");
        var hookPath = Path.Combine(_outputDirectory, "ll-hook.jsonl");
        var raw = ReadJsonl(rawPath);
        var hooks = ReadJsonl(hookPath);
        var matchedWithin20ms = 0;
        var rawWithInjectedHookNearby = 0;
        var samplePairs = new List<object>();
        foreach (var r in raw.Take(500))
        {
            if (!r.TryGetProperty("timestamp_us", out var rts)) continue;
            var t = rts.GetInt64();
            var near = hooks.Where(h =>
            {
                if (!h.TryGetProperty("timestamp_us", out var hts)) return false;
                return Math.Abs(hts.GetInt64() - t) <= 20_000;
            }).ToList();
            if (near.Count == 0) continue;
            matchedWithin20ms++;
            if (near.Any(h => h.TryGetProperty("llmhf_injected", out var inj) && inj.GetBoolean()))
                rawWithInjectedHookNearby++;
            if (samplePairs.Count < 8)
            {
                samplePairs.Add(new
                {
                    raw_timestamp_us = t,
                    raw_hDevice = r.TryGetProperty("hDevice", out var hd) ? hd.GetString() : null,
                    raw_absolute = r.TryGetProperty("mouse_move_absolute", out var abs) && abs.GetBoolean(),
                    nearby_hooks = near.Take(3).Select(h => new
                    {
                        timestamp_us = h.GetProperty("timestamp_us").GetInt64(),
                        message = h.TryGetProperty("message", out var m) ? m.GetString() : null,
                        llmhf_injected = h.TryGetProperty("llmhf_injected", out var i) && i.GetBoolean()
                    })
                });
            }
        }

        var stats = SnapshotStats();
        return new
        {
            raw_events = raw.Count,
            hook_events = hooks.Count,
            matched_raw_with_hook_within_20ms = matchedWithin20ms,
            matched_raw_near_injected_hook = rawWithInjectedHookNearby,
            stats,
            sample_pairs = samplePairs,
            answers_preliminary = new
            {
                uu_mouse_enters_raw_input = raw.Count > 0 ? "OBSERVED_RAW_EVENTS_PRESENT_IF_STIMULUS_WAS_UU" : "NO_RAW_EVENTS",
                llmhf_injected_seen = stats.HookInjected > 0,
                hDevice_always_zero = stats.RawHDeviceNonZero == 0 && stats.RawEvents > 0,
                absolute_vs_relative = new { absolute = stats.RawAbsolute, relative = stats.RawRelative },
                note = "Cross-check with per-test labels; do not conclude from a single metric."
            }
        };
    }

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
            catch { /* skip bad line */ }
        }
        return list;
    }
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
