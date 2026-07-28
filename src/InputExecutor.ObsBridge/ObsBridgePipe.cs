using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace InputExecutor.ObsBridge;

public static class ObsBridgePipe
{
    public const string PipeName = "inputd-obs-bridge-v1";
    public const int MaximumRequestCharacters = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunServerAsync(ObsRecordingController controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 32 * 1024, 32 * 1024);
            try { await pipe.WaitForConnectionAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }

            ObsBridgeResponse response;
            ObsBridgeRequest? deferredStop = null;
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null || line.Length > MaximumRequestCharacters)
                    throw new InvalidDataException("The OBS bridge request is empty or too large.");
                var request = JsonSerializer.Deserialize<ObsBridgeRequest>(line, JsonOptions)
                              ?? throw new InvalidDataException("The OBS bridge request is invalid JSON.");
                if (string.Equals(request.Action, "stop_async", StringComparison.OrdinalIgnoreCase))
                {
                    deferredStop = request;
                    var current = controller.Status();
                    response = current with { Ok = true, Active = false, Message = "Paired OBS input finalization was accepted." };
                }
                else
                {
                    response = await controller.HandleAsync(request, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                response = new ObsBridgeResponse { Ok = false, Active = controller.Status().Active, Message = ex.Message };
            }

            try
            {
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), cancellationToken);
            }
            catch (IOException) { }
            if (response.ShutdownRequested) break;
            if (deferredStop is not null)
            {
                // The OBS frontend callback is already released. Drain and hash outside
                // that UI-critical request, and never cancel a lossless finalization just
                // because OBS exits immediately after RECORDING_STOPPED.
                await controller.StopAsync(deferredStop.VideoPath, "obs_recording_stopped", CancellationToken.None);
            }
        }
    }

    public static async Task<ObsBridgeResponse> SendAsync(ObsBridgeRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutSource.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeoutSource.Token);
        var line = await reader.ReadLineAsync(timeoutSource.Token);
        return JsonSerializer.Deserialize<ObsBridgeResponse>(line ?? string.Empty, JsonOptions)
               ?? throw new InvalidDataException("The OBS bridge returned an invalid response.");
    }

    public static async Task EnsureServerAsync(int? parentProcessId, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(new ObsBridgeRequest { Action = "status" }, TimeSpan.FromMilliseconds(300), cancellationToken);
            return;
        }
        catch { }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the OBS bridge executable.");
        var arguments = parentProcessId is > 0 ? $"serve --parent-pid {parentProcessId.Value}" : "serve";
        _ = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        }) ?? throw new InvalidOperationException("Could not launch the OBS bridge server.");

        var deadline = Environment.TickCount64 + 5_000;
        Exception? lastError = null;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                await SendAsync(new ObsBridgeRequest { Action = "status" }, TimeSpan.FromMilliseconds(300), cancellationToken);
                return;
            }
            catch (Exception ex) { lastError = ex; await Task.Delay(50, cancellationToken); }
        }
        throw new TimeoutException("The OBS bridge server did not become ready.", lastError);
    }
}
