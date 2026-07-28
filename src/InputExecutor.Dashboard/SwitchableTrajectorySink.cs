using System.Text.Json;
using System.Text.Json.Nodes;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Recording;

namespace InputExecutor.Dashboard;

internal sealed class SwitchableTrajectorySink : IBatchedTrajectorySink
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _fallbackShortcutWindowUs;
    private readonly List<ShortcutSuppression> _suppressions = [];
    private JsonLinesTrajectorySink? _inner;
    private bool _includeInjected;
    private long _persisted;

    internal bool Active => Volatile.Read(ref _inner) is not null;
    internal string? CurrentPath { get; private set; }
    internal long PersistedCount => Interlocked.Read(ref _persisted);

    internal SwitchableTrajectorySink(TimeSpan? shortcutSearchWindow = null)
    {
        var window = shortcutSearchWindow ?? TimeSpan.FromSeconds(3);
        if (window < TimeSpan.Zero || window > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(shortcutSearchWindow));
        _fallbackShortcutWindowUs = checked((long)(window.TotalMilliseconds * 1_000));
    }

    internal async Task StartAsync(string path, bool includeInjected, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is not null) await StopCoreAsync(cancellationToken);
            _inner = new JsonLinesTrajectorySink(path);
            CurrentPath = Path.GetFullPath(path);
            _includeInjected = includeInjected;
            _suppressions.Clear();
            Interlocked.Exchange(ref _persisted, 0);
        }
        finally { _gate.Release(); }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var inner = _inner;
        _inner = null;
        if (inner is null) return;

        await inner.DisposeAsync();
        var completed = _suppressions.Where(item => item.EndTimestampUs is not null).ToArray();
        if (completed.Length > 0 && CurrentPath is not null)
            await RewriteWithoutShortcutsAsync(CurrentPath, completed, cancellationToken);
        _suppressions.Clear();
    }

    internal async Task BeginShortcutSuppressionAsync(
        IReadOnlyCollection<string> physicalCodes,
        long hotkeyTimestampUs,
        CancellationToken cancellationToken = default)
    {
        var codes = new HashSet<string>(physicalCodes.Where(code => !string.IsNullOrWhiteSpace(code)), StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is not null) _suppressions.Add(new ShortcutSuppression(codes, hotkeyTimestampUs));
        }
        finally { _gate.Release(); }
    }

    internal async Task CompleteShortcutSuppressionAsync(
        long hotkeyTimestampUs,
        long releasedTimestampUs,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var suppression = _suppressions.LastOrDefault(item =>
                item.HotkeyTimestampUs == hotkeyTimestampUs && item.EndTimestampUs is null);
            if (suppression is not null)
                suppression.EndTimestampUs = Math.Max(hotkeyTimestampUs, releasedTimestampUs);
        }
        finally { _gate.Release(); }
    }

    public ValueTask WriteAsync(TrajectoryRecord record, CancellationToken cancellationToken) =>
        WriteBatchAsync([record], cancellationToken);

    public async ValueTask WriteBatchAsync(IReadOnlyList<TrajectoryRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is null) return;
            var accepted = _includeInjected
                ? records
                : records.Where(record => record.ActionRequested is not HumanInputFrame { InjectedTagged: true }).ToArray();
            if (accepted.Count == 0) return;
            await _inner.WriteBatchAsync(accepted, cancellationToken);
            Interlocked.Add(ref _persisted, accepted.Count);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is not null) await _inner.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task RewriteWithoutShortcutsAsync(
        string path,
        IReadOnlyList<ShortcutSuppression> suppressions,
        CancellationToken cancellationToken)
    {
        await FindSuppressionStartsAsync(path, suppressions, cancellationToken);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.redacting";
        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var reader = new StreamReader(input, System.Text.Encoding.UTF8, true, 256 * 1024, leaveOpen: true))
            await using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), 256 * 1024, leaveOpen: true))
            {
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    var rewritten = RedactLine(line, suppressions);
                    await writer.WriteLineAsync(rewritten.AsMemory(), cancellationToken);
                }
                await writer.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private async Task FindSuppressionStartsAsync(
        string path,
        IReadOnlyList<ShortcutSuppression> suppressions,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 256 * 1024);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!TryReadFrame(line, out var timestampUs, out var frame)) continue;
            foreach (var suppression in suppressions)
            {
                if (timestampUs > suppression.HotkeyTimestampUs) continue;
                var touches = TouchesShortcut(frame, suppression.Codes);
                if (touches)
                {
                    if (!suppression.InCandidateChord) suppression.CandidateStartTimestampUs = timestampUs;
                    suppression.InCandidateChord = true;
                }
                else
                {
                    suppression.InCandidateChord = false;
                    suppression.CandidateStartTimestampUs = null;
                }
            }
        }

        foreach (var suppression in suppressions)
            suppression.StartTimestampUs = suppression.CandidateStartTimestampUs ??
                                           Math.Max(0, suppression.HotkeyTimestampUs - _fallbackShortcutWindowUs);
    }

    private static string RedactLine(string line, IReadOnlyList<ShortcutSuppression> suppressions)
    {
        if (!TryReadFrame(line, out var timestampUs, out _)) return line;
        var active = suppressions.Where(item => item.StartTimestampUs is not null && item.EndTimestampUs is not null &&
                                                timestampUs >= item.StartTimestampUs && timestampUs <= item.EndTimestampUs).ToArray();
        if (active.Length == 0) return line;

        var codes = new HashSet<string>(active.SelectMany(item => item.Codes), StringComparer.OrdinalIgnoreCase);
        if (JsonNode.Parse(line) is not JsonObject root || root["action_requested"] is not JsonObject action) return line;
        if (action["keyboard"] is JsonObject keyboard && keyboard["held"] is JsonArray held)
        {
            var cleaned = new JsonArray();
            foreach (var node in held)
                if (node is JsonValue value && value.TryGetValue<string>(out var code) && !codes.Contains(code)) cleaned.Add(code);
            keyboard["held"] = cleaned;
        }
        if (action["changed_key"] is JsonValue changed && changed.TryGetValue<string>(out var changedCode) && codes.Contains(changedCode))
        {
            action["changed_key"] = null;
            action["key_down"] = null;
        }
        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static bool TryReadFrame(string line, out long timestampUs, out HumanInputFrame frame)
    {
        timestampUs = 0;
        frame = new HumanInputFrame();
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            timestampUs = root.GetProperty("executor_receive_timestamp_us").GetInt64();
            frame = root.GetProperty("action_requested").Deserialize<HumanInputFrame>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? new HumanInputFrame();
            return true;
        }
        catch { return false; }
    }

    private static bool TouchesShortcut(HumanInputFrame frame, HashSet<string> codes) =>
        (frame.ChangedKey is not null && codes.Contains(frame.ChangedKey)) || frame.Keyboard.Held.Any(codes.Contains);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private sealed class ShortcutSuppression(HashSet<string> codes, long hotkeyTimestampUs)
    {
        internal HashSet<string> Codes { get; } = codes;
        internal long HotkeyTimestampUs { get; } = hotkeyTimestampUs;
        internal long? EndTimestampUs { get; set; }
        internal long? CandidateStartTimestampUs { get; set; }
        internal long? StartTimestampUs { get; set; }
        internal bool InCandidateChord { get; set; }
    }
}
