using System.Runtime.InteropServices;
using InputExecutor.Core.Abstractions;
using InputExecutor.Windows;
using InputExecutor.Windows.RemoteInputDiagnostics;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows only.");
    return 2;
}

var argsList = args.ToList();
var seconds = TakeInt(argsList, "--seconds") ?? 8;
var label = TakeOption(argsList, "--label") ?? "capture";
var controller = TakeOption(argsList, "--controller") ?? "UNKNOWN";
var outRoot = TakeOption(argsList, "--out") ?? Path.Combine("docs", "physical-input-sse", "uu-remote-diagnostics");
var stimulus = TakeOption(argsList, "--stimulus") ?? "none"; // none | untagged-mouse-event | tagged-sendinput

if (controller is not ("UNKNOWN" or "WINDOWS_CONTROLLER" or "MACOS_CONTROLLER"))
{
    Console.Error.WriteLine("--controller must be UNKNOWN | WINDOWS_CONTROLLER | MACOS_CONTROLLER");
    return 2;
}

var runDir = Path.GetFullPath(Path.Combine(outRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}"));
Directory.CreateDirectory(runDir);
await File.WriteAllTextAsync(Path.Combine(runDir, "run-meta.json"),
    System.Text.Json.JsonSerializer.Serialize(new
    {
        label,
        controller_platform = controller,
        seconds,
        stimulus,
        note = "SM_REMOTESESSION=false does not prove absence of UU/third-party remote control."
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Output: {runDir}");
Console.WriteLine($"Listening {seconds}s label={label} controller={controller} stimulus={stimulus}");
if (stimulus == "none")
    Console.WriteLine("Perform the requested mouse action NOW (UU remote or local as instructed).");

await using var session = new WindowsRemoteInputDiagnosticsSession(new SystemMonotonicClock(), runDir);
await session.StartAsync();

var deadline = DateTime.UtcNow.AddSeconds(seconds);
var nextStimulus = DateTime.UtcNow.AddMilliseconds(500);
var burstsLeft = stimulus is "untagged-mouse-event" or "tagged-sendinput" ? 4 : 0;
while (DateTime.UtcNow < deadline)
{
    if (burstsLeft > 0 && DateTime.UtcNow >= nextStimulus)
    {
        if (stimulus == "untagged-mouse-event") Stimulus.UntaggedMouseEventBurst();
        else if (stimulus == "tagged-sendinput") Stimulus.TaggedSendInputBurst();
        burstsLeft--;
        nextStimulus = DateTime.UtcNow.AddMilliseconds(400);
    }
    await Task.Delay(50);
}

await session.StopAsync();
var stats = session.SnapshotStats();
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(stats, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Done.");
return 0;

static string? TakeOption(List<string> values, string name)
{
    var i = values.FindIndex(v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= values.Count) return null;
    var value = values[i + 1];
    values.RemoveAt(i + 1);
    values.RemoveAt(i);
    return value;
}

static int? TakeInt(List<string> values, string name)
{
    var text = TakeOption(values, name);
    return text is not null && int.TryParse(text, out var n) ? n : null;
}

internal static class Stimulus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEEL = 0x0800;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void UntaggedMouseEventBurst()
    {
        mouse_event(MOVE, 20, 0, 0, UIntPtr.Zero);
        mouse_event(MOVE, 0, 15, 0, UIntPtr.Zero);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        mouse_event(WHEEL, 0, 0, unchecked((uint)120), UIntPtr.Zero);
        mouse_event(MOVE, unchecked((uint)(-20)), unchecked((uint)(-15)), 0, UIntPtr.Zero);
    }

    public static void TaggedSendInputBurst()
    {
        var tag = unchecked((UIntPtr)WindowsSendInputBackend.ExecutorEventTag);
        var inputs = new INPUT[6];
        for (var i = 0; i < inputs.Length; i++)
        {
            inputs[i].type = INPUT_MOUSE;
            inputs[i].U.mi.dwExtraInfo = tag;
        }
        inputs[0].U.mi.dwFlags = MOVE; inputs[0].U.mi.dx = 25;
        inputs[1].U.mi.dwFlags = MOVE; inputs[1].U.mi.dy = 20;
        inputs[2].U.mi.dwFlags = LEFTDOWN;
        inputs[3].U.mi.dwFlags = LEFTUP;
        inputs[4].U.mi.dwFlags = WHEEL; inputs[4].U.mi.mouseData = 120;
        inputs[5].U.mi.dwFlags = MOVE; inputs[5].U.mi.dx = -25; inputs[5].U.mi.dy = -20;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
