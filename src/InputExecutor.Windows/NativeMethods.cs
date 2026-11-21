using System.Runtime.InteropServices;
using System.Text;

namespace InputExecutor.Windows;

internal static partial class NativeMethods
{
    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_XDOWN = 0x0080;
    internal const uint MOUSEEVENTF_XUP = 0x0100;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint XBUTTON1 = 0x0001;
    internal const uint XBUTTON2 = 0x0002;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const int SM_CMONITORS = 80;
    internal const int SM_REMOTESESSION = 0x1000;
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;
    internal const uint RID_HEADER = 0x10000005;
    internal const uint RIDI_DEVICENAME = 0x20000007;
    internal const uint RIDI_DEVICEINFO = 0x2000000B;
    internal const int LLMHF_INJECTED = 0x00000001;
    internal const int LLMHF_LOWER_IL_INJECTED = 0x00000002;
    internal const ushort MouseMoveAbsoluteFlag = 0x0001;
    internal const ushort MouseVirtualDesktopFlag = 0x0002;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_NOREPEAT = 0x4000;
    internal const uint WM_HOTKEY = 0x0312;
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_INPUT = 0x00FF;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_MBUTTONUP = 0x0208;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_XBUTTONDOWN = 0x020B;
    internal const uint WM_XBUTTONUP = 0x020C;
    internal const uint WM_MOUSEHWHEEL = 0x020E;
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const int SW_RESTORE = 9;
    internal const uint RID_INPUT = 0x10000003;
    internal const uint RIM_TYPEMOUSE = 0;
    internal const uint RIM_TYPEKEYBOARD = 1;
    internal const uint RIDEV_INPUTSINK = 0x00000100;
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint type;
        internal INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] internal MOUSEINPUT mi;
        [FieldOffset(0)] internal KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx;
        internal int dy;
        internal uint mouseData;
        internal uint dwFlags;
        internal uint time;
        internal UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort wVk;
        internal ushort wScan;
        internal uint dwFlags;
        internal uint time;
        internal UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal UIntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal POINT pt;
        internal uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { internal int Left; internal int Top; internal int Right; internal int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        internal ushort usUsagePage;
        internal ushort usUsage;
        internal uint dwFlags;
        internal IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        internal uint dwType;
        internal uint dwSize;
        internal IntPtr hDevice;
        internal UIntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RAWMOUSEBUTTONS
    {
        [FieldOffset(0)] internal uint ulButtons;
        [FieldOffset(0)] internal ushort usButtonFlags;
        [FieldOffset(2)] internal ushort usButtonData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWMOUSE
    {
        internal ushort usFlags;
        internal RAWMOUSEBUTTONS buttons;
        internal uint ulRawButtons;
        internal int lLastX;
        internal int lLastY;
        internal uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWKEYBOARD
    {
        internal ushort MakeCode;
        internal ushort Flags;
        internal ushort Reserved;
        internal ushort VKey;
        internal uint Message;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        internal uint vkCode;
        internal uint scanCode;
        internal uint flags;
        internal uint time;
        internal UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        internal POINT pt;
        internal uint mouseData;
        internal uint flags;
        internal uint time;
        internal UIntPtr dwExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr HookProcedure(int code, UIntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        internal uint cbSize;
        internal uint style;
        internal WindowProcedure lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    internal static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll", EntryPoint = "mouse_event")]
    internal static partial void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out MSG message, IntPtr hWnd, uint minFilter, uint maxFilter);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial IntPtr DispatchMessage(ref MSG message);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindowAsync(IntPtr hWnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICELIST
    {
        internal IntPtr hDevice;
        internal uint dwType;
    }

    internal enum WTS_INFO_CLASS
    {
        WTSClientProtocolType = 16
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr wParam, IntPtr lParam);
}
