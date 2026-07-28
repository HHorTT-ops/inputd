using System.Text.Json;
using System.Text.Json.Serialization;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Scheduler;

public sealed record TrajectoryReplaySummary(
    string Path,
    long EventCount,
    long FirstSequence,
    long LastSequence,
    long DurationMicroseconds,
    string SessionId,
    string Source,
    long MouseMoveEventCount,
    long TotalMouseDx,
    long TotalMouseDy);

public sealed record TrajectoryReplayProgress(
    long EventsPlayed,
    long EventCount,
    long RecordedElapsedMicroseconds,
    long RecordedDurationMicroseconds);

/// <summary>
/// Validates and replays lossless Raw Input JSONL trajectories. All actual input is
/// submitted through InputScheduler, so SendInput remains confined to its one worker thread.
/// </summary>
public sealed class TrajectoryReplayPlayer
{
    // Raw Input can report thousands of +/-1 count packets per second. Sending each
    // tiny count as an independent SendInput call can round it to zero under Windows
    // pointer-speed scaling. Merge only adjacent mouse packets with identical held
    // state and at most 2 ms of recorded time, preserving their exact signed sum.
    private const long MaximumMouseBatchWindowUs = 2_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> MouseButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        "left", "right", "middle", "x1", "x2"
    };

    private readonly InputScheduler _scheduler;
    private readonly ITargetGuard _targetGuard;
    private readonly IMonotonicClock _clock;

    public TrajectoryReplayPlayer(InputScheduler scheduler, ITargetGuard targetGuard, IMonotonicClock? clock = null)
    {
        _scheduler = scheduler;
        _targetGuard = targetGuard;
        _clock = clock ?? new SystemMonotonicClock();
    }

    public async Task<TrajectoryReplaySummary> ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        await using var stream = OpenReadLocked(fullPath);
        return await ValidateStreamAsync(stream, fullPath, cancellationToken);
    }

    public async Task<TrajectoryReplaySummary> ReplayAsync(
        string path,
        TargetBinding target,
        IProgress<TrajectoryReplayProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        await using var stream = OpenReadLocked(fullPath);
        var summary = await ValidateStreamAsync(stream, fullPath, cancellationToken);
        ValidateTarget(target);

        stream.Position = 0;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 256 * 1024, leaveOpen: true);
        var firstTimestamp = -1L;
        var replayStartedUs = _clock.NowMicroseconds;
        var played = 0L;
        var read = 0L;
        MouseReplayBatch? pendingMouse = null;

        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = Parse(line, ++read);
                firstTimestamp = firstTimestamp < 0 ? record.TimestampUs : firstTimestamp;
                if (IsMouseRecord(record.Action))
                {
                    if (pendingMouse is not null && pendingMouse.CanAdd(record, MaximumMouseBatchWindowUs))
                    {
                        pendingMouse.Add(record);
                    }
                    else
                    {
                        if (pendingMouse is not null)
                            played += await EmitMouseBatchAsync(pendingMouse, firstTimestamp, replayStartedUs, target, summary, played, progress, cancellationToken);
                        pendingMouse = new MouseReplayBatch(record);
                    }
                    continue;
                }

                if (pendingMouse is not null)
                {
                    played += await EmitMouseBatchAsync(pendingMouse, firstTimestamp, replayStartedUs, target, summary, played, progress, cancellationToken);
                    pendingMouse = null;
                }
                await EmitRecordAsync(record, firstTimestamp, replayStartedUs, target, cancellationToken);
                played++;
                ReportProgress(progress, summary, played, record.TimestampUs - firstTimestamp);
            }

            if (pendingMouse is not null)
                played += await EmitMouseBatchAsync(pendingMouse, firstTimestamp, replayStartedUs, target, summary, played, progress, cancellationToken);
            if (played != summary.EventCount)
                throw new InvalidDataException($"回放事件计数不一致：校验 {summary.EventCount} 条，实际处理 {played} 条。");
            return summary;
        }
        finally
        {
            try { await _scheduler.ReleaseAllAndClearAsync("trajectory_replay_finished", CancellationToken.None); }
            catch { /* Best effort during process shutdown. */ }
        }
    }

    private async Task<TrajectoryReplaySummary> ValidateStreamAsync(FileStream stream, string fullPath, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 256 * 1024, leaveOpen: true);
        ReplayRecord? first = null;
        ReplayRecord? previous = null;
        var count = 0L;
        var mouseMoveEvents = 0L;
        var totalMouseDx = 0L;
        var totalMouseDy = 0L;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var current = Parse(line, count + 1);
            if (previous is not null)
            {
                if (current.Sequence != previous.Sequence + 1)
                    throw new InvalidDataException($"轨迹序号在 {previous.Sequence} 与 {current.Sequence} 之间不连续，拒绝回放可能缺失的操作。");
                if (current.TimestampUs < previous.TimestampUs)
                    throw new InvalidDataException($"轨迹时间戳在序号 {current.Sequence} 发生倒退。");
            }

            foreach (var key in current.Action.Keyboard.Held)
                if (!_scheduler.SupportsKey(key)) throw new InvalidDataException($"轨迹包含不支持的物理按键代码：{key}。");
            foreach (var button in current.Action.Mouse.HeldButtons)
                if (!MouseButtons.Contains(button)) throw new InvalidDataException($"轨迹包含不支持的鼠标按钮：{button}。");
            if (current.Action.Mouse.Dx != 0 || current.Action.Mouse.Dy != 0)
            {
                mouseMoveEvents++;
                totalMouseDx = checked(totalMouseDx + current.Action.Mouse.Dx);
                totalMouseDy = checked(totalMouseDy + current.Action.Mouse.Dy);
            }

            first ??= current;
            previous = current;
            count++;
        }

        if (first is null || previous is null) throw new InvalidDataException("轨迹文件为空，没有可播放的操作。");
        stream.Position = 0;
        return new TrajectoryReplaySummary(
            fullPath,
            count,
            first.Sequence,
            previous.Sequence,
            previous.TimestampUs - first.TimestampUs,
            first.SessionId,
            first.Source,
            mouseMoveEvents,
            totalMouseDx,
            totalMouseDy);
    }

    private async Task EmitRecordAsync(
        ReplayRecord record,
        long firstTimestamp,
        long replayStartedUs,
        TargetBinding target,
        CancellationToken cancellationToken)
    {
        var recordedElapsed = record.TimestampUs - firstTimestamp;
        await WaitUntilAsync(replayStartedUs + recordedElapsed, target, cancellationToken);
        var repeatedKeyDowns = record.Action.ChangedKey is { Length: > 0 } changedKey && record.Action.KeyDown == true
            ? new[] { changedKey }
            : Array.Empty<string>();
        await _scheduler.EnqueueReplayAsync(new RealtimeFrame
        {
            Sequence = record.Sequence,
            TimestampUs = _clock.NowMicroseconds,
            Keyboard = record.Action.Keyboard,
            Mouse = record.Action.Mouse
        }, repeatedKeyDowns, () => ValidateTarget(target), cancellationToken);
    }

    private async Task<long> EmitMouseBatchAsync(
        MouseReplayBatch batch,
        long firstTimestamp,
        long replayStartedUs,
        TargetBinding target,
        TrajectoryReplaySummary summary,
        long alreadyPlayed,
        IProgress<TrajectoryReplayProgress>? progress,
        CancellationToken cancellationToken)
    {
        var record = batch.ToReplayRecord();
        await EmitRecordAsync(record, firstTimestamp, replayStartedUs, target, cancellationToken);
        ReportProgress(progress, summary, alreadyPlayed + batch.SourceEventCount, record.TimestampUs - firstTimestamp);
        return batch.SourceEventCount;
    }

    private static void ReportProgress(
        IProgress<TrajectoryReplayProgress>? progress,
        TrajectoryReplaySummary summary,
        long played,
        long elapsedUs) =>
        progress?.Report(new TrajectoryReplayProgress(played, summary.EventCount, elapsedUs, summary.DurationMicroseconds));

    private static bool IsMouseRecord(HumanInputFrame action) =>
        action.Device.Contains("mouse", StringComparison.OrdinalIgnoreCase) && action.ChangedKey is null;

    private async Task WaitUntilAsync(long dueUs, TargetBinding target, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTarget(target);
            var remainingUs = dueUs - _clock.NowMicroseconds;
            if (remainingUs <= 0) return;
            var delayMs = (int)Math.Clamp((remainingUs + 999) / 1_000, 1, 50);
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    private void ValidateTarget(TargetBinding target)
    {
        var check = _targetGuard.Validate(target, requireForeground: true);
        if (!check.IsValid)
            throw new InputExecutorException(check.ErrorCode ?? "TARGET_INVALID", check.Message ?? "回放目标窗口校验失败。", 409);
    }

    private static ReplayRecord Parse(string line, long lineNumber)
    {
        ReplayEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<ReplayEnvelope>(line, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"轨迹第 {lineNumber} 条 JSON 无效：{ex.Message}", ex); }

        if (envelope is null || envelope.Sequence <= 0)
            throw new InvalidDataException($"轨迹第 {lineNumber} 条缺少有效 sequence。");
        if (envelope.ActionRequested is null)
            throw new InvalidDataException($"轨迹第 {lineNumber} 条缺少 action_requested。");
        if (!string.Equals(envelope.Source, "human_raw_input", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"轨迹第 {lineNumber} 条来源为 '{envelope.Source}'，只允许回放 human_raw_input 操作记录。");
        var timestamp = envelope.ActionRequested.TimestampUs != 0
            ? envelope.ActionRequested.TimestampUs
            : envelope.ExecutorReceiveTimestampUs;
        if (timestamp < 0) throw new InvalidDataException($"轨迹第 {lineNumber} 条时间戳无效。");
        return new ReplayRecord(envelope.Sequence, timestamp, envelope.SessionId, envelope.Source, envelope.ActionRequested);
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("必须选择轨迹文件。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到轨迹文件。", fullPath);
        return fullPath;
    }

    private static FileStream OpenReadLocked(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private sealed record ReplayRecord(long Sequence, long TimestampUs, string SessionId, string Source, HumanInputFrame Action);

    private sealed class MouseReplayBatch
    {
        private readonly string _sessionId;
        private readonly string _source;
        private readonly KeyboardFrame _keyboard;
        private readonly IReadOnlyList<string> _heldButtons;
        private long _dx;
        private long _dy;
        private long _wheelX;
        private long _wheelY;

        internal MouseReplayBatch(ReplayRecord first)
        {
            _sessionId = first.SessionId;
            _source = first.Source;
            _keyboard = first.Action.Keyboard;
            _heldButtons = first.Action.Mouse.HeldButtons;
            StartTimestampUs = first.TimestampUs;
            EndTimestampUs = first.TimestampUs;
            LastSequence = first.Sequence;
            AddValues(first);
        }

        internal long StartTimestampUs { get; }
        internal long EndTimestampUs { get; private set; }
        internal long LastSequence { get; private set; }
        internal long SourceEventCount { get; private set; }

        internal bool CanAdd(ReplayRecord next, long maximumWindowUs) =>
            next.TimestampUs - StartTimestampUs <= maximumWindowUs &&
            StateEquals(_keyboard.Held, next.Action.Keyboard.Held) &&
            StateEquals(_heldButtons, next.Action.Mouse.HeldButtons);

        internal void Add(ReplayRecord next)
        {
            EndTimestampUs = next.TimestampUs;
            LastSequence = next.Sequence;
            AddValues(next);
        }

        internal ReplayRecord ToReplayRecord()
        {
            int dx, dy, wheelX, wheelY;
            try
            {
                dx = checked((int)_dx);
                dy = checked((int)_dy);
                wheelX = checked((int)_wheelX);
                wheelY = checked((int)_wheelY);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("短时间窗口内累计的鼠标增量超出 Int32 范围。", ex);
            }

            return new ReplayRecord(LastSequence, EndTimestampUs, _sessionId, _source, new HumanInputFrame
            {
                Device = "mouse_replay_batch",
                TimestampUs = EndTimestampUs,
                Keyboard = _keyboard,
                Mouse = new MouseFrame
                {
                    Dx = dx,
                    Dy = dy,
                    HeldButtons = _heldButtons,
                    WheelX = wheelX,
                    WheelY = wheelY
                }
            });
        }

        private void AddValues(ReplayRecord record)
        {
            _dx = checked(_dx + record.Action.Mouse.Dx);
            _dy = checked(_dy + record.Action.Mouse.Dy);
            _wheelX = checked(_wheelX + record.Action.Mouse.WheelX);
            _wheelY = checked(_wheelY + record.Action.Mouse.WheelY);
            SourceEventCount++;
        }

        private static bool StateEquals(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
            left.Count == right.Count && new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);
    }

    private sealed record ReplayEnvelope
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; init; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("sequence")]
        public long Sequence { get; init; }

        [JsonPropertyName("executor_receive_timestamp_us")]
        public long ExecutorReceiveTimestampUs { get; init; }

        [JsonPropertyName("action_requested")]
        public HumanInputFrame? ActionRequested { get; init; }
    }
}
