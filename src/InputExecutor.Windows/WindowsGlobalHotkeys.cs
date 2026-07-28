using System.ComponentModel;

namespace InputExecutor.Windows;

[Flags]
public enum WindowsHotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

public static class WindowsGlobalHotkeys
{
    public const int MessageId = 0x0312;

    public static void Register(IntPtr windowHandle, int id, WindowsHotkeyModifiers modifiers, int virtualKey)
    {
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("A created window handle is required.", nameof(windowHandle));
        if (virtualKey is < 1 or > 0xff) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        var nativeModifiers = (uint)modifiers | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(windowHandle, id, nativeModifiers, (uint)virtualKey))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "The global hotkey is already in use or cannot be registered.");
    }

    public static void Unregister(IntPtr windowHandle, int id)
    {
        if (windowHandle != IntPtr.Zero) NativeMethods.UnregisterHotKey(windowHandle, id);
    }

    public static bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
