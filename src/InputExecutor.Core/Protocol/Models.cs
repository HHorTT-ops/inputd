using System.Text.Json.Serialization;

namespace InputExecutor.Core.Protocol;

public sealed record TargetBinding
{
    [JsonPropertyName("window_handle")]
    public string WindowHandle { get; init; } = "0x0";

    [JsonPropertyName("process_id")]
    public int ProcessId { get; init; }

    [JsonPropertyName("process_name")]
    public string ProcessName { get; init; } = string.Empty;

    [JsonPropertyName("process_path")]
    public string? ProcessPath { get; init; }

    [JsonPropertyName("process_start_time_utc_ticks")]
    public long? ProcessStartTimeUtcTicks { get; init; }

    [JsonPropertyName("window_title")]
    public string? WindowTitle { get; init; }
}

public sealed record CreateSessionRequest
{
    [JsonPropertyName("target")]
    public TargetBinding? Target { get; init; }

    [JsonPropertyName("bind_to_foreground")]
    public bool BindToForeground { get; init; } = true;

    [JsonPropertyName("target_process_id")]
    public int? TargetProcessId { get; init; }

    [JsonPropertyName("lease_timeout_ms")]
    public int LeaseTimeoutMs { get; init; } = 250;

    [JsonPropertyName("allowed_processes")]
    public IReadOnlyList<string> AllowedProcesses { get; init; } = Array.Empty<string>();

    [JsonPropertyName("client_process_id")]
    public int? ClientProcessId { get; init; }
}

public sealed record ArmRequest
{
    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; init; } = 1_800;
}

public sealed record ActionCommand
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("dx")]
    public int? Dx { get; init; }

    [JsonPropertyName("dy")]
    public int? Dy { get; init; }

    [JsonPropertyName("x")]
    public int? X { get; init; }

    [JsonPropertyName("y")]
    public int? Y { get; init; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; init; }

    [JsonPropertyName("coalesce")]
    public bool? Coalesce { get; init; }

    [JsonPropertyName("button")]
    public string? Button { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("delta_x")]
    public int? DeltaX { get; init; }

    [JsonPropertyName("delta_y")]
    public int? DeltaY { get; init; }
}

public sealed record CommandRequest
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("deadline_us")]
    public long? DeadlineUs { get; init; }

    [JsonPropertyName("exclusive")]
    public bool Exclusive { get; init; } = true;

    [JsonPropertyName("actions")]
    public IReadOnlyList<ActionCommand> Actions { get; init; } = Array.Empty<ActionCommand>();
}

public sealed record KeyboardFrame
{
    [JsonPropertyName("held")]
    public IReadOnlyList<string> Held { get; init; } = Array.Empty<string>();
}

public sealed record MouseFrame
{
    [JsonPropertyName("dx")]
    public int Dx { get; init; }

    [JsonPropertyName("dy")]
    public int Dy { get; init; }

    [JsonPropertyName("held_buttons")]
    public IReadOnlyList<string> HeldButtons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("wheel_x")]
    public int WheelX { get; init; }

    [JsonPropertyName("wheel_y")]
    public int WheelY { get; init; }
}

public sealed record RealtimeFrame
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("timestamp_us")]
    public long TimestampUs { get; init; }

    [JsonPropertyName("deadline_us")]
    public long? DeadlineUs { get; init; }

    [JsonPropertyName("keyboard")]
    public KeyboardFrame Keyboard { get; init; } = new();

    [JsonPropertyName("mouse")]
    public MouseFrame Mouse { get; init; } = new();
}

public sealed record EmittedInput
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("emitted_at_us")]
    public long EmittedAtUs { get; init; }
}

public sealed record ExecutionResult
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "executed";

    [JsonPropertyName("events")]
    public IReadOnlyList<EmittedInput> Events { get; init; } = Array.Empty<EmittedInput>();

    [JsonPropertyName("monotonic_us")]
    public long MonotonicUs { get; init; }
}

public sealed record SessionStatus
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public TargetBinding Target { get; init; } = new();

    [JsonPropertyName("armed")]
    public bool Armed { get; init; }

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("pause_reason")]
    public string? PauseReason { get; init; }

    [JsonPropertyName("armed_until_us")]
    public long? ArmedUntilUs { get; init; }

    [JsonPropertyName("lease_timeout_ms")]
    public int LeaseTimeoutMs { get; init; }

    [JsonPropertyName("allowed_processes")]
    public IReadOnlyList<string> AllowedProcesses { get; init; } = Array.Empty<string>();

    [JsonPropertyName("client_process_id")]
    public int? ClientProcessId { get; init; }

    [JsonPropertyName("realtime_connected")]
    public bool RealtimeConnected { get; init; }

    [JsonPropertyName("last_command_sequence")]
    public long LastCommandSequence { get; init; }

    [JsonPropertyName("last_realtime_sequence")]
    public long LastRealtimeSequence { get; init; }

    [JsonPropertyName("held_keys")]
    public IReadOnlyList<string> HeldKeys { get; init; } = Array.Empty<string>();

    [JsonPropertyName("held_mouse_buttons")]
    public IReadOnlyList<string> HeldMouseButtons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("monotonic_us")]
    public long MonotonicUs { get; init; }
}

public sealed record CreateSessionResponse
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("capability_token")]
    public string CapabilityToken { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public SessionStatus Status { get; init; } = new();
}

public sealed record ApiError
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public object? Details { get; init; }
}

public sealed record TrajectoryRecord
{
    [JsonPropertyName("recorded_at_utc")]
    public DateTimeOffset RecordedAtUtc { get; init; }

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("executor_receive_timestamp_us")]
    public long ExecutorReceiveTimestampUs { get; init; }

    [JsonPropertyName("executor_complete_timestamp_us")]
    public long ExecutorCompleteTimestampUs { get; init; }

    [JsonPropertyName("action_requested")]
    public object? ActionRequested { get; init; }

    [JsonPropertyName("action_emitted")]
    public IReadOnlyList<EmittedInput> ActionEmitted { get; init; } = Array.Empty<EmittedInput>();

    [JsonPropertyName("target")]
    public TargetBinding Target { get; init; } = new();

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("termination_reason")]
    public string? TerminationReason { get; init; }
}

public sealed record HumanInputFrame
{
    [JsonPropertyName("device")]
    public string Device { get; init; } = string.Empty;

    [JsonPropertyName("timestamp_us")]
    public long TimestampUs { get; init; }

    [JsonPropertyName("keyboard")]
    public KeyboardFrame Keyboard { get; init; } = new();

    [JsonPropertyName("mouse")]
    public MouseFrame Mouse { get; init; } = new();

    [JsonPropertyName("changed_key")]
    public string? ChangedKey { get; init; }

    [JsonPropertyName("key_down")]
    public bool? KeyDown { get; init; }

    [JsonPropertyName("injected_tagged")]
    public bool InjectedTagged { get; init; }

    /// <summary>
    /// Raw Input <c>RAWINPUTHEADER.hDevice</c> as hex (e.g. <c>0x1A2B</c>), or null when unavailable.
    /// A non-null non-zero handle means Windows associated the packet with a registered device;
    /// it does not prove a human hand operated a physical mouse (virtual HID can also appear).
    /// </summary>
    [JsonPropertyName("raw_device_handle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawDeviceHandle { get; init; }
}

public sealed record RecordingStartRequest
{
    [JsonPropertyName("include_executor_events")]
    public bool IncludeExecutorEvents { get; init; }
}

public sealed record RecordingStatus
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("started_at_utc")]
    public DateTimeOffset? StartedAtUtc { get; init; }

    [JsonPropertyName("events_recorded")]
    public long EventsRecorded { get; init; }

    [JsonPropertyName("events_dropped")]
    public long EventsDropped { get; init; }

    [JsonPropertyName("events_pending")]
    public long EventsPending { get; init; }

    [JsonPropertyName("faulted")]
    public bool Faulted { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("include_executor_events")]
    public bool IncludeExecutorEvents { get; init; }

    [JsonPropertyName("diagnostic_hooks_active")]
    public bool DiagnosticHooksActive { get; init; }

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "windows-raw-input";
}

public sealed record ScreenshotFrame
{
    [JsonPropertyName("frame_id")]
    public string FrameId { get; init; } = string.Empty;

    [JsonPropertyName("captured_at_utc")]
    public DateTimeOffset CapturedAtUtc { get; init; }

    [JsonPropertyName("origin_x")]
    public int OriginX { get; init; }

    [JsonPropertyName("origin_y")]
    public int OriginY { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonIgnore]
    public byte[] PngBytes { get; init; } = Array.Empty<byte>();
}
