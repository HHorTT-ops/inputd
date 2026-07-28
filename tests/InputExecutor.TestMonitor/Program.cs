using System.Runtime.InteropServices;

namespace InputExecutor.TestMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MonitorForm());
    }
}

internal sealed class MonitorForm : Form
{
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 18),
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false
    };
    private readonly System.Windows.Forms.Timer _focusTimer = new() { Interval = 50 };
    private int _focusTicks;

    internal MonitorForm()
    {
        Text = "Inputd Test Monitor | waiting";
        ClientSize = new Size(900, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_text);
        _text.TextChanged += (_, _) => Text = "Inputd Test Monitor | " + _text.Text.Replace("\r", "").Replace("\n", " ↵ ");
        Shown += (_, _) =>
        {
            TopMost = true;
            _text.Focus();
            _focusTimer.Start();
        };
        _focusTimer.Tick += (_, _) =>
        {
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
            _text.Focus();
            if (++_focusTicks >= 80)
            {
                _focusTimer.Stop();
                TopMost = false;
            }
        };
    }
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr window);
}
