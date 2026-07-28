using System.ComponentModel;
using System.Runtime.InteropServices;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows;

public sealed record WindowsSendInputOptions(bool TagInjectedEvents = true);

public sealed class WindowsSendInputBackend : IInputBackend
{
    // Visible in diagnostics and Raw Input metadata; this is not a security secret.
    public const ulong ExecutorEventTag = 0x494E505554440001;
    private readonly IMonotonicClock _clock;
    private readonly UIntPtr _extraInfo;

    public WindowsSendInputBackend(IMonotonicClock? clock = null, WindowsSendInputOptions? options = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SendInput is available only on Windows.");
        _clock = clock ?? new SystemMonotonicClock();
        options ??= new WindowsSendInputOptions();
        _extraInfo = options.TagInjectedEvents ? unchecked((UIntPtr)ExecutorEventTag) : UIntPtr.Zero;
    }

    public string Name => _extraInfo == UIntPtr.Zero ? "windows-sendinput-game-replay" : "windows-sendinput";
    public bool SupportsKey(string code) => PhysicalKeyMap.TryGet(code, out _);

    public ValueTask<EmittedInput> MoveMouseRelativeAsync(int dx, int dy, bool noCoalesce, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var flags = NativeMethods.MOUSEEVENTF_MOVE | (noCoalesce ? NativeMethods.MOUSEEVENTF_MOVE_NOCOALESCE : 0);
        Send(Mouse(dx, dy, 0, flags));
        return ValueTask.FromResult(Event("mouse_move_relative", $"dx={dx},dy={dy},no_coalesce={noCoalesce}"));
    }

    public ValueTask<EmittedInput> MoveMouseAbsoluteAsync(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (width <= 1 || height <= 1) throw new InputExecutorException("DISPLAY_METRICS_FAILED", "Could not read virtual desktop metrics.", 500);
        if (x < left || x >= left + width || y < top || y >= top + height)
            throw new InputExecutorException("ABSOLUTE_COORDINATE_OUT_OF_RANGE", $"Coordinate ({x},{y}) is outside the virtual desktop.");

        var normalizedX = (int)Math.Round((x - left) * 65535d / (width - 1));
        var normalizedY = (int)Math.Round((y - top) * 65535d / (height - 1));
        Send(Mouse(normalizedX, normalizedY, 0,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK));
        return ValueTask.FromResult(Event("mouse_move_absolute", $"x={x},y={y}"));
    }

    public ValueTask<EmittedInput> SetMouseButtonAsync(string button, bool isDown, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (flags, data) = (button.ToLowerInvariant(), isDown) switch
        {
            ("left", true) => (NativeMethods.MOUSEEVENTF_LEFTDOWN, 0u),
            ("left", false) => (NativeMethods.MOUSEEVENTF_LEFTUP, 0u),
            ("right", true) => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0u),
            ("right", false) => (NativeMethods.MOUSEEVENTF_RIGHTUP, 0u),
            ("middle", true) => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, 0u),
            ("middle", false) => (NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
            ("x1", true) => (NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.XBUTTON1),
            ("x1", false) => (NativeMethods.MOUSEEVENTF_XUP, NativeMethods.XBUTTON1),
            ("x2", true) => (NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.XBUTTON2),
            ("x2", false) => (NativeMethods.MOUSEEVENTF_XUP, NativeMethods.XBUTTON2),
            _ => throw new InputExecutorException("UNSUPPORTED_MOUSE_BUTTON", $"Unsupported mouse button '{button}'.")
        };
        Send(Mouse(0, 0, data, flags));
        return ValueTask.FromResult(Event("mouse_button", $"button={button},down={isDown}"));
    }

    public ValueTask<EmittedInput> ScrollMouseAsync(int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new List<NativeMethods.INPUT>(2);
        if (deltaY != 0) inputs.Add(Mouse(0, 0, unchecked((uint)deltaY), NativeMethods.MOUSEEVENTF_WHEEL));
        if (deltaX != 0) inputs.Add(Mouse(0, 0, unchecked((uint)deltaX), NativeMethods.MOUSEEVENTF_HWHEEL));
        if (inputs.Count != 0) Send(inputs.ToArray());
        return ValueTask.FromResult(Event("mouse_wheel", $"delta_x={deltaX},delta_y={deltaY}"));
    }

    public ValueTask<EmittedInput> SetKeyAsync(string code, bool isDown, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PhysicalKeyMap.TryGet(code, out var scan))
            throw new InputExecutorException("UNSUPPORTED_KEY", $"Unsupported physical key code '{code}'.");
        var flags = NativeMethods.KEYEVENTF_SCANCODE |
                    (scan.Extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0) |
                    (!isDown ? NativeMethods.KEYEVENTF_KEYUP : 0);
        Send(Key(scan.Value, flags));
        return ValueTask.FromResult(Event("key", $"code={code},down={isDown}"));
    }

    public ValueTask<IReadOnlyList<EmittedInput>> TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length == 0) return ValueTask.FromResult<IReadOnlyList<EmittedInput>>(Array.Empty<EmittedInput>());
        var inputs = new NativeMethods.INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Key(text[i], NativeMethods.KEYEVENTF_UNICODE);
            inputs[i * 2 + 1] = Key(text[i], NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
        }
        Send(inputs);
        return ValueTask.FromResult<IReadOnlyList<EmittedInput>>(new[] { Event("text", $"utf16_code_units={text.Length}") });
    }

    private EmittedInput Event(string kind, string detail) => new() { Kind = kind, Detail = detail, EmittedAtUs = _clock.NowMicroseconds };

    private NativeMethods.INPUT Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        U = new NativeMethods.INPUTUNION
        {
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = _extraInfo
            }
        }
    };

    private NativeMethods.INPUT Key(ushort scanCode, uint flags) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = scanCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = _extraInfo
            }
        }
    };

    private static void Send(params NativeMethods.INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent == inputs.Length) return;
        var error = Marshal.GetLastWin32Error();
        var message = error == 0
            ? "SendInput returned zero without a Win32 error. A UIPI integrity-level boundary may have blocked injection."
            : new Win32Exception(error).Message;
        throw new InputExecutorException("INPUT_INJECTION_FAILED", message, 500,
            new { requested = inputs.Length, sent, win32_error = error });
    }
}
