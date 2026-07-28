using System.Diagnostics;
using System.Text.Json;
using InputExecutor.ObsBridge;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("inputd-obs-bridge requires Windows.");
    return 2;
}

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
var parentPid = ReadIntOption(args, "--parent-pid");

if (command == "register")
{
    if (Process.GetProcessesByName("obs64").Length != 0)
    {
        Console.Error.WriteLine("Close OBS before registering the script so its saved scene collection is not overwritten.");
        return 3;
    }
    var scene = ReadStringOption(args, "--scene") ?? throw new ArgumentException("Provide --scene.");
    var script = ReadStringOption(args, "--script") ?? throw new ArgumentException("Provide --script.");
    var bridge = ReadStringOption(args, "--bridge") ?? throw new ArgumentException("Provide --bridge.");
    var registered = await ObsScriptRegistration.RegisterAsync(scene, script, bridge);
    Console.WriteLine(JsonSerializer.Serialize(registered, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (command == "serve")
{
    using var mutex = new Mutex(initiallyOwned: true, "Local\\inputd-obs-bridge-v1", out var ownsMutex);
    if (!ownsMutex) return 0;
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
    if (parentPid is > 0) _ = MonitorParentAsync(parentPid.Value, shutdown);
    await using var controller = ObsRecordingController.CreateWindows();
    try { await ObsBridgePipe.RunServerAsync(controller, shutdown.Token); }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    return 0;
}

if (command == "ensure")
{
    await ObsBridgePipe.EnsureServerAsync(parentPid, CancellationToken.None);
    return 0;
}

await ObsBridgePipe.EnsureServerAsync(ReadIntOption(args, "--obs-pid"), CancellationToken.None);
var request = new ObsBridgeRequest
{
    Action = command,
    VideoPath = ReadStringOption(args, "--video"),
    ObsProcessId = ReadIntOption(args, "--obs-pid")
};
var response = await ObsBridgePipe.SendAsync(request, TimeSpan.FromSeconds(5));
Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
return response.Ok ? 0 : 1;

static string? ReadStringOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static int? ReadIntOption(string[] arguments, string name) =>
    int.TryParse(ReadStringOption(arguments, name), out var value) ? value : null;

static async Task MonitorParentAsync(int processId, CancellationTokenSource shutdown)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        await process.WaitForExitAsync(shutdown.Token);
        shutdown.Cancel();
    }
    catch (ArgumentException) { shutdown.Cancel(); }
    catch (OperationCanceledException) { }
}
