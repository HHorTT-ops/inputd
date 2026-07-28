using System.Collections.Concurrent;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Tests;

internal sealed record BackendCall(int ThreadId, string Kind, string Detail);

internal sealed class RecordingBackend : IInputBackend
{
    private readonly ConcurrentQueue<BackendCall> _calls = new();
    private long _time;
    public string Name => "recording-backend";
    public IReadOnlyList<BackendCall> Calls => _calls.ToArray();
    public ManualResetEventSlim FirstCall { get; } = new(false);
    public bool SupportsKey(string code) => !string.IsNullOrWhiteSpace(code) && code.StartsWithAny("Key", "Shift", "Control", "Alt", "Space", "Enter", "Escape", "F");

    public ValueTask<EmittedInput> MoveMouseRelativeAsync(int dx, int dy, bool noCoalesce, CancellationToken cancellationToken) =>
        One("mouse_move_relative", $"{dx},{dy}", cancellationToken);
    public ValueTask<EmittedInput> MoveMouseAbsoluteAsync(int x, int y, CancellationToken cancellationToken) =>
        One("mouse_move_absolute", $"{x},{y}", cancellationToken);
    public ValueTask<EmittedInput> SetMouseButtonAsync(string button, bool isDown, CancellationToken cancellationToken) =>
        One("mouse_button", $"{button}:{(isDown ? "down" : "up")}", cancellationToken);
    public ValueTask<EmittedInput> ScrollMouseAsync(int deltaX, int deltaY, CancellationToken cancellationToken) =>
        One("mouse_wheel", $"{deltaX},{deltaY}", cancellationToken);
    public ValueTask<EmittedInput> SetKeyAsync(string code, bool isDown, CancellationToken cancellationToken) =>
        One("key", $"{code}:{(isDown ? "down" : "up")}", cancellationToken);

    public ValueTask<IReadOnlyList<EmittedInput>> TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        var emitted = One("text", text, cancellationToken).Result;
        return ValueTask.FromResult<IReadOnlyList<EmittedInput>>(new[] { emitted });
    }

    private ValueTask<EmittedInput> One(string kind, string detail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new BackendCall(Environment.CurrentManagedThreadId, kind, detail);
        _calls.Enqueue(call);
        FirstCall.Set();
        return ValueTask.FromResult(new EmittedInput { Kind = kind, Detail = detail, EmittedAtUs = Interlocked.Increment(ref _time) });
    }
}

internal sealed class FakeClock : IMonotonicClock
{
    public long NowMicroseconds { get; set; } = 1_000_000;
}

internal sealed class FakeTargetGuard : ITargetGuard
{
    public bool Exists { get; set; } = true;
    public bool Foreground { get; set; } = true;
    public TargetBinding Target { get; set; } = new()
    {
        WindowHandle = "0x1234",
        ProcessId = 42,
        ProcessName = "test-game",
        ProcessPath = "C:\\games\\test-game.exe",
        ProcessStartTimeUtcTicks = 123
    };

    public TargetBinding CaptureForeground() => Target;
    public TargetBinding CaptureProcess(int processId) => Target with { ProcessId = processId };
    public bool TryActivate(TargetBinding target) { Foreground = true; return true; }
    public TargetCheck Validate(TargetBinding target, bool requireForeground)
    {
        if (!Exists) return new(false, "TARGET_WINDOW_CLOSED", "closed");
        if (requireForeground && !Foreground) return new(false, "TARGET_NOT_FOREGROUND", "not foreground");
        return new(true);
    }
}

internal static class StringTestExtensions
{
    public static bool StartsWithAny(this string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
