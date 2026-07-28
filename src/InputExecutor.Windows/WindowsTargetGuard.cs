using System.Diagnostics;
using System.Globalization;
using System.Text;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows;

public sealed class WindowsTargetGuard : ITargetGuard
{
    public TargetBinding CaptureForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new InputExecutorException("NO_FOREGROUND_WINDOW", "There is no foreground window to bind.", 409);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) throw new InputExecutorException("TARGET_PROCESS_NOT_FOUND", "Could not identify the foreground process.", 409);
        return CaptureWindow(hwnd, (int)pid);
    }

    public TargetBinding CaptureProcess(int processId)
    {
        if (processId <= 0) throw new InputExecutorException("INVALID_TARGET_PROCESS", "target_process_id must be positive.");
        try
        {
            using var process = Process.GetProcessById(processId);
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero) throw new InputExecutorException("TARGET_WINDOW_NOT_FOUND", "The process does not have a main window.", 409);
            return CaptureWindow(hwnd, processId);
        }
        catch (ArgumentException) { throw new InputExecutorException("TARGET_PROCESS_NOT_FOUND", "target_process_id does not identify a running process.", 404); }
    }

    public bool TryActivate(TargetBinding target)
    {
        if (!TryParseHandle(target.WindowHandle, out var hwnd) || hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd) return true;
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var attached = foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread &&
                       NativeMethods.AttachThreadInput(foregroundThread, targetThread, true);
        try
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(foregroundThread, targetThread, false);
        }
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    private static TargetBinding CaptureWindow(IntPtr hwnd, int processId)
    {
        using var process = Process.GetProcessById(processId);
        return new TargetBinding
        {
            WindowHandle = FormatHandle(hwnd),
            ProcessId = processId,
            ProcessName = process.ProcessName,
            ProcessPath = TryGetPath(process),
            ProcessStartTimeUtcTicks = TryGetStartTicks(process),
            WindowTitle = GetTitle(hwnd)
        };
    }

    public TargetCheck Validate(TargetBinding target, bool requireForeground)
    {
        if (!TryParseHandle(target.WindowHandle, out var hwnd) || hwnd == IntPtr.Zero)
            return new(false, "INVALID_WINDOW_HANDLE", "window_handle must be a non-zero hexadecimal or decimal HWND.");
        if (!NativeMethods.IsWindow(hwnd)) return new(false, "TARGET_WINDOW_CLOSED", "The bound target window no longer exists.");
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid != target.ProcessId) return new(false, "TARGET_IDENTITY_CHANGED", "The HWND now belongs to a different process.");
        if (requireForeground && NativeMethods.GetForegroundWindow() != hwnd)
            return new(false, "TARGET_NOT_FOREGROUND", "The bound target window is not the current foreground window.");

        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited) return new(false, "TARGET_PROCESS_EXITED", "The bound target process exited.");
            if (!SameProcessName(process.ProcessName, target.ProcessName))
                return new(false, "TARGET_IDENTITY_CHANGED", "The target process name changed.");
            if (target.ProcessStartTimeUtcTicks is not null && TryGetStartTicks(process) != target.ProcessStartTimeUtcTicks)
                return new(false, "TARGET_IDENTITY_CHANGED", "The target PID was reused by another process.");
            if (!string.IsNullOrWhiteSpace(target.ProcessPath))
            {
                var actual = TryGetPath(process);
                if (actual is null) return new(false, "TARGET_IDENTITY_UNAVAILABLE", "The target executable path cannot be verified at this integrity level.");
                if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(target.ProcessPath), StringComparison.OrdinalIgnoreCase))
                    return new(false, "TARGET_IDENTITY_CHANGED", "The target executable path changed.");
            }
        }
        catch (ArgumentException) { return new(false, "TARGET_PROCESS_EXITED", "The bound target process exited."); }
        catch (InvalidOperationException) { return new(false, "TARGET_PROCESS_EXITED", "The bound target process exited."); }

        return new(true);
    }

    public static bool TryParseHandle(string value, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        var style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text[2..]; style = NumberStyles.AllowHexSpecifier; }
        if (!long.TryParse(text, style, CultureInfo.InvariantCulture, out var raw)) return false;
        handle = new IntPtr(raw);
        return true;
    }

    public static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static string? TryGetPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static long? TryGetStartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch { return null; }
    }

    private static bool SameProcessName(string left, string right) =>
        string.Equals(Path.GetFileNameWithoutExtension(left), Path.GetFileNameWithoutExtension(right), StringComparison.OrdinalIgnoreCase);

    private static string GetTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
