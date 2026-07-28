using InputExecutor.Core.Protocol;
using InputExecutor.Dashboard;

namespace InputExecutor.Tests;

public sealed class SwitchableTrajectorySinkTests
{
    [Fact]
    public async Task StartStopStart_WritesEachRecordingToItsOwnFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"inputd-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.jsonl");
        var secondPath = Path.Combine(directory, "second.jsonl");
        await using var sink = new SwitchableTrajectorySink();
        try
        {
            await sink.StartAsync(firstPath, includeInjected: false);
            await sink.WriteAsync(Record(1, "KeyW"), default);
            await sink.StopAsync();

            await sink.StartAsync(secondPath, includeInjected: false);
            await sink.WriteAsync(Record(1, "KeyE"), default);
            await sink.StopAsync();

            var first = await File.ReadAllTextAsync(firstPath);
            var second = await File.ReadAllTextAsync(secondPath);
            Assert.Contains("KeyW", first);
            Assert.DoesNotContain("KeyE", first);
            Assert.Contains("KeyE", second);
            Assert.DoesNotContain("KeyW", second);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EmptySecondRecording_IsReportedAsZeroRatherThanReusingFirstCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"inputd-empty-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.jsonl");
        var secondPath = Path.Combine(directory, "second.jsonl");
        await using var sink = new SwitchableTrajectorySink();
        try
        {
            await sink.StartAsync(firstPath, includeInjected: false);
            await sink.WriteAsync(Record(1, "KeyW"), default);
            Assert.Equal(1, sink.PersistedCount);
            await sink.StopAsync();

            await sink.StartAsync(secondPath, includeInjected: false);
            Assert.Equal(0, sink.PersistedCount);
            await sink.StopAsync();

            Assert.True(new FileInfo(firstPath).Length > 0);
            Assert.Equal(0, new FileInfo(secondPath).Length);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StopRecordingShortcut_IsRedactedWithoutDroppingMouseOrSequenceData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"inputd-hotkey-redaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "redacted.jsonl");
        await using var sink = new SwitchableTrajectorySink(TimeSpan.FromSeconds(3));
        try
        {
            await sink.StartAsync(path, includeInjected: false);
            await sink.WriteAsync(FrameRecord(1, 1_000_000, ["ControlLeft"], "ControlLeft", true), default);
            Assert.True(new FileInfo(path).Length > 0, "Shortcut filtering must not keep recent training data only in memory.");
            await sink.WriteAsync(FrameRecord(2, 1_010_000, ["ControlLeft", "AltLeft"], "AltLeft", true), default);
            const long hotkeyUs = 1_020_000;
            await sink.BeginShortcutSuppressionAsync(["ControlLeft", "ControlRight", "AltLeft", "AltRight", "F10"], hotkeyUs);
            await sink.WriteAsync(FrameRecord(3, hotkeyUs, ["ControlLeft", "AltLeft", "F10"], "F10", true), default);
            await sink.WriteAsync(FrameRecord(4, 1_030_000, ["ControlLeft", "AltLeft", "F10"], null, null, dx: 17, dy: -9), default);
            await sink.WriteAsync(FrameRecord(5, 1_040_000, ["ControlLeft"], "AltLeft", false), default);
            await sink.WriteAsync(FrameRecord(6, 1_050_000, [], "ControlLeft", false), default);
            await sink.CompleteShortcutSuppressionAsync(hotkeyUs, 1_060_000);
            await sink.StopAsync();

            var lines = await File.ReadAllLinesAsync(path);
            var json = string.Join('\n', lines);
            Assert.Equal(6, lines.Length);
            Assert.Equal(6, sink.PersistedCount);
            Assert.DoesNotContain("ControlLeft", json);
            Assert.DoesNotContain("ControlRight", json);
            Assert.DoesNotContain("AltLeft", json);
            Assert.DoesNotContain("AltRight", json);
            Assert.DoesNotContain("F10", json);
            Assert.Contains("\"dx\":17", json);
            Assert.Contains("\"dy\":-9", json);
            Assert.Equal(Enumerable.Range(1, 6).Select(value => (long)value), lines.Select(ReadSequence));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static TrajectoryRecord Record(long sequence, string key) => new()
    {
        RecordedAtUtc = DateTimeOffset.UtcNow,
        SessionId = "rotation-test",
        Source = "test",
        Sequence = sequence,
        ExecutorReceiveTimestampUs = sequence,
        ExecutorCompleteTimestampUs = sequence,
        ActionRequested = new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = sequence,
            Keyboard = new KeyboardFrame { Held = [key] },
            Mouse = new MouseFrame(),
            ChangedKey = key,
            KeyDown = true
        },
        Backend = "test"
    };

    private static TrajectoryRecord FrameRecord(
        long sequence,
        long timestampUs,
        IReadOnlyList<string> held,
        string? changed,
        bool? down,
        int dx = 0,
        int dy = 0) => new()
    {
        RecordedAtUtc = DateTimeOffset.UtcNow,
        SessionId = "redaction-test",
        Source = "human_raw_input",
        Sequence = sequence,
        ExecutorReceiveTimestampUs = timestampUs,
        ExecutorCompleteTimestampUs = timestampUs,
        ActionRequested = new HumanInputFrame
        {
            Device = dx != 0 || dy != 0 ? "mouse" : "keyboard",
            TimestampUs = timestampUs,
            Keyboard = new KeyboardFrame { Held = held },
            Mouse = new MouseFrame { Dx = dx, Dy = dy },
            ChangedKey = changed,
            KeyDown = down
        },
        Backend = "windows-raw-input"
    };

    private static long ReadSequence(string line)
    {
        using var document = System.Text.Json.JsonDocument.Parse(line);
        return document.RootElement.GetProperty("sequence").GetInt64();
    }
}
