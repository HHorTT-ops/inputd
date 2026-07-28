using System.Runtime.InteropServices;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows;

[StructLayout(LayoutKind.Sequential)]
public struct InterceptionMouseStroke
{
    public ushort State;
    public ushort Flags;
    public short Rolling;
    public int X;
    public int Y;
    public uint Information;
}

public interface IInterceptionApi
{
    IntPtr CreateContext();
    void DestroyContext(IntPtr context);
    uint GetHardwareId(IntPtr context, int device, IntPtr buffer, uint bufferSize);
    int Send(IntPtr context, int device, ref InterceptionMouseStroke stroke, uint strokeCount);
}

/// <summary>
/// Hybrid backend for games that consume mouse movement from WM_INPUT/DirectInput.
/// Relative mouse reports, buttons, and wheels use the Interception mouse-class filter;
/// keyboard, text, and absolute desktop movement remain on the normal SendInput backend.
/// The third-party driver and interception.dll are intentionally not bundled or installed.
/// </summary>
public sealed partial class WindowsInterceptionInputBackend : IInputBackend, IDisposable
{
    private const int FirstMouseDevice = 11;
    private const int LastMouseDevice = 20;
    private const ushort MouseMoveRelative = 0x0000;
    private const ushort MouseMoveNoCoalesce = 0x0008;
    private const ushort LeftDown = 0x0001, LeftUp = 0x0002, RightDown = 0x0004, RightUp = 0x0008;
    private const ushort MiddleDown = 0x0010, MiddleUp = 0x0020, X1Down = 0x0040, X1Up = 0x0080;
    private const ushort X2Down = 0x0100, X2Up = 0x0200, Wheel = 0x0400, HWheel = 0x0800;

    private readonly IInputBackend _fallback;
    private readonly IInterceptionApi _api;
    private readonly IMonotonicClock _clock;
    private IntPtr _context;
    private readonly int _mouseDevice;
    private bool _disposed;

    public WindowsInterceptionInputBackend(IInputBackend fallback, IMonotonicClock? clock = null)
        : this(fallback, new NativeInterceptionApi(), clock) { }

    public WindowsInterceptionInputBackend(IInputBackend fallback, IInterceptionApi api, IMonotonicClock? clock = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Interception is available only on Windows.");
        _fallback = fallback;
        _api = api;
        _clock = clock ?? new SystemMonotonicClock();
        try { _context = _api.CreateContext(); }
        catch (DllNotFoundException ex)
        {
            throw new InputExecutorException("RAW_MOUSE_LIBRARY_MISSING", "找不到 interception.dll。游戏 Raw Input 回放需要用户明确安装并提供 Interception 运行库。", 503, inner: ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new InputExecutorException("RAW_MOUSE_LIBRARY_ARCHITECTURE", "interception.dll 架构与 inputd 不匹配，Windows x64 发布版需要 x64 DLL。", 503, inner: ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InputExecutorException("RAW_MOUSE_LIBRARY_INVALID", "interception.dll 缺少所需导出函数。", 503, inner: ex);
        }

        if (_context == IntPtr.Zero)
            throw new InputExecutorException("RAW_MOUSE_DRIVER_UNAVAILABLE", "Interception 驱动未安装、尚未重启生效，或当前进程无权访问驱动。", 503);
        try { _mouseDevice = FindMouseDevice(_context, _api); }
        catch
        {
            _api.DestroyContext(_context);
            _context = IntPtr.Zero;
            throw;
        }
        if (_mouseDevice == 0)
        {
            _api.DestroyContext(_context);
            _context = IntPtr.Zero;
            throw new InputExecutorException("RAW_MOUSE_DEVICE_NOT_FOUND", "Interception 驱动中没有可用的鼠标设备。", 503);
        }
    }

    public string Name => $"windows-interception-mouse:{_mouseDevice}+sendinput";
    public bool SupportsKey(string code) => _fallback.SupportsKey(code);

    public ValueTask<EmittedInput> MoveMouseRelativeAsync(int dx, int dy, bool noCoalesce, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stroke = new InterceptionMouseStroke
        {
            Flags = (ushort)(MouseMoveRelative | (noCoalesce ? MouseMoveNoCoalesce : 0)),
            X = dx,
            Y = dy,
            Information = unchecked((uint)WindowsSendInputBackend.ExecutorEventTag)
        };
        Send(ref stroke);
        return ValueTask.FromResult(Event("mouse_move_relative_raw", $"dx={dx},dy={dy},device={_mouseDevice}"));
    }

    public ValueTask<EmittedInput> MoveMouseAbsoluteAsync(int x, int y, CancellationToken cancellationToken) =>
        _fallback.MoveMouseAbsoluteAsync(x, y, cancellationToken);

    public ValueTask<EmittedInput> SetMouseButtonAsync(string button, bool isDown, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = (button.ToLowerInvariant(), isDown) switch
        {
            ("left", true) => LeftDown,
            ("left", false) => LeftUp,
            ("right", true) => RightDown,
            ("right", false) => RightUp,
            ("middle", true) => MiddleDown,
            ("middle", false) => MiddleUp,
            ("x1", true) => X1Down,
            ("x1", false) => X1Up,
            ("x2", true) => X2Down,
            ("x2", false) => X2Up,
            _ => throw new InputExecutorException("UNSUPPORTED_MOUSE_BUTTON", $"Unsupported mouse button '{button}'.")
        };
        var stroke = new InterceptionMouseStroke
        {
            State = state,
            Information = unchecked((uint)WindowsSendInputBackend.ExecutorEventTag)
        };
        Send(ref stroke);
        return ValueTask.FromResult(Event("mouse_button_raw", $"button={button},down={isDown},device={_mouseDevice}"));
    }

    public ValueTask<EmittedInput> ScrollMouseAsync(int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deltaY != 0) SendWheel(Wheel, deltaY);
        if (deltaX != 0) SendWheel(HWheel, deltaX);
        return ValueTask.FromResult(Event("mouse_wheel_raw", $"delta_x={deltaX},delta_y={deltaY},device={_mouseDevice}"));
    }

    public ValueTask<EmittedInput> SetKeyAsync(string code, bool isDown, CancellationToken cancellationToken) =>
        _fallback.SetKeyAsync(code, isDown, cancellationToken);

    public ValueTask<IReadOnlyList<EmittedInput>> TypeTextAsync(string text, CancellationToken cancellationToken) =>
        _fallback.TypeTextAsync(text, cancellationToken);

    private void SendWheel(ushort state, int delta)
    {
        while (delta != 0)
        {
            var part = (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
            var stroke = new InterceptionMouseStroke
            {
                State = state,
                Rolling = part,
                Information = unchecked((uint)WindowsSendInputBackend.ExecutorEventTag)
            };
            Send(ref stroke);
            delta -= part;
        }
    }

    private void Send(ref InterceptionMouseStroke stroke)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_api.Send(_context, _mouseDevice, ref stroke, 1) != 1)
            throw new InputExecutorException("RAW_MOUSE_INJECTION_FAILED", "Interception 驱动未接受鼠标报告。", 500,
                new { mouse_device = _mouseDevice, stroke.X, stroke.Y, stroke.State, stroke.Rolling });
    }

    private EmittedInput Event(string kind, string detail) => new()
    {
        Kind = kind,
        Detail = detail,
        EmittedAtUs = _clock.NowMicroseconds
    };

    private static int FindMouseDevice(IntPtr context, IInterceptionApi api)
    {
        var buffer = Marshal.AllocHGlobal(1_000);
        try
        {
            for (var device = FirstMouseDevice; device <= LastMouseDevice; device++)
                if (api.GetHardwareId(context, device, buffer, 1_000) > 0) return device;
            return 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context != IntPtr.Zero) _api.DestroyContext(_context);
        _context = IntPtr.Zero;
        if (_fallback is IDisposable disposable) disposable.Dispose();
    }

    private sealed partial class NativeInterceptionApi : IInterceptionApi
    {
        public IntPtr CreateContext() => interception_create_context();
        public void DestroyContext(IntPtr context) => interception_destroy_context(context);
        public uint GetHardwareId(IntPtr context, int device, IntPtr buffer, uint bufferSize) =>
            interception_get_hardware_id(context, device, buffer, bufferSize);
        public int Send(IntPtr context, int device, ref InterceptionMouseStroke stroke, uint strokeCount) =>
            interception_send(context, device, ref stroke, strokeCount);

        [LibraryImport("interception.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        private static partial IntPtr interception_create_context();

        [LibraryImport("interception.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        private static partial void interception_destroy_context(IntPtr context);

        [LibraryImport("interception.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        private static partial uint interception_get_hardware_id(IntPtr context, int device, IntPtr buffer, uint bufferSize);

        [LibraryImport("interception.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        private static partial int interception_send(IntPtr context, int device, ref InterceptionMouseStroke stroke, uint strokeCount);
    }
}
