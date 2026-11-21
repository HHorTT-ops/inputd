using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows;

public sealed class WindowsRawInputRecorder : IHumanInputRecorder
{
    private const int MaximumWriteBatch = 1024;
    private const int WriteBatchWindowMilliseconds = 8;
    private const ushort MouseMoveAbsolute = MouseAbsoluteCoordinateTracker.MouseMoveAbsolute;
    private const ushort KeyBreak = 0x0001;
    private const ushort KeyE0 = 0x0002;
    private const ushort LeftDown = 0x0001, LeftUp = 0x0002, RightDown = 0x0004, RightUp = 0x0008;
    private const ushort MiddleDown = 0x0010, MiddleUp = 0x0020, X1Down = 0x0040, X1Up = 0x0080;
    private const ushort X2Down = 0x0100, X2Up = 0x0200, Wheel = 0x0400, HWheel = 0x0800;

    private readonly object _sync = new();
    private readonly object _diagnosticHookSync = new();
    private readonly HashSet<string> _heldKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _heldButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly MouseAbsoluteCoordinateTracker _absoluteTracker = new();
    private readonly MouseAbsoluteCoordinateTracker _taggedAbsoluteTracker = new();
    private readonly ITrajectorySink _sink;
    private readonly IMonotonicClock _clock;
    private readonly Channel<QueuedRecord> _records;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ManualResetEventSlim _windowReady = new(false);
    private readonly Thread _windowThread;
    private readonly Task _writerTask;
    private Thread? _diagnosticHookThread;
    private ManualResetEventSlim? _diagnosticHookReady;
    private NativeMethods.WindowProcedure? _windowProcedure;
    private NativeMethods.WindowProcedure? _diagnosticWindowProcedure;
    private NativeMethods.HookProcedure? _keyboardHookProcedure;
    private NativeMethods.HookProcedure? _mouseHookProcedure;
    private IntPtr _window;
    private IntPtr _diagnosticWindow;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private NativeMethods.POINT? _lastHookPoint;
    private uint _windowThreadId;
    private uint _diagnosticHookThreadId;
    private Exception? _startupError;
    private Exception? _diagnosticHookError;
    private RecordingContext? _active;
    private RecordingStatus _lastStatus = new();
    private bool _diagnosticHooksActive;
    private bool _disposed;

    public WindowsRawInputRecorder(ITrajectorySink sink, IMonotonicClock clock)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Raw Input is available only on Windows.");
        _sink = sink;
        _clock = clock;
        _records = Channel.CreateUnbounded<QueuedRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(WriteLoopAsync);
        _windowThread = new Thread(WindowLoop) { IsBackground = true, Name = "inputd-raw-input" };
        _windowThread.Start();
        if (!_windowReady.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Raw Input message window did not start.");
        if (_startupError is not null) throw new InvalidOperationException("Raw Input initialization failed.", _startupError);
    }

    public RecordingStatus Status
    {
        get
        {
            lock (_sync) return _active is null ? _lastStatus : ToStatus(_active, true);
        }
    }

    /// <summary>Raised for every observed physical Raw Input frame and every opted-in inputd-tagged diagnostic frame.</summary>
    public event EventHandler<HumanInputFrame>? FrameObserved;

    /// <summary>
    /// Diagnostic-only observation of inputd-tagged packets arriving through WM_INPUT.
    /// These frames are never added to physical held state or persisted as demonstrations.
    /// </summary>
    public event EventHandler<HumanInputFrame>? TaggedRawFrameObserved;

    public RecordingStatus Start(string sessionId, TargetBinding target, bool includeExecutorEvents)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
                throw new InputExecutorException("RECORDING_ACTIVE", $"Raw Input recording is already owned by session '{_active.SessionId}'.", 409);
            ConfigureDiagnosticHooks(includeExecutorEvents);
            _lastHookPoint = includeExecutorEvents && NativeMethods.GetCursorPos(out var point) ? point : null;
            _active = new RecordingContext(sessionId, target, includeExecutorEvents, DateTimeOffset.UtcNow);
            // Raw Input is observed for the recorder's entire lifetime, including while no
            // episode is active. Preserve that physical state at the episode boundary: a
            // key pressed just before Start must not disappear from every subsequent mouse
            // frame. Emit a snapshot so replay/training can establish the initial state even
            // when no new mouse packet arrives before the key is released.
            if (_heldKeys.Count != 0 || _heldButtons.Count != 0)
            {
                var timestampUs = _clock.NowMicroseconds;
                QueueLocked(_active, new HumanInputFrame
                {
                    Device = "state_snapshot",
                    TimestampUs = timestampUs,
                    Keyboard = new KeyboardFrame { Held = Sorted(_heldKeys) },
                    Mouse = new MouseFrame { HeldButtons = Sorted(_heldButtons) }
                });
            }
            _lastStatus = ToStatus(_active, true);
            return _lastStatus;
        }
    }

    public async Task<RecordingStatus> StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        RecordingContext context;
        lock (_sync)
        {
            if (_active is null) return _lastStatus;
            if (!string.Equals(_active.SessionId, sessionId, StringComparison.Ordinal))
                throw new InputExecutorException("RECORDING_OWNED_BY_OTHER_SESSION", "Only the session that started recording may stop it.", 403);
            context = _active;
            _active = null;
            context.RequestDrain();
            _lastStatus = ToStatus(context, false);
        }

        if (context.IncludeExecutorEvents)
        {
            try { ConfigureDiagnosticHooks(false); }
            catch (Exception ex) { context.MarkFault("Could not disable diagnostic input hooks: " + ex.Message); }
        }

        await context.Drained.Task.WaitAsync(cancellationToken);
        if (_sink is IBatchedTrajectorySink batched) await batched.FlushAsync(cancellationToken);
        lock (_sync) return _lastStatus = ToStatus(context, false);
    }

    private void WindowLoop()
    {
        try
        {
            _windowThreadId = NativeMethods.GetCurrentThreadId();
            _windowProcedure = WindowProcedure;
            var instance = NativeMethods.GetModuleHandle(null);
            var className = $"inputd.RawInput.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = _windowProcedure,
                hInstance = instance,
                lpszClassName = className,
                lpszMenuName = string.Empty
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = NativeMethods.CreateWindowEx(0, className, "inputd Raw Input", 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            var devices = new[]
            {
                new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _window },
                new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _window }
            };
            if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _windowReady.Set();
            int getMessageResult;
            while ((getMessageResult = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            if (getMessageResult < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw Input message loop failed.");
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _windowReady.Set();
        }
        finally
        {
            if (_window != IntPtr.Zero) NativeMethods.DestroyWindow(_window);
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_INPUT)
        {
            try { ProcessRawInput(lParam); }
            catch (Exception ex) { MarkCaptureFailure("Raw Input packet processing failed: " + ex.Message); }
        }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ConfigureDiagnosticHooks(bool enabled)
    {
        lock (_diagnosticHookSync)
        {
            if (Volatile.Read(ref _diagnosticHooksActive) == enabled) return;
            if (enabled)
            {
                _diagnosticHookError = null;
                _diagnosticHookReady = new ManualResetEventSlim(false);
                _diagnosticHookThread = new Thread(DiagnosticHookLoop)
                {
                    IsBackground = true,
                    Name = "inputd-diagnostic-hooks"
                };
                _diagnosticHookThread.Start();
                if (!_diagnosticHookReady.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The diagnostic hook thread did not start in time.");
                if (!Volatile.Read(ref _diagnosticHooksActive))
                    throw new InvalidOperationException("Low-level diagnostic hooks could not be installed.", _diagnosticHookError);
                return;
            }

            if (_diagnosticHookThreadId != 0)
                NativeMethods.PostThreadMessage(_diagnosticHookThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            if (_diagnosticHookThread is not null && !_diagnosticHookThread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The diagnostic hook thread did not stop in time.");
            _diagnosticHookReady?.Dispose();
            _diagnosticHookReady = null;
            _diagnosticHookThread = null;
            _diagnosticHookThreadId = 0;
            if (Volatile.Read(ref _diagnosticHooksActive))
                throw new InvalidOperationException("Low-level diagnostic hooks could not be removed.", _diagnosticHookError);
        }
    }

    private void DiagnosticHookLoop()
    {
        try
        {
            _diagnosticHookThreadId = NativeMethods.GetCurrentThreadId();
            _diagnosticWindowProcedure ??= DiagnosticWindowProcedure;
            var instance = NativeMethods.GetModuleHandle(null);
            var className = $"inputd.DiagnosticHooks.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = _diagnosticWindowProcedure,
                hInstance = instance,
                lpszClassName = className,
                lpszMenuName = string.Empty
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The diagnostic hook window class could not be registered.");
            _diagnosticWindow = NativeMethods.CreateWindowEx(0, className, "inputd Diagnostic Hooks", 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (_diagnosticWindow == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The diagnostic hook message window could not be created.");
            _keyboardHookProcedure ??= KeyboardHookProcedure;
            _mouseHookProcedure ??= MouseHookProcedure;
            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProcedure, instance, 0);
            if (_keyboardHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The diagnostic keyboard hook could not be installed.");
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseHookProcedure, instance, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                throw new Win32Exception(error, "The diagnostic mouse hook could not be installed.");
            }
            Volatile.Write(ref _diagnosticHooksActive, true);
            _diagnosticHookReady?.Set();
            int getMessageResult;
            while ((getMessageResult = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            if (getMessageResult < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The diagnostic hook message loop failed.");
        }
        catch (Exception ex) { _diagnosticHookError = ex; }
        finally
        {
            if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
            if (_diagnosticWindow != IntPtr.Zero) NativeMethods.DestroyWindow(_diagnosticWindow);
            _diagnosticWindow = IntPtr.Zero;
            Volatile.Write(ref _diagnosticHooksActive, false);
            _diagnosticHookReady?.Set();
        }
    }

    private static IntPtr DiagnosticWindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam) =>
        NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);

    private void ProcessRawInput(IntPtr rawHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        if (NativeMethods.GetRawInputData(rawHandle, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize)
        {
            MarkCaptureFailure("Raw Input packet size could not be read.");
            return;
        }
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(rawHandle, NativeMethods.RID_INPUT, buffer, ref size, headerSize) != size)
            {
                MarkCaptureFailure("Raw Input packet payload could not be read.");
                return;
            }
            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
            var body = IntPtr.Add(buffer, Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>());
            var deviceHandle = InputSourceClass.FormatDeviceHandle(header.hDevice);
            if (header.dwType == NativeMethods.RIM_TYPEMOUSE)
                ProcessMouse(Marshal.PtrToStructure<NativeMethods.RAWMOUSE>(body), deviceHandle);
            else if (header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                ProcessKeyboard(Marshal.PtrToStructure<NativeMethods.RAWKEYBOARD>(body), deviceHandle);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void ProcessKeyboard(NativeMethods.RAWKEYBOARD keyboard, string deviceHandle)
    {
        var tagged = keyboard.ExtraInformation == unchecked((uint)WindowsSendInputBackend.ExecutorEventTag);
        // Executor-generated input is recorded by the opt-in diagnostic hooks. It must never
        // mutate the physical held-key state used by production training trajectories.
        var isDown = (keyboard.Flags & KeyBreak) == 0;
        var extended = (keyboard.Flags & KeyE0) != 0;
        if (!PhysicalKeyMap.TryGetCode(keyboard.MakeCode, extended, out var code))
            code = extended ? $"ScanCodeE0_{keyboard.MakeCode:X4}" : $"ScanCode_{keyboard.MakeCode:X4}";
        if (tagged)
        {
            TaggedRawFrameObserved?.Invoke(this, new HumanInputFrame
            {
                Device = "tagged_raw_keyboard",
                TimestampUs = _clock.NowMicroseconds,
                ChangedKey = code,
                KeyDown = isDown,
                InjectedTagged = true,
                RawDeviceHandle = deviceHandle
            });
            return;
        }
        HumanInputFrame frame;
        lock (_sync)
        {
            var context = _active;
            if (isDown) _heldKeys.Add(code);
            else if (!_heldKeys.Remove(code))
            {
                // RegisterHotKey and episode boundaries can expose a key-up whose
                // matching key-down was consumed or occurred before recording began.
                // It produces no state change and must not become an orphan training edge.
                return;
            }
            frame = new HumanInputFrame
            {
                Device = "keyboard",
                TimestampUs = _clock.NowMicroseconds,
                Keyboard = new KeyboardFrame { Held = Sorted(_heldKeys) },
                Mouse = new MouseFrame { HeldButtons = Sorted(_heldButtons) },
                ChangedKey = code,
                KeyDown = isDown,
                InjectedTagged = tagged,
                RawDeviceHandle = deviceHandle
            };
            if (context is not null) QueueLocked(context, frame);
        }
        Notify(frame);
    }

    private void ProcessMouse(NativeMethods.RAWMOUSE mouse, string deviceHandle)
    {
        var tagged = mouse.ulExtraInformation == unchecked((uint)WindowsSendInputBackend.ExecutorEventTag);
        var flags = mouse.buttons.usButtonFlags;
        if (tagged)
        {
            var taggedMotion = _taggedAbsoluteTracker.Process(
                mouse.usFlags,
                mouse.lLastX,
                mouse.lLastY,
                MouseAbsoluteCoordinateTracker.CaptureFromSystem());
            var taggedDx = taggedMotion.Dx;
            var taggedDy = taggedMotion.Dy;
            var taggedWheelY = (flags & Wheel) != 0 ? unchecked((short)mouse.buttons.usButtonData) : 0;
            var taggedWheelX = (flags & HWheel) != 0 ? unchecked((short)mouse.buttons.usButtonData) : 0;
            // Keep click/wheel edges even when absolute baseline yields dx=dy=0.
            if (taggedDx != 0 || taggedDy != 0 || flags != 0)
            {
                TaggedRawFrameObserved?.Invoke(this, new HumanInputFrame
                {
                    Device = "tagged_raw_mouse",
                    TimestampUs = _clock.NowMicroseconds,
                    Mouse = new MouseFrame { Dx = taggedDx, Dy = taggedDy, WheelX = taggedWheelX, WheelY = taggedWheelY },
                    InjectedTagged = true,
                    RawDeviceHandle = deviceHandle
                });
            }
            return;
        }
        HumanInputFrame frame;
        lock (_sync)
        {
            var context = _active;
            UpdateButton(flags, LeftDown, LeftUp, "left");
            UpdateButton(flags, RightDown, RightUp, "right");
            UpdateButton(flags, MiddleDown, MiddleUp, "middle");
            UpdateButton(flags, X1Down, X1Up, "x1");
            UpdateButton(flags, X2Down, X2Up, "x2");
            var motion = _absoluteTracker.Process(
                mouse.usFlags,
                mouse.lLastX,
                mouse.lLastY,
                MouseAbsoluteCoordinateTracker.CaptureFromSystem());
            var dx = motion.Dx;
            var dy = motion.Dy;
            var wheelY = (flags & Wheel) != 0 ? unchecked((short)mouse.buttons.usButtonData) : 0;
            var wheelX = (flags & HWheel) != 0 ? unchecked((short)mouse.buttons.usButtonData) : 0;
            // Keep click/wheel edges even when absolute baseline is established with dx=dy=0.
            if (dx == 0 && dy == 0 && flags == 0) return;
            frame = new HumanInputFrame
            {
                Device = "mouse",
                TimestampUs = _clock.NowMicroseconds,
                Keyboard = new KeyboardFrame { Held = Sorted(_heldKeys) },
                Mouse = new MouseFrame { Dx = dx, Dy = dy, HeldButtons = Sorted(_heldButtons), WheelX = wheelX, WheelY = wheelY },
                InjectedTagged = tagged,
                RawDeviceHandle = deviceHandle
            };
            if (context is not null) QueueLocked(context, frame);
        }
        Notify(frame);
    }

    private IntPtr KeyboardHookProcedure(int code, UIntPtr wParam, IntPtr lParam)
    {
        HumanInputFrame? observed = null;
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (IsExecutorEvent(data.dwExtraInfo))
            {
                var message = unchecked((uint)wParam.ToUInt64());
                var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var extended = (data.flags & 0x01) != 0;
                    var mapped = PhysicalKeyMap.TryGetCode((ushort)data.scanCode, extended, out var codeName);
                    if (!mapped && data.vkCode == 0xE7) codeName = $"UnicodeU+{data.scanCode:X4}";
                    if (mapped || data.vkCode == 0xE7)
                    {
                        lock (_sync)
                        {
                            var context = _active;
                            if (isDown) _heldKeys.Add(codeName); else _heldKeys.Remove(codeName);
                            observed = new HumanInputFrame
                            {
                                Device = "keyboard_hook_injected",
                                TimestampUs = _clock.NowMicroseconds,
                                Keyboard = new KeyboardFrame { Held = Sorted(_heldKeys) },
                                Mouse = new MouseFrame { HeldButtons = Sorted(_heldButtons) },
                                ChangedKey = codeName,
                                KeyDown = isDown,
                                InjectedTagged = true
                            };
                            if (context is not null && context.IncludeExecutorEvents)
                                QueueLocked(context, observed);
                        }
                    }
                }
            }
        }
        if (observed is not null) Notify(observed);
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseHookProcedure(int code, UIntPtr wParam, IntPtr lParam)
    {
        HumanInputFrame? observed = null;
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (IsExecutorEvent(data.dwExtraInfo))
            {
                lock (_sync)
                {
                    var context = _active;
                    var message = unchecked((uint)wParam.ToUInt64());
                    var dx = _lastHookPoint is null ? 0 : data.pt.X - _lastHookPoint.Value.X;
                    var dy = _lastHookPoint is null ? 0 : data.pt.Y - _lastHookPoint.Value.Y;
                    _lastHookPoint = data.pt;
                    if (message == NativeMethods.WM_LBUTTONDOWN) _heldButtons.Add("left");
                    if (message == NativeMethods.WM_LBUTTONUP) _heldButtons.Remove("left");
                    if (message == NativeMethods.WM_RBUTTONDOWN) _heldButtons.Add("right");
                    if (message == NativeMethods.WM_RBUTTONUP) _heldButtons.Remove("right");
                    if (message == NativeMethods.WM_MBUTTONDOWN) _heldButtons.Add("middle");
                    if (message == NativeMethods.WM_MBUTTONUP) _heldButtons.Remove("middle");
                    if (message is NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP)
                    {
                        var button = ((data.mouseData >> 16) & 0xffff) == 1 ? "x1" : "x2";
                        if (message == NativeMethods.WM_XBUTTONDOWN) _heldButtons.Add(button); else _heldButtons.Remove(button);
                    }
                    var wheel = unchecked((short)(data.mouseData >> 16));
                    observed = new HumanInputFrame
                    {
                        Device = "mouse_hook_injected",
                        TimestampUs = _clock.NowMicroseconds,
                        Keyboard = new KeyboardFrame { Held = Sorted(_heldKeys) },
                        Mouse = new MouseFrame
                        {
                            Dx = message == NativeMethods.WM_MOUSEMOVE ? dx : 0,
                            Dy = message == NativeMethods.WM_MOUSEMOVE ? dy : 0,
                            HeldButtons = Sorted(_heldButtons),
                            WheelY = message == NativeMethods.WM_MOUSEWHEEL ? wheel : 0,
                            WheelX = message == NativeMethods.WM_MOUSEHWHEEL ? wheel : 0
                        },
                        InjectedTagged = true
                    };
                    if (context is not null && context.IncludeExecutorEvents)
                        QueueLocked(context, observed);
                }
            }
        }
        if (observed is not null) Notify(observed);
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void Notify(HumanInputFrame frame)
    {
        try { FrameObserved?.Invoke(this, frame); }
        catch { }
    }

    private void MarkCaptureFailure(string message)
    {
        lock (_sync)
        {
            if (_active is null) return;
            _active.MarkFailure(message);
            _lastStatus = ToStatus(_active, true);
        }
    }

    // RAWINPUT exposes only a 32-bit ExtraInformation value even on 64-bit Windows.
    // Compare the shared low 32 bits in both Raw Input and low-level hook paths so
    // inputd events are recognized consistently across native input APIs.
    private static bool IsExecutorEvent(UIntPtr extraInformation) =>
        unchecked((uint)extraInformation.ToUInt64()) == unchecked((uint)WindowsSendInputBackend.ExecutorEventTag);

    private void UpdateButton(ushort flags, ushort down, ushort up, string button)
    {
        if ((flags & down) != 0) _heldButtons.Add(button);
        if ((flags & up) != 0) _heldButtons.Remove(button);
    }

    private void QueueLocked(RecordingContext context, HumanInputFrame frame)
    {
        var sequence = ++context.Sequence;
        var record = new TrajectoryRecord
        {
            RecordedAtUtc = DateTimeOffset.UtcNow,
            SessionId = context.SessionId,
            Source = "human_raw_input",
            Sequence = sequence,
            ExecutorReceiveTimestampUs = frame.TimestampUs,
            ExecutorCompleteTimestampUs = frame.TimestampUs,
            ActionRequested = frame,
            Target = context.Target,
            Backend = "windows-raw-input"
        };
        if (_records.Writer.TryWrite(new QueuedRecord(context, record))) context.Enqueued++;
        else context.MarkFailure("The lossless recording queue was closed while recording was active.");
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<QueuedRecord>(MaximumWriteBatch);
        try
        {
            while (await _records.Reader.WaitToReadAsync(_lifetime.Token))
            {
                batch.Clear();
                if (!_records.Reader.TryRead(out var first)) continue;
                batch.Add(first);
                var batchDeadline = Environment.TickCount64 + WriteBatchWindowMilliseconds;
                while (batch.Count < MaximumWriteBatch)
                {
                    while (batch.Count < MaximumWriteBatch && _records.Reader.TryRead(out var queued)) batch.Add(queued);
                    if (batch.Count >= MaximumWriteBatch || Environment.TickCount64 >= batchDeadline) break;
                    await Task.Delay(1, _lifetime.Token);
                }

                try
                {
                    if (_sink is IBatchedTrajectorySink batched)
                        await batched.WriteBatchAsync(batch.Select(item => item.Record).ToArray(), CancellationToken.None);
                    else
                        foreach (var item in batch) await _sink.WriteAsync(item.Record, CancellationToken.None);
                    foreach (var item in batch) Interlocked.Increment(ref item.Context.Recorded);
                }
                catch (Exception ex)
                {
                    foreach (var item in batch) item.Context.MarkFailure(ex.Message);
                }
                finally
                {
                    foreach (var item in batch) item.Context.MarkCompleted();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private static string[] Sorted(HashSet<string> values) => values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    private RecordingStatus ToStatus(RecordingContext context, bool active) => new()
    {
        Active = active,
        SessionId = context.SessionId,
        StartedAtUtc = context.StartedAtUtc,
        EventsRecorded = Volatile.Read(ref context.Recorded),
        EventsDropped = Volatile.Read(ref context.Dropped),
        EventsPending = Math.Max(0, Volatile.Read(ref context.Enqueued) - Volatile.Read(ref context.Completed)),
        Faulted = context.Faulted,
        LastError = context.LastError,
        IncludeExecutorEvents = context.IncludeExecutorEvents,
        DiagnosticHooksActive = Volatile.Read(ref _diagnosticHooksActive)
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        RecordingContext? active;
        lock (_sync)
        {
            active = _active;
            _active = null;
            active?.RequestDrain();
        }
        if (active is not null) await active.Drained.Task;
        try { ConfigureDiagnosticHooks(false); } catch { }
        _records.Writer.TryComplete();
        await _writerTask;
        if (_sink is IBatchedTrajectorySink batched) await batched.FlushAsync(CancellationToken.None);
        if (_windowThreadId != 0) NativeMethods.PostThreadMessage(_windowThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _windowThread.Join(TimeSpan.FromSeconds(2));
        _lifetime.Cancel();
        _lifetime.Dispose();
        _windowReady.Dispose();
    }

    private sealed class RecordingContext(string sessionId, TargetBinding target, bool includeExecutorEvents, DateTimeOffset startedAtUtc)
    {
        internal string SessionId { get; } = sessionId;
        internal TargetBinding Target { get; } = target;
        internal bool IncludeExecutorEvents { get; } = includeExecutorEvents;
        internal DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        internal long Sequence;
        internal long Enqueued;
        internal long Completed;
        internal long Recorded;
        internal long Dropped;
        internal bool DrainRequested;
        internal long DrainTarget;
        internal string? LastError;
        internal bool Faulted;
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void RequestDrain()
        {
            DrainTarget = Volatile.Read(ref Enqueued);
            DrainRequested = true;
            if (Volatile.Read(ref Completed) >= DrainTarget) Drained.TrySetResult();
        }

        internal void MarkCompleted()
        {
            var completed = Interlocked.Increment(ref Completed);
            if (DrainRequested && completed >= Volatile.Read(ref DrainTarget)) Drained.TrySetResult();
        }

        internal void MarkFailure(string message)
        {
            Interlocked.Increment(ref Dropped);
            Faulted = true;
            LastError ??= message;
        }

        internal void MarkFault(string message)
        {
            Faulted = true;
            LastError ??= message;
        }
    }

    private sealed record QueuedRecord(RecordingContext Context, TrajectoryRecord Record);
}
