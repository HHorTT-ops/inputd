using System.ComponentModel;

namespace InputExecutor.Windows;

public sealed class EmergencyHotkeyListener : IDisposable
{
    private const int HotkeyId = 0x4944;
    private const uint VirtualKeyF12 = 0x7B;
    private readonly Action _onTriggered;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _initialized = new(false);
    private uint _threadId;
    private bool _disposed;

    public EmergencyHotkeyListener(Action onTriggered)
    {
        _onTriggered = onTriggered;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "inputd-emergency-hotkey" };
        _thread.Start();
        if (!_initialized.Wait(TimeSpan.FromSeconds(5))) Failure = "Hotkey thread initialization timed out.";
    }

    public bool RegistrationSucceeded { get; private set; }
    public string? Failure { get; private set; }

    private void MessageLoop()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        RegistrationSucceeded = NativeMethods.RegisterHotKey(IntPtr.Zero, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VirtualKeyF12);
        if (!RegistrationSucceeded) Failure = new Win32Exception().Message;
        _initialized.Set();
        if (!RegistrationSucceeded) return;

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == NativeMethods.WM_HOTKEY && unchecked((int)message.wParam.ToUInt64()) == HotkeyId)
                ThreadPool.QueueUserWorkItem(_ => _onTriggered());
        }
        NativeMethods.UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _initialized.Dispose();
    }
}
