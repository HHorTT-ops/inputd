using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using InputExecutor.Core.Abstractions;
using InputExecutor.Scheduler;
using InputExecutor.Windows;

if (args.Length != 3 || !int.TryParse(args[1], out var processId))
{
    Console.Error.WriteLine("Usage: inputd-game-acceptance <trajectory.jsonl> <process-id> <output-directory>");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[2]);
if (!File.Exists(sourcePath)) throw new FileNotFoundException("Trajectory not found.", sourcePath);
Directory.CreateDirectory(outputDirectory);

var mousePath = Path.Combine(outputDirectory, "mouse-only.jsonl");
var keyPath = Path.Combine(outputDirectory, "w-only.jsonl");
var sourceStats = await CreateIsolatedTrajectoriesAsync(sourcePath, mousePath, keyPath);

var clock = new SystemMonotonicClock();
var guard = new WindowsTargetGuard();
var target = guard.CaptureProcess(processId);
if (!guard.TryActivate(target)) throw new InvalidOperationException("Could not activate the exact game window.");
await Task.Delay(500);

var beforeMousePath = Path.Combine(outputDirectory, "mouse-before.png");
var afterMousePath = Path.Combine(outputDirectory, "mouse-after.png");
var beforeWPath = Path.Combine(outputDirectory, "w-before.png");
var afterWPath = Path.Combine(outputDirectory, "w-after.png");

CaptureWindow(target.WindowHandle, beforeMousePath);
TrajectoryReplaySummary mouseSummary;
TrajectoryReplaySummary keySummary;
var mouseElapsed = Stopwatch.StartNew();
await using (var scheduler = new InputScheduler(
                 new WindowsLegacyMouseReplayBackend(
                     new WindowsSendInputBackend(clock, new WindowsSendInputOptions(TagInjectedEvents: false)), clock), clock))
{
    var player = new TrajectoryReplayPlayer(scheduler, guard, clock);
    mouseSummary = await player.ReplayAsync(mousePath, target);
}
mouseElapsed.Stop();
await Task.Delay(500);
CaptureWindow(target.WindowHandle, afterMousePath);

CaptureWindow(target.WindowHandle, beforeWPath);
var keyElapsed = Stopwatch.StartNew();
await using (var scheduler = new InputScheduler(
                 new WindowsLegacyMouseReplayBackend(
                     new WindowsSendInputBackend(clock, new WindowsSendInputOptions(TagInjectedEvents: false)), clock), clock))
{
    var player = new TrajectoryReplayPlayer(scheduler, guard, clock);
    keySummary = await player.ReplayAsync(keyPath, target);
}
keyElapsed.Stop();
await Task.Delay(500);
CaptureWindow(target.WindowHandle, afterWPath);

var report = new
{
    source = sourcePath,
    target = new { target.ProcessId, target.ProcessName, target.WindowTitle, target.WindowHandle },
    source_stats = sourceStats,
    mouse_replay = new
    {
        backend = "windows-legacy-mouse-replay",
        summary = mouseSummary,
        elapsed_ms = mouseElapsed.Elapsed.TotalMilliseconds,
        mean_sampled_rgb_difference = ImageDifference(beforeMousePath, afterMousePath),
        before = beforeMousePath,
        after = afterMousePath
    },
    w_replay = new
    {
        backend = "windows-legacy-mouse-replay",
        summary = keySummary,
        elapsed_ms = keyElapsed.Elapsed.TotalMilliseconds,
        mean_sampled_rgb_difference = ImageDifference(beforeWPath, afterWPath),
        before = beforeWPath,
        after = afterWPath
    },
    completed_at_utc = DateTimeOffset.UtcNow
};
var reportPath = Path.Combine(outputDirectory, "acceptance.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
}));
Console.WriteLine(reportPath);
return 0;

static async Task<object> CreateIsolatedTrajectoriesAsync(string source, string mouseOutput, string keyOutput)
{
    var mouseLines = new List<string>();
    var keyLines = new List<string>();
    long mouseDx = 0, mouseDy = 0;
    long? firstW = null, lastW = null;
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    foreach (var line in await File.ReadAllLinesAsync(source))
    {
        if (string.IsNullOrWhiteSpace(line) || JsonNode.Parse(line) is not JsonObject root ||
            root["action_requested"] is not JsonObject action || action["mouse"] is not JsonObject mouse)
            continue;

        var dx = mouse["dx"]?.GetValue<int>() ?? 0;
        var dy = mouse["dy"]?.GetValue<int>() ?? 0;
        var wheelX = mouse["wheel_x"]?.GetValue<int>() ?? 0;
        var wheelY = mouse["wheel_y"]?.GetValue<int>() ?? 0;
        var changedKey = action["changed_key"]?.GetValue<string>();
        var timestampUs = action["timestamp_us"]?.GetValue<long>() ?? 0;

        if (dx != 0 || dy != 0 || wheelX != 0 || wheelY != 0)
        {
            var copy = JsonNode.Parse(line)!.AsObject();
            var copyAction = copy["action_requested"]!.AsObject();
            copy["sequence"] = mouseLines.Count + 1L;
            copyAction["device"] = "mouse";
            copyAction["changed_key"] = null;
            copyAction["key_down"] = null;
            copyAction["keyboard"]!["held"] = new JsonArray();
            copyAction["mouse"]!["held_buttons"] = new JsonArray();
            mouseLines.Add(copy.ToJsonString(options));
            mouseDx = checked(mouseDx + dx);
            mouseDy = checked(mouseDy + dy);
        }

        if (string.Equals(changedKey, "KeyW", StringComparison.OrdinalIgnoreCase))
        {
            var copy = JsonNode.Parse(line)!.AsObject();
            var copyAction = copy["action_requested"]!.AsObject();
            copy["sequence"] = keyLines.Count + 1L;
            copyAction["device"] = "keyboard";
            copyAction["mouse"]!["dx"] = 0;
            copyAction["mouse"]!["dy"] = 0;
            copyAction["mouse"]!["wheel_x"] = 0;
            copyAction["mouse"]!["wheel_y"] = 0;
            copyAction["mouse"]!["held_buttons"] = new JsonArray();
            keyLines.Add(copy.ToJsonString(options));
            firstW ??= timestampUs;
            if (copyAction["key_down"]?.GetValue<bool>() == false) lastW = timestampUs;
        }
    }

    if (mouseLines.Count == 0) throw new InvalidDataException("Source trajectory has no mouse movement.");
    if (keyLines.Count < 2 || firstW is null || lastW is null) throw new InvalidDataException("Source trajectory has no complete KeyW hold.");
    await File.WriteAllLinesAsync(mouseOutput, mouseLines);
    await File.WriteAllLinesAsync(keyOutput, keyLines);
    return new
    {
        mouse_events = mouseLines.Count,
        total_mouse_dx = mouseDx,
        total_mouse_dy = mouseDy,
        w_packets = keyLines.Count,
        w_hold_microseconds = lastW.Value - firstW.Value
    };
}

static void CaptureWindow(string windowHandle, string path)
{
    var handle = new IntPtr(Convert.ToInt64(windowHandle[2..], 16));
    if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("GetWindowRect failed.");
    var width = rect.Right - rect.Left;
    var height = rect.Bottom - rect.Top;
    using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
    bitmap.Save(path, ImageFormat.Png);
}

static double ImageDifference(string leftPath, string rightPath)
{
    using var left = new Bitmap(leftPath);
    using var right = new Bitmap(rightPath);
    if (left.Size != right.Size) return double.PositiveInfinity;
    long total = 0, samples = 0;
    var xStart = left.Width / 10;
    var xEnd = left.Width * 9 / 10;
    var yStart = left.Height / 10;
    var yEnd = left.Height * 9 / 10;
    for (var y = yStart; y < yEnd; y += 4)
    for (var x = xStart; x < xEnd; x += 4)
    {
        var a = left.GetPixel(x, y);
        var b = right.GetPixel(x, y);
        total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        samples += 3;
    }
    return samples == 0 ? 0 : (double)total / samples;
}

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

[StructLayout(LayoutKind.Sequential)]
struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
