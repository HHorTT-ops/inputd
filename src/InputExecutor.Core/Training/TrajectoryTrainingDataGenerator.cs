using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InputExecutor.Core.Training;

public sealed class TrajectoryTrainingDataGenerator
{
    private const int MaximumFramesPerSecond = 1_000;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly JsonSerializerOptions _manifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<TrainingGenerationResult> GenerateAsync(
        TrainingGenerationOptions options,
        IProgress<TrainingGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FramesPerSecond is < 1 or > MaximumFramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(options.FramesPerSecond), $"Frames per second must be between 1 and {MaximumFramesPerSecond}.");

        var inputPath = Path.GetFullPath(options.InputPath);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var manifestPath = outputPath + ".manifest.json";
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Source trajectory JSONL was not found.", inputPath);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output paths must be different.");
        if (!options.Overwrite && (File.Exists(outputPath) || File.Exists(manifestPath)))
            throw new IOException("Output or manifest already exists. Choose another path or enable overwrite.");

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryId = Guid.NewGuid().ToString("N");
        var temporaryOutput = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{temporaryId}.tmp");
        var temporaryManifest = Path.Combine(outputDirectory, $".{Path.GetFileName(manifestPath)}.{temporaryId}.tmp");
        var totalBytes = new FileInfo(inputPath).Length;

        try
        {
            var statistics = await ResampleAsync(inputPath, temporaryOutput, options, totalBytes, progress, cancellationToken);
            var sourceHash = await Sha256Async(inputPath, cancellationToken);
            var outputHash = await Sha256Async(temporaryOutput, cancellationToken);
            var manifest = new TrainingDataManifest
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                SourcePath = inputPath,
                OutputPath = outputPath,
                EpisodeId = statistics.EpisodeId,
                FramesPerSecond = options.FramesPerSecond,
                NominalFrameDurationUs = 1_000_000d / options.FramesPerSecond,
                SourceEvents = statistics.SourceEvents,
                Frames = statistics.Frames,
                EmptyFrames = statistics.EmptyFrames,
                SourceFirstSequence = statistics.FirstSequence,
                SourceLastSequence = statistics.LastSequence,
                SourceStartTimestampUs = statistics.StartTimestampUs,
                SourceEndTimestampUs = statistics.EndTimestampUs,
                SequenceGaps = statistics.SequenceGaps,
                SourceSha256 = sourceHash,
                OutputSha256 = outputHash
            };
            await WriteManifestAsync(temporaryManifest, manifest, cancellationToken);

            File.Move(temporaryOutput, outputPath, options.Overwrite);
            File.Move(temporaryManifest, manifestPath, options.Overwrite);
            progress?.Report(new TrainingGenerationProgress
            {
                SourceBytesRead = totalBytes,
                SourceBytesTotal = totalBytes,
                SourceEventsRead = statistics.SourceEvents,
                FramesWritten = statistics.Frames
            });
            return new TrainingGenerationResult
            {
                OutputPath = outputPath,
                ManifestPath = manifestPath,
                FramesPerSecond = options.FramesPerSecond,
                SourceEvents = statistics.SourceEvents,
                FramesWritten = statistics.Frames,
                SequenceGaps = statistics.SequenceGaps,
                SourceSha256 = sourceHash,
                OutputSha256 = outputHash
            };
        }
        catch
        {
            TryDelete(temporaryOutput);
            TryDelete(temporaryManifest);
            throw;
        }
    }

    private async Task<GenerationStatistics> ResampleAsync(
        string inputPath,
        string temporaryOutput,
        TrainingGenerationOptions options,
        long totalBytes,
        IProgress<TrainingGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 256 * 1024, leaveOpen: true);
        await using var output = new FileStream(temporaryOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 256 * 1024, leaveOpen: true);

        long lineNumber = 0, sourceEvents = 0, sequenceGaps = 0, frames = 0, emptyFrames = 0;
        long? previousSequence = null, previousTimestamp = null;
        string? episodeId = null;
        long firstSequence = 0, lastSequence = 0, startTimestamp = 0, endTimestamp = 0;

        async Task<SourceInputEvent?> ReadNextAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) return null;
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var sourceEvent = ParseSourceEvent(line, lineNumber);
                if (previousSequence is null)
                {
                    firstSequence = sourceEvent.Sequence;
                    startTimestamp = sourceEvent.TimestampUs;
                    episodeId = sourceEvent.EpisodeId;
                    if (!options.AllowSequenceGaps && sourceEvent.Sequence != 1)
                        throw Invalid(lineNumber, $"The first source sequence is {sourceEvent.Sequence}, not 1. Use allow-gaps only for an intentionally partial recording.");
                }
                else
                {
                    if (sourceEvent.Sequence <= previousSequence.Value)
                        throw Invalid(lineNumber, $"Source sequence {sourceEvent.Sequence} is not greater than {previousSequence.Value}.");
                    var missing = sourceEvent.Sequence - previousSequence.Value - 1;
                    if (missing > 0)
                    {
                        sequenceGaps += missing;
                        if (!options.AllowSequenceGaps)
                            throw Invalid(lineNumber, $"Source sequence gap detected: {previousSequence.Value} -> {sourceEvent.Sequence} ({missing} missing). ");
                    }
                    if (sourceEvent.TimestampUs < previousTimestamp)
                        throw Invalid(lineNumber, $"Timestamp moved backwards: {previousTimestamp} -> {sourceEvent.TimestampUs}.");
                    if (!string.Equals(episodeId, sourceEvent.EpisodeId, StringComparison.Ordinal))
                        throw Invalid(lineNumber, "A source file may contain only one session/episode.");
                }

                sourceEvents++;
                previousSequence = lastSequence = sourceEvent.Sequence;
                previousTimestamp = endTimestamp = sourceEvent.TimestampUs;
                if ((sourceEvents & 0xFFF) == 0)
                    progress?.Report(new TrainingGenerationProgress
                    {
                        SourceBytesRead = input.Position,
                        SourceBytesTotal = totalBytes,
                        SourceEventsRead = sourceEvents,
                        FramesWritten = frames
                    });
                return sourceEvent;
            }
        }

        var next = await ReadNextAsync();
        if (next is null) throw new InvalidDataException("Source trajectory contains no JSON records.");
        var state = new InputState();
        long frameIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameStart = checked(startTimestamp + frameIndex * 1_000_000L / options.FramesPerSecond);
            var frameEnd = checked(startTimestamp + (frameIndex + 1) * 1_000_000L / options.FramesPerSecond);
            var accumulator = new FrameAccumulator(frameStart);
            while (next is not null && next.TimestampUs < frameEnd)
            {
                accumulator.Apply(next, state);
                next = await ReadNextAsync();
            }

            var trainingFrame = accumulator.Create(episodeId!, options.FramesPerSecond, frameIndex, frameEnd, state);
            await writer.WriteLineAsync(JsonSerializer.Serialize(trainingFrame, _json).AsMemory(), cancellationToken);
            frames++;
            if (trainingFrame.SourceEventCount == 0) emptyFrames++;
            if ((frames & 0x3FF) == 0)
            {
                await writer.FlushAsync(cancellationToken);
                await output.FlushAsync(cancellationToken);
                progress?.Report(new TrainingGenerationProgress
                {
                    SourceBytesRead = input.Position,
                    SourceBytesTotal = totalBytes,
                    SourceEventsRead = sourceEvents,
                    FramesWritten = frames
                });
            }
            if (next is null) break;
            frameIndex++;
        }

        await writer.FlushAsync(cancellationToken);
        await output.FlushAsync(cancellationToken);
        return new GenerationStatistics(episodeId!, sourceEvents, frames, emptyFrames, firstSequence, lastSequence,
            startTimestamp, endTimestamp, sequenceGaps);
    }

    private static SourceInputEvent ParseSourceEvent(string line, long lineNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var sequence = RequiredInt64(root, "sequence");
            var episodeId = RequiredString(root, "session_id");
            if (!root.TryGetProperty("action_requested", out var action) || action.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("action_requested must be an object.");
            var timestamp = action.TryGetProperty("timestamp_us", out var timestampElement) && timestampElement.TryGetInt64(out var actionTimestamp)
                ? actionTimestamp
                : RequiredInt64(root, "executor_receive_timestamp_us");
            if (timestamp <= 0) throw new InvalidDataException("timestamp_us must be positive.");

            var keyboard = action.TryGetProperty("keyboard", out var keyboardElement) && keyboardElement.ValueKind == JsonValueKind.Object
                ? StringArray(keyboardElement, "held")
                : Array.Empty<string>();
            MouseSourceFrame mouse;
            if (action.TryGetProperty("mouse", out var mouseElement) && mouseElement.ValueKind == JsonValueKind.Object)
            {
                mouse = new MouseSourceFrame(
                    OptionalInt64(mouseElement, "dx"), OptionalInt64(mouseElement, "dy"),
                    OptionalInt64(mouseElement, "wheel_x"), OptionalInt64(mouseElement, "wheel_y"),
                    StringArray(mouseElement, "held_buttons"));
            }
            else mouse = new MouseSourceFrame(0, 0, 0, 0, Array.Empty<string>());

            return new SourceInputEvent(
                sequence,
                episodeId,
                timestamp,
                action.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.String ? device.GetString() ?? "unknown" : "unknown",
                keyboard,
                mouse,
                action.TryGetProperty("changed_key", out var changedKey) && changedKey.ValueKind == JsonValueKind.String ? changedKey.GetString() : null,
                action.TryGetProperty("key_down", out var keyDown) && keyDown.ValueKind is JsonValueKind.True or JsonValueKind.False ? keyDown.GetBoolean() : null);
        }
        catch (JsonException ex) { throw Invalid(lineNumber, "Invalid JSON: " + ex.Message); }
        catch (InvalidDataException ex) { throw Invalid(lineNumber, ex.Message); }
    }

    private async Task WriteManifestAsync(string path, TrainingDataManifest manifest, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, _manifestJson, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static long RequiredInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"{property} is required and must be an integer.");

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"{property} is required and must be a non-empty string.");

    private static long OptionalInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static string[] StringArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : Array.Empty<string>();

    private static InvalidDataException Invalid(long lineNumber, string message) => new($"Source JSONL line {lineNumber}: {message}");
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class InputState
    {
        internal HashSet<string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FrameAccumulator(long frameStart)
    {
        private readonly HashSet<string> _pressedKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _releasedKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pressedButtons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _releasedButtons = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TrainingInputTransition> _transitions = [];
        private long? _firstSequence;
        private long? _lastSequence;
        private int _eventCount;
        private long _dx, _dy, _wheelX, _wheelY;

        internal void Apply(SourceInputEvent source, InputState current)
        {
            _eventCount++;
            _firstSequence ??= source.Sequence;
            _lastSequence = source.Sequence;
            _dx = checked(_dx + source.Mouse.Dx);
            _dy = checked(_dy + source.Mouse.Dy);
            _wheelX = checked(_wheelX + source.Mouse.WheelX);
            _wheelY = checked(_wheelY + source.Mouse.WheelY);

            var nextKeys = new HashSet<string>(source.HeldKeys, StringComparer.OrdinalIgnoreCase);
            var explicitKeyRecorded = false;
            if (!string.IsNullOrWhiteSpace(source.ChangedKey) && source.KeyDown is not null)
            {
                AddTransition(source, "keyboard", source.ChangedKey, source.KeyDown.Value ? "down" : "up");
                if (source.KeyDown.Value) _pressedKeys.Add(source.ChangedKey); else _releasedKeys.Add(source.ChangedKey);
                explicitKeyRecorded = true;
            }
            foreach (var key in nextKeys.Except(current.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!(explicitKeyRecorded && source.KeyDown == true && string.Equals(source.ChangedKey, key, StringComparison.OrdinalIgnoreCase)))
                    AddTransition(source, "keyboard", key, "down");
                _pressedKeys.Add(key);
            }
            foreach (var key in current.Keys.Except(nextKeys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!(explicitKeyRecorded && source.KeyDown == false && string.Equals(source.ChangedKey, key, StringComparison.OrdinalIgnoreCase)))
                    AddTransition(source, "keyboard", key, "up");
                _releasedKeys.Add(key);
            }
            current.Keys = nextKeys;

            var nextButtons = new HashSet<string>(source.Mouse.HeldButtons, StringComparer.OrdinalIgnoreCase);
            foreach (var button in nextButtons.Except(current.Buttons, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                AddTransition(source, "mouse", button, "down");
                _pressedButtons.Add(button);
            }
            foreach (var button in current.Buttons.Except(nextButtons, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                AddTransition(source, "mouse", button, "up");
                _releasedButtons.Add(button);
            }
            current.Buttons = nextButtons;
        }

        private void AddTransition(SourceInputEvent source, string device, string control, string action) =>
            _transitions.Add(new TrainingInputTransition
            {
                OffsetUs = source.TimestampUs - frameStart,
                SourceSequence = source.Sequence,
                Device = device,
                Control = control,
                Action = action
            });

        internal TrainingFrame Create(string episodeId, int framesPerSecond, long frameIndex, long frameEnd, InputState current) => new()
        {
            EpisodeId = episodeId,
            FramesPerSecond = framesPerSecond,
            FrameIndex = frameIndex,
            WindowStartUs = frameStart,
            WindowEndUs = frameEnd,
            DurationUs = frameEnd - frameStart,
            SourceEventCount = _eventCount,
            SourceFirstSequence = _firstSequence,
            SourceLastSequence = _lastSequence,
            Keyboard = new TrainingKeyboardFrame
            {
                Held = Sorted(current.Keys),
                Pressed = Sorted(_pressedKeys),
                Released = Sorted(_releasedKeys)
            },
            Mouse = new TrainingMouseFrame
            {
                Dx = _dx,
                Dy = _dy,
                WheelX = _wheelX,
                WheelY = _wheelY,
                HeldButtons = Sorted(current.Buttons),
                PressedButtons = Sorted(_pressedButtons),
                ReleasedButtons = Sorted(_releasedButtons)
            },
            Transitions = _transitions.ToArray()
        };

        private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record SourceInputEvent(
        long Sequence,
        string EpisodeId,
        long TimestampUs,
        string Device,
        IReadOnlyList<string> HeldKeys,
        MouseSourceFrame Mouse,
        string? ChangedKey,
        bool? KeyDown);

    private sealed record MouseSourceFrame(long Dx, long Dy, long WheelX, long WheelY, IReadOnlyList<string> HeldButtons);
    private sealed record GenerationStatistics(string EpisodeId, long SourceEvents, long Frames, long EmptyFrames,
        long FirstSequence, long LastSequence, long StartTimestampUs, long EndTimestampUs, long SequenceGaps);
}
