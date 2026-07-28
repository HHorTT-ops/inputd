using System.Text.Json;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Training;

namespace InputExecutor.Tests;

public sealed class TrajectoryTrainingDataGeneratorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Resample_PreservesMouseSumsAndShortKeyTapAcrossBoundary()
    {
        await using var fixture = new TrainingFixture();
        await fixture.WriteAsync(
            Event(1, 1_000_000, mouseDx: 3, mouseDy: -2),
            Event(2, 1_005_000, heldKeys: ["KeyW"], changedKey: "KeyW", keyDown: true),
            Event(3, 1_010_000, changedKey: "KeyW", keyDown: false),
            Event(4, 1_016_665, mouseDx: 4),
            Event(5, 1_016_666, mouseDx: 5));

        var result = await fixture.GenerateAsync(60);
        var frames = await ReadFramesAsync(result.OutputPath);

        Assert.Equal(2, frames.Count);
        Assert.Equal(4, frames[0].SourceEventCount);
        Assert.Equal(7, frames[0].Mouse.Dx);
        Assert.Equal(-2, frames[0].Mouse.Dy);
        Assert.Contains("KeyW", frames[0].Keyboard.Pressed);
        Assert.Contains("KeyW", frames[0].Keyboard.Released);
        Assert.Empty(frames[0].Keyboard.Held);
        Assert.Equal([1L, 4L], [frames[0].SourceFirstSequence!.Value, frames[0].SourceLastSequence!.Value]);
        Assert.Collection(frames[0].Transitions.Where(item => item.Control == "KeyW"),
            item => Assert.Equal("down", item.Action),
            item => Assert.Equal("up", item.Action));
        Assert.Equal(1_016_666, frames[1].WindowStartUs);
        Assert.Equal(5, frames[1].Mouse.Dx);
        Assert.Equal(5, frames[1].SourceFirstSequence);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(37)]
    public async Task Resample_PresetAndCustomRatesProduceExactOneSecondFrameCount(int framesPerSecond)
    {
        await using var fixture = new TrainingFixture();
        await fixture.WriteAsync(Event(1, 5_000_000), Event(2, 5_999_999));
        var result = await fixture.GenerateAsync(framesPerSecond);
        Assert.Equal(framesPerSecond, result.FramesWritten);
        var frames = await ReadFramesAsync(result.OutputPath);
        Assert.Equal(framesPerSecond, frames.Count);
        Assert.Equal(5_000_000, frames[0].WindowStartUs);
        Assert.Equal(6_000_000, frames[^1].WindowEndUs);
    }

    [Fact]
    public async Task Resample_EmptyFramesCarryHeldStateWithoutInventingTransitions()
    {
        await using var fixture = new TrainingFixture();
        await fixture.WriteAsync(
            Event(1, 1_000_000, heldKeys: ["KeyW"], changedKey: "KeyW", keyDown: true),
            Event(2, 1_200_000, changedKey: "KeyW", keyDown: false));
        var result = await fixture.GenerateAsync(20);
        var frames = await ReadFramesAsync(result.OutputPath);
        Assert.Equal(5, frames.Count);
        Assert.All(frames.Skip(1).Take(3), frame =>
        {
            Assert.Equal(0, frame.SourceEventCount);
            Assert.Contains("KeyW", frame.Keyboard.Held);
            Assert.Empty(frame.Transitions);
        });
        Assert.Empty(frames[^1].Keyboard.Held);
        Assert.Contains("KeyW", frames[^1].Keyboard.Released);
    }

    [Fact]
    public async Task Resample_MouseClickWithinOneFramePreservesBothEdgesAndFinalState()
    {
        await using var fixture = new TrainingFixture();
        await fixture.WriteAsync(
            Event(1, 2_000_000, heldButtons: ["left"], mouseDx: 9, wheelY: 120),
            Event(2, 2_010_000, heldButtons: [], mouseDx: -4, wheelY: -120));
        var result = await fixture.GenerateAsync(20);
        var frame = Assert.Single(await ReadFramesAsync(result.OutputPath));
        Assert.Equal(5, frame.Mouse.Dx);
        Assert.Equal(0, frame.Mouse.WheelY);
        Assert.Contains("left", frame.Mouse.PressedButtons);
        Assert.Contains("left", frame.Mouse.ReleasedButtons);
        Assert.Empty(frame.Mouse.HeldButtons);
        Assert.Collection(frame.Transitions.Where(item => item.Device == "mouse"),
            item => Assert.Equal("down", item.Action),
            item => Assert.Equal("up", item.Action));
    }

    [Fact]
    public async Task Resample_SequenceGapFailsByDefaultAndIsExplicitInAllowedManifest()
    {
        await using var fixture = new TrainingFixture();
        await fixture.WriteAsync(Event(1, 3_000_000), Event(3, 3_010_000));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync(60));
        Assert.Contains("gap", error.Message, StringComparison.OrdinalIgnoreCase);

        var result = await fixture.GenerateAsync(60, allowGaps: true);
        Assert.Equal(1, result.SequenceGaps);
        var manifest = JsonSerializer.Deserialize<TrainingDataManifest>(await File.ReadAllTextAsync(result.ManifestPath), Json)!;
        Assert.Equal(1, manifest.SequenceGaps);
        Assert.NotEmpty(manifest.SourceSha256);
        Assert.NotEmpty(manifest.OutputSha256);
    }

    private static TrajectoryRecord Event(
        long sequence,
        long timestampUs,
        IReadOnlyList<string>? heldKeys = null,
        string? changedKey = null,
        bool? keyDown = null,
        long mouseDx = 0,
        long mouseDy = 0,
        long wheelY = 0,
        IReadOnlyList<string>? heldButtons = null)
    {
        return new TrajectoryRecord
        {
            RecordedAtUtc = DateTimeOffset.UnixEpoch,
            SessionId = "episode-test",
            Source = "human_raw_input",
            Sequence = sequence,
            ExecutorReceiveTimestampUs = timestampUs,
            ExecutorCompleteTimestampUs = timestampUs,
            ActionRequested = new HumanInputFrame
            {
                Device = changedKey is null ? "mouse" : "keyboard",
                TimestampUs = timestampUs,
                Keyboard = new KeyboardFrame { Held = heldKeys ?? Array.Empty<string>() },
                Mouse = new MouseFrame
                {
                    Dx = checked((int)mouseDx),
                    Dy = checked((int)mouseDy),
                    WheelY = checked((int)wheelY),
                    HeldButtons = heldButtons ?? Array.Empty<string>()
                },
                ChangedKey = changedKey,
                KeyDown = keyDown
            }
        };
    }

    private static async Task<List<TrainingFrame>> ReadFramesAsync(string path)
    {
        var frames = new List<TrainingFrame>();
        foreach (var line in await File.ReadAllLinesAsync(path))
            frames.Add(JsonSerializer.Deserialize<TrainingFrame>(line, Json)!);
        return frames;
    }

    private sealed class TrainingFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"inputd-training-{Guid.NewGuid():N}");
        internal string InputPath => Path.Combine(_directory, "source.jsonl");

        internal TrainingFixture() => Directory.CreateDirectory(_directory);

        internal async Task WriteAsync(params TrajectoryRecord[] records)
        {
            await using var writer = new StreamWriter(InputPath, false, new System.Text.UTF8Encoding(false));
            foreach (var record in records) await writer.WriteLineAsync(JsonSerializer.Serialize(record, Json));
        }

        internal Task<TrainingGenerationResult> GenerateAsync(int framesPerSecond, bool allowGaps = false)
        {
            var output = Path.Combine(_directory, $"training-{framesPerSecond}-{allowGaps}.jsonl");
            return new TrajectoryTrainingDataGenerator().GenerateAsync(new TrainingGenerationOptions
            {
                InputPath = InputPath,
                OutputPath = output,
                FramesPerSecond = framesPerSecond,
                AllowSequenceGaps = allowGaps,
                Overwrite = true
            });
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
