using System.Text.Json.Serialization;
using InputExecutor.Core.Protocol;

namespace InputExecutor.ObsBridge;

public sealed record ObsBridgeRequest
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("video_path")]
    public string? VideoPath { get; init; }

    [JsonPropertyName("obs_process_id")]
    public int? ObsProcessId { get; init; }
}

public sealed record ObsBridgeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("video_path")]
    public string? VideoPath { get; init; }

    [JsonPropertyName("trajectory_path")]
    public string? TrajectoryPath { get; init; }

    [JsonPropertyName("partial_path")]
    public string? PartialPath { get; init; }

    [JsonPropertyName("events_recorded")]
    public long EventsRecorded { get; init; }

    [JsonPropertyName("events_dropped")]
    public long EventsDropped { get; init; }

    [JsonPropertyName("events_pending")]
    public long EventsPending { get; init; }

    [JsonPropertyName("recording_status")]
    public RecordingStatus? RecordingStatus { get; init; }

    [JsonIgnore]
    public bool ShutdownRequested { get; init; }
}

public sealed record ObsPairingManifest
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("video_path")]
    public string VideoPath { get; init; } = string.Empty;

    [JsonPropertyName("video_file_name")]
    public string VideoFileName { get; init; } = string.Empty;

    [JsonPropertyName("trajectory_path")]
    public string TrajectoryPath { get; init; } = string.Empty;

    [JsonPropertyName("started_at_utc")]
    public DateTimeOffset StartedAtUtc { get; init; }

    [JsonPropertyName("stopped_at_utc")]
    public DateTimeOffset StoppedAtUtc { get; init; }

    [JsonPropertyName("events_recorded")]
    public long EventsRecorded { get; init; }

    [JsonPropertyName("events_dropped")]
    public long EventsDropped { get; init; }

    [JsonPropertyName("events_pending")]
    public long EventsPending { get; init; }

    [JsonPropertyName("trajectory_sha256")]
    public string? TrajectorySha256 { get; init; }

    [JsonPropertyName("termination_reason")]
    public string TerminationReason { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
