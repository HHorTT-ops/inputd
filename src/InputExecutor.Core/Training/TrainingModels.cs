using System.Text.Json.Serialization;

namespace InputExecutor.Core.Training;

public sealed record TrainingGenerationOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public int FramesPerSecond { get; init; } = 60;
    public bool AllowSequenceGaps { get; init; }
    public bool Overwrite { get; init; }
}

public sealed record TrainingGenerationProgress
{
    public long SourceBytesRead { get; init; }
    public long SourceBytesTotal { get; init; }
    public long SourceEventsRead { get; init; }
    public long FramesWritten { get; init; }
}

public sealed record TrainingGenerationResult
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; init; } = string.Empty;

    [JsonPropertyName("manifest_path")]
    public string ManifestPath { get; init; } = string.Empty;

    [JsonPropertyName("frames_per_second")]
    public int FramesPerSecond { get; init; }

    [JsonPropertyName("source_events")]
    public long SourceEvents { get; init; }

    [JsonPropertyName("frames_written")]
    public long FramesWritten { get; init; }

    [JsonPropertyName("sequence_gaps")]
    public long SequenceGaps { get; init; }

    [JsonPropertyName("source_sha256")]
    public string SourceSha256 { get; init; } = string.Empty;

    [JsonPropertyName("output_sha256")]
    public string OutputSha256 { get; init; } = string.Empty;
}

public sealed record TrainingFrame
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    [JsonPropertyName("episode_id")]
    public string EpisodeId { get; init; } = string.Empty;

    [JsonPropertyName("frames_per_second")]
    public int FramesPerSecond { get; init; }

    [JsonPropertyName("frame_index")]
    public long FrameIndex { get; init; }

    [JsonPropertyName("window_start_us")]
    public long WindowStartUs { get; init; }

    [JsonPropertyName("window_end_us")]
    public long WindowEndUs { get; init; }

    [JsonPropertyName("duration_us")]
    public long DurationUs { get; init; }

    [JsonPropertyName("source_event_count")]
    public int SourceEventCount { get; init; }

    [JsonPropertyName("source_first_sequence")]
    public long? SourceFirstSequence { get; init; }

    [JsonPropertyName("source_last_sequence")]
    public long? SourceLastSequence { get; init; }

    [JsonPropertyName("keyboard")]
    public TrainingKeyboardFrame Keyboard { get; init; } = new();

    [JsonPropertyName("mouse")]
    public TrainingMouseFrame Mouse { get; init; } = new();

    [JsonPropertyName("transitions")]
    public IReadOnlyList<TrainingInputTransition> Transitions { get; init; } = Array.Empty<TrainingInputTransition>();
}

public sealed record TrainingKeyboardFrame
{
    [JsonPropertyName("held")]
    public IReadOnlyList<string> Held { get; init; } = Array.Empty<string>();

    [JsonPropertyName("pressed")]
    public IReadOnlyList<string> Pressed { get; init; } = Array.Empty<string>();

    [JsonPropertyName("released")]
    public IReadOnlyList<string> Released { get; init; } = Array.Empty<string>();
}

public sealed record TrainingMouseFrame
{
    [JsonPropertyName("dx")]
    public long Dx { get; init; }

    [JsonPropertyName("dy")]
    public long Dy { get; init; }

    [JsonPropertyName("wheel_x")]
    public long WheelX { get; init; }

    [JsonPropertyName("wheel_y")]
    public long WheelY { get; init; }

    [JsonPropertyName("held_buttons")]
    public IReadOnlyList<string> HeldButtons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("pressed_buttons")]
    public IReadOnlyList<string> PressedButtons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("released_buttons")]
    public IReadOnlyList<string> ReleasedButtons { get; init; } = Array.Empty<string>();
}

public sealed record TrainingInputTransition
{
    [JsonPropertyName("offset_us")]
    public long OffsetUs { get; init; }

    [JsonPropertyName("source_sequence")]
    public long SourceSequence { get; init; }

    [JsonPropertyName("device")]
    public string Device { get; init; } = string.Empty;

    [JsonPropertyName("control")]
    public string Control { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;
}

public sealed record TrainingDataManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "inputd.training.frames.v1";

    [JsonPropertyName("generated_at_utc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }

    [JsonPropertyName("source_path")]
    public string SourcePath { get; init; } = string.Empty;

    [JsonPropertyName("output_path")]
    public string OutputPath { get; init; } = string.Empty;

    [JsonPropertyName("episode_id")]
    public string EpisodeId { get; init; } = string.Empty;

    [JsonPropertyName("frames_per_second")]
    public int FramesPerSecond { get; init; }

    [JsonPropertyName("nominal_frame_duration_us")]
    public double NominalFrameDurationUs { get; init; }

    [JsonPropertyName("source_events")]
    public long SourceEvents { get; init; }

    [JsonPropertyName("frames")]
    public long Frames { get; init; }

    [JsonPropertyName("empty_frames")]
    public long EmptyFrames { get; init; }

    [JsonPropertyName("source_first_sequence")]
    public long SourceFirstSequence { get; init; }

    [JsonPropertyName("source_last_sequence")]
    public long SourceLastSequence { get; init; }

    [JsonPropertyName("source_start_timestamp_us")]
    public long SourceStartTimestampUs { get; init; }

    [JsonPropertyName("source_end_timestamp_us")]
    public long SourceEndTimestampUs { get; init; }

    [JsonPropertyName("sequence_gaps")]
    public long SequenceGaps { get; init; }

    [JsonPropertyName("source_sha256")]
    public string SourceSha256 { get; init; } = string.Empty;

    [JsonPropertyName("output_sha256")]
    public string OutputSha256 { get; init; } = string.Empty;
}
