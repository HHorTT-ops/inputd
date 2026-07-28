using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.ObsBridge;

namespace InputExecutor.Tests;

public sealed class ObsBridgeTests
{
    [Fact]
    public void PairedPaths_UseVideoDirectoryAndStem()
    {
        var video = Path.Combine(Path.GetTempPath(), "OBS 配套测试", "capture.sample.mkv");

        var paths = ObsPairedPaths.FromVideo(video);

        Assert.Equal(Path.GetFullPath(video), paths.VideoPath);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(video)!, "capture.sample.input.jsonl"), paths.TrajectoryPath);
        Assert.Equal(paths.TrajectoryPath + ".partial", paths.PartialPath);
        Assert.Equal(paths.TrajectoryPath + ".manifest.json", paths.ManifestPath);
    }

    [Fact]
    public void PairedPaths_RejectRelativeAndMissingPaths()
    {
        Assert.Throws<ArgumentException>(() => ObsPairedPaths.FromVideo("relative-video.mkv"));
        Assert.Throws<ArgumentException>(() => ObsPairedPaths.FromVideo(""));
    }

    [Fact]
    public async Task PairedSink_DrainsToFinalFileAndWritesIntegrityManifest()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var video = Path.Combine(directory, "OBS-测试.mkv");
            await using var sink = new ObsPairedTrajectorySink();
            var paths = await sink.StartAsync(video);
            await sink.WriteAsync(Record(1), CancellationToken.None);

            var completed = await sink.CompleteAsync(video, Manifest(video, 1), CancellationToken.None);

            Assert.False(File.Exists(paths.PartialPath));
            Assert.True(File.Exists(completed.TrajectoryPath));
            Assert.Single(await File.ReadAllLinesAsync(completed.TrajectoryPath));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(completed.ManifestPath));
            Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal("OBS-测试.mkv", json.RootElement.GetProperty("video_file_name").GetString());
            Assert.Equal(64, json.RootElement.GetProperty("trajectory_sha256").GetString()!.Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Controller_StartAndStop_AreIdempotentAndUseFinalObsPath()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var startedVideo = Path.Combine(directory, "paired.mkv");
            var finalVideo = Path.Combine(directory, "paired.mp4");
            await using var sink = new ObsPairedTrajectorySink();
            await using var recorder = new FakeRecorder();
            await using var controller = new ObsRecordingController(sink, recorder, new FakeTargetGuard());

            var started = await controller.StartAsync(startedVideo, 100, CancellationToken.None);
            var duplicate = await controller.StartAsync(startedVideo, 100, CancellationToken.None);
            await sink.WriteAsync(Record(1), CancellationToken.None);
            recorder.EventsRecorded = 1;
            var stopped = await controller.StopAsync(finalVideo, "obs_recording_stopped", CancellationToken.None);
            var duplicateStop = await controller.StopAsync(finalVideo, "obs_recording_stopped", CancellationToken.None);

            Assert.True(started.Ok);
            Assert.True(duplicate.Ok);
            Assert.True(stopped.Ok);
            Assert.True(duplicateStop.Ok);
            Assert.False(stopped.Active);
            Assert.Equal(Path.Combine(directory, "paired.input.jsonl"), stopped.TrajectoryPath);
            Assert.True(File.Exists(stopped.TrajectoryPath));
            Assert.Equal(1, recorder.StartCalls);
            Assert.Equal(1, recorder.StopCalls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ScriptRegistration_PreservesModulesAndIsIdempotent()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var scene = Path.Combine(directory, "scene.json");
            var script = Path.Combine(directory, "inputd-obs.lua");
            var bridge = Path.Combine(directory, "inputd-obs-bridge.exe");
            await File.WriteAllTextAsync(scene, "{\"modules\":{\"other\":{\"kept\":true},\"scripts-tool\":[{\"path\":\"old.lua\",\"settings\":{}}]}}" );
            await File.WriteAllTextAsync(script, "-- test");
            await File.WriteAllTextAsync(bridge, "test");

            var first = await ObsScriptRegistration.RegisterAsync(scene, script, bridge);
            var second = await ObsScriptRegistration.RegisterAsync(scene, script, bridge);

            Assert.True(File.Exists(first.BackupPath));
            Assert.True(File.Exists(second.BackupPath));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(scene));
            Assert.True(json.RootElement.GetProperty("modules").GetProperty("other").GetProperty("kept").GetBoolean());
            var scripts = json.RootElement.GetProperty("modules").GetProperty("scripts-tool");
            Assert.Equal(2, scripts.GetArrayLength());
            Assert.Single(scripts.EnumerateArray(), item =>
                string.Equals(item.GetProperty("path").GetString(), script, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static TrajectoryRecord Record(long sequence) => new()
    {
        RecordedAtUtc = DateTimeOffset.UtcNow,
        SessionId = "obs-test",
        Source = "human_raw_input",
        Sequence = sequence,
        ExecutorReceiveTimestampUs = sequence,
        ExecutorCompleteTimestampUs = sequence,
        ActionRequested = new HumanInputFrame { Device = "mouse", TimestampUs = sequence, Mouse = new MouseFrame { Dx = 1 } },
        Target = new TargetBinding { ProcessId = 100, ProcessName = "game" },
        Backend = "windows-raw-input"
    };

    private static ObsPairingManifest Manifest(string video, long events) => new()
    {
        VideoPath = video,
        VideoFileName = Path.GetFileName(video),
        TrajectoryPath = ObsPairedPaths.FromVideo(video).PartialPath,
        StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        StoppedAtUtc = DateTimeOffset.UtcNow,
        EventsRecorded = events,
        TerminationReason = "test"
    };

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "inputd-obs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRecorder : IHumanInputRecorder
    {
        private string? _session;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public long EventsRecorded { get; set; }
        public RecordingStatus Status { get; private set; } = new();

        public RecordingStatus Start(string sessionId, TargetBinding target, bool includeExecutorEvents)
        {
            StartCalls++;
            _session = sessionId;
            return Status = new RecordingStatus { Active = true, SessionId = sessionId, StartedAtUtc = DateTimeOffset.UtcNow };
        }

        public Task<RecordingStatus> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            Assert.Equal(_session, sessionId);
            StopCalls++;
            Status = new RecordingStatus { Active = false, SessionId = sessionId, EventsRecorded = EventsRecorded };
            return Task.FromResult(Status);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTargetGuard : ITargetGuard
    {
        public TargetBinding CaptureForeground() => new() { WindowHandle = "0x1", ProcessId = 100, ProcessName = "game" };
        public TargetBinding CaptureProcess(int processId) => CaptureForeground();
        public bool TryActivate(TargetBinding target) => true;
        public TargetCheck Validate(TargetBinding target, bool requireForeground) => new(true);
    }
}
