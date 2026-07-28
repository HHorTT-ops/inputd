using System.Diagnostics;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Windows;

namespace InputExecutor.ObsBridge;

public sealed class ObsRecordingController : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ObsPairedTrajectorySink _sink;
    private readonly IHumanInputRecorder _recorder;
    private readonly ITargetGuard _targetGuard;
    private ActiveRecording? _active;
    private bool _disposed;

    public ObsRecordingController(ObsPairedTrajectorySink sink, IHumanInputRecorder recorder, ITargetGuard targetGuard)
    {
        _sink = sink;
        _recorder = recorder;
        _targetGuard = targetGuard;
    }

    public static ObsRecordingController CreateWindows()
    {
        var sink = new ObsPairedTrajectorySink();
        return new ObsRecordingController(sink, new WindowsRawInputRecorder(sink, new SystemMonotonicClock()), new WindowsTargetGuard());
    }

    public async Task<ObsBridgeResponse> HandleAsync(ObsBridgeRequest request, CancellationToken cancellationToken)
    {
        if (request.Version != 1) return Error($"Unsupported protocol version {request.Version}.");
        return request.Action.Trim().ToLowerInvariant() switch
        {
            "start" => await StartAsync(request.VideoPath, request.ObsProcessId, cancellationToken),
            "stop" => await StopAsync(request.VideoPath, "obs_recording_stopped", cancellationToken),
            "status" => Status(),
            "shutdown" => (await StopForShutdownAsync("obs_plugin_shutdown", cancellationToken)) with { ShutdownRequested = true },
            _ => Error($"Unsupported action '{request.Action}'.")
        };
    }

    public async Task<ObsBridgeResponse> StartAsync(string? videoPath, int? obsProcessId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return Error("OBS did not provide the recording output path.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requested = ObsPairedPaths.FromVideo(videoPath);
            if (_active is not null)
            {
                if (string.Equals(_active.Paths.VideoPath, requested.VideoPath, StringComparison.OrdinalIgnoreCase))
                    return Response(true, true, "The paired input recording is already active.", _recorder.Status, _active.Paths);
                return Error($"An OBS input recording is already active for '{_active.Paths.VideoPath}'.", true);
            }

            var paths = await _sink.StartAsync(requested.VideoPath, cancellationToken);
            try
            {
                var target = CaptureTarget(obsProcessId);
                var sessionId = $"obs-{obsProcessId ?? 0}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                var status = _recorder.Start(sessionId, target, includeExecutorEvents: false);
                _active = new ActiveRecording(paths, status.StartedAtUtc ?? DateTimeOffset.UtcNow, sessionId);
                return Response(true, true, "Paired OBS input recording started.", status, paths);
            }
            catch (Exception ex)
            {
                await _sink.PreserveIncompleteAsync(NewManifest(paths, DateTimeOffset.UtcNow, _recorder.Status, "start_failed", ex.Message), cancellationToken);
                return Error("Could not start the input recorder: " + ex.Message);
            }
        }
        catch (Exception ex) { return Error("Could not prepare the paired input file: " + ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<ObsBridgeResponse> StopAsync(string? finalVideoPath, string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null) return Response(true, false, "No paired OBS input recording is active.", _recorder.Status, _sink.Paths);
            var active = _active;
            var status = await _recorder.StopAsync(active.SessionId, cancellationToken);
            var complete = !status.Faulted && status.EventsDropped == 0 && status.EventsPending == 0;
            var videoPath = string.IsNullOrWhiteSpace(finalVideoPath) ? active.Paths.VideoPath : finalVideoPath;
            var manifest = NewManifest(active.Paths, active.StartedAtUtc, status, reason,
                complete ? null : status.LastError ?? "The Raw Input queue did not complete without loss.");
            _active = null;

            if (!complete)
            {
                await _sink.PreserveIncompleteAsync(manifest, cancellationToken);
                return Response(false, false, "Input recording was preserved as incomplete because its integrity check failed.", status, _sink.Paths);
            }

            try
            {
                var paths = await _sink.CompleteAsync(videoPath!, manifest, cancellationToken);
                return Response(true, false, "Paired OBS input recording completed.", status, paths);
            }
            catch (Exception ex)
            {
                await _sink.PreserveIncompleteAsync(manifest with { Error = ex.Message }, cancellationToken);
                return Response(false, false, "Input data was flushed but could not be finalized: " + ex.Message, status, _sink.Paths);
            }
        }
        catch (Exception ex) { return Error("Could not stop the paired input recording: " + ex.Message, _active is not null); }
        finally { _gate.Release(); }
    }

    public ObsBridgeResponse Status()
    {
        var active = Volatile.Read(ref _active);
        return Response(true, active is not null, active is null ? "The OBS input recorder is idle." : "The OBS input recorder is active.",
            _recorder.Status, active?.Paths ?? _sink.Paths);
    }

    public async Task<ObsBridgeResponse> StopForShutdownAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null) return Status();
            var active = _active;
            var status = await _recorder.StopAsync(active.SessionId, cancellationToken);
            _active = null;
            await _sink.PreserveIncompleteAsync(NewManifest(active.Paths, active.StartedAtUtc, status, reason,
                "OBS or the bridge exited before a normal RECORDING_STOPPED event."), cancellationToken);
            return Response(true, false, "Active input data was flushed and preserved as an incomplete recording.", status, _sink.Paths);
        }
        finally { _gate.Release(); }
    }

    private TargetBinding CaptureTarget(int? obsProcessId)
    {
        try { return _targetGuard.CaptureForeground(); }
        catch when (obsProcessId is > 0)
        {
            using var process = Process.GetProcessById(obsProcessId.Value);
            long? startTicks = null;
            string? path = null;
            try { startTicks = process.StartTime.ToUniversalTime().Ticks; } catch { }
            try { path = process.MainModule?.FileName; } catch { }
            return new TargetBinding
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                ProcessPath = path,
                ProcessStartTimeUtcTicks = startTicks,
                WindowTitle = process.MainWindowTitle
            };
        }
    }

    private static ObsPairingManifest NewManifest(ObsPairedPaths paths, DateTimeOffset startedAt, RecordingStatus status, string reason, string? error) => new()
    {
        Complete = false,
        VideoPath = paths.VideoPath,
        VideoFileName = Path.GetFileName(paths.VideoPath),
        TrajectoryPath = paths.PartialPath,
        StartedAtUtc = startedAt,
        StoppedAtUtc = DateTimeOffset.UtcNow,
        EventsRecorded = status.EventsRecorded,
        EventsDropped = status.EventsDropped,
        EventsPending = status.EventsPending,
        TerminationReason = reason,
        Error = error
    };

    private static ObsBridgeResponse Response(bool ok, bool active, string message, RecordingStatus status, ObsPairedPaths? paths) => new()
    {
        Ok = ok,
        Active = active,
        Message = message,
        VideoPath = paths?.VideoPath,
        TrajectoryPath = paths?.TrajectoryPath,
        PartialPath = paths?.PartialPath,
        EventsRecorded = status.EventsRecorded,
        EventsDropped = status.EventsDropped,
        EventsPending = status.EventsPending,
        RecordingStatus = status
    };

    private static ObsBridgeResponse Error(string message, bool active = false) => new() { Ok = false, Active = active, Message = message };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await StopForShutdownAsync("bridge_disposed", CancellationToken.None); } catch { }
        _disposed = true;
        await _recorder.DisposeAsync();
        await _sink.DisposeAsync();
        _gate.Dispose();
    }

    private sealed record ActiveRecording(ObsPairedPaths Paths, DateTimeOffset StartedAtUtc, string SessionId);
}
