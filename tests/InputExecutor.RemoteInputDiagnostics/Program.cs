using System.Runtime.InteropServices;
using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Windows;
using InputExecutor.Windows.RemoteInputDiagnostics;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows only.");
    return 2;
}

var argsList = args.ToList();
var mode = TakeOption(argsList, "--mode") ?? "capture"; // capture | device-snapshot | device-compare | coordinate-snapshot
var seconds = TakeInt(argsList, "--seconds") ?? 8;
var label = TakeOption(argsList, "--label") ?? "capture";
var controller = TakeOption(argsList, "--controller") ?? "UNKNOWN";
var outRoot = TakeOption(argsList, "--out") ?? Path.Combine("docs", "physical-input-sse", "uu-remote-diagnostics");
var stimulus = TakeOption(argsList, "--stimulus") ?? "none"; // none | untagged-mouse-event | tagged-sendinput
var beforePath = TakeOption(argsList, "--before");
var afterPath = TakeOption(argsList, "--after");
var prompt = TakeOption(argsList, "--prompt");

if (controller is not ("UNKNOWN" or "WINDOWS_CONTROLLER" or "MACOS_CONTROLLER"))
{
    Console.Error.WriteLine("--controller must be UNKNOWN | WINDOWS_CONTROLLER | MACOS_CONTROLLER");
    return 2;
}

Directory.CreateDirectory(outRoot);

if (mode is "device-snapshot")
{
    var runDir = Path.GetFullPath(Path.Combine(outRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}-device-snapshot"));
    Directory.CreateDirectory(runDir);
    var snap = DeviceSnapshotCompare.CaptureDevicesOnly();
    var path = Path.Combine(runDir, "session-environment.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Device snapshot: {path}");
    Console.WriteLine($"devices={snap.RawInputDevices.Count}");
    return 0;
}

if (mode is "device-compare")
{
    if (string.IsNullOrWhiteSpace(beforePath) || string.IsNullOrWhiteSpace(afterPath))
    {
        Console.Error.WriteLine("device-compare requires --before and --after session-environment.json paths");
        return 2;
    }
    var runDir = Path.GetFullPath(Path.Combine(outRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}-device-compare"));
    Directory.CreateDirectory(runDir);
    var cmp = DeviceSnapshotCompare.CompareFiles(beforePath, afterPath);
    var path = Path.Combine(runDir, "device-compare.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(cmp, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Device compare: {path}");
    Console.WriteLine(JsonSerializer.Serialize(cmp, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (mode is "coordinate-snapshot")
{
    var runDir = Path.GetFullPath(Path.Combine(outRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}-coordinate-snapshot"));
    Directory.CreateDirectory(runDir);
    var coords = DisplayCoordinateDiagnostics.Capture();
    var path = Path.Combine(runDir, "coordinate-diagnostics.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(coords, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Coordinate diagnostics: {path}");
    return 0;
}

var runDirCapture = Path.GetFullPath(Path.Combine(outRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}"));
Directory.CreateDirectory(runDirCapture);
await File.WriteAllTextAsync(Path.Combine(runDirCapture, "run-meta.json"),
    JsonSerializer.Serialize(new
    {
        label,
        controller_platform = controller,
        seconds,
        stimulus,
        prompt,
        note = "SM_REMOTESESSION=false does not prove absence of UU/third-party remote control."
    }, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Output: {runDirCapture}");
Console.WriteLine($"Listening {seconds}s label={label} controller={controller} stimulus={stimulus}");
if (stimulus == "none")
{
    Console.WriteLine(prompt ?? "Perform the requested mouse action NOW (UU remote or local as instructed).");
}

await using var session = new WindowsRemoteInputDiagnosticsSession(new SystemMonotonicClock(), runDirCapture, stimulusLabel: label);
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
Console.WriteLine(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
var verdictPath = Path.Combine(runDirCapture, "stimulus-verdict.json");
if (File.Exists(verdictPath))
    Console.WriteLine("Verdict: " + File.ReadAllText(verdictPath));
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
