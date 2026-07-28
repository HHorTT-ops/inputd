using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows;

/// <summary>
/// Game-replay compatibility backend. Some game camera paths accept legacy relative
/// mouse packets while rejecting equivalent SendInput movement. All other operations
/// remain delegated to the normal SendInput backend.
/// </summary>
public sealed class WindowsLegacyMouseReplayBackend : IInputBackend, IDisposable, IAsyncDisposable
{
    private readonly IInputBackend _fallback;
    private readonly IMonotonicClock _clock;
    private readonly bool _ownsFallback;

    public WindowsLegacyMouseReplayBackend(
        IInputBackend? fallback = null,
        IMonotonicClock? clock = null,
        bool ownsFallback = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Legacy mouse replay is available only on Windows.");
        _clock = clock ?? new SystemMonotonicClock();
        _fallback = fallback ?? new WindowsSendInputBackend(
            _clock, new WindowsSendInputOptions(TagInjectedEvents: false));
        _ownsFallback = ownsFallback;
    }

    public string Name => "windows-legacy-mouse-replay";
    public bool SupportsKey(string code) => _fallback.SupportsKey(code);

    public ValueTask<EmittedInput> MoveMouseRelativeAsync(
        int dx, int dy, bool noCoalesce, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeMethods.MouseEvent(
            NativeMethods.MOUSEEVENTF_MOVE,
            unchecked((uint)dx),
            unchecked((uint)dy),
            0,
            UIntPtr.Zero);
        return ValueTask.FromResult(new EmittedInput
        {
            Kind = "mouse_move_relative",
            Detail = $"dx={dx},dy={dy},api=mouse_event",
            EmittedAtUs = _clock.NowMicroseconds
        });
    }

    public ValueTask<EmittedInput> MoveMouseAbsoluteAsync(int x, int y, CancellationToken cancellationToken) =>
        _fallback.MoveMouseAbsoluteAsync(x, y, cancellationToken);

    public ValueTask<EmittedInput> SetMouseButtonAsync(string button, bool isDown, CancellationToken cancellationToken) =>
        _fallback.SetMouseButtonAsync(button, isDown, cancellationToken);

    public ValueTask<EmittedInput> ScrollMouseAsync(int deltaX, int deltaY, CancellationToken cancellationToken) =>
        _fallback.ScrollMouseAsync(deltaX, deltaY, cancellationToken);

    public ValueTask<EmittedInput> SetKeyAsync(string code, bool isDown, CancellationToken cancellationToken) =>
        _fallback.SetKeyAsync(code, isDown, cancellationToken);

    public ValueTask<IReadOnlyList<EmittedInput>> TypeTextAsync(string text, CancellationToken cancellationToken) =>
        _fallback.TypeTextAsync(text, cancellationToken);

    public void Dispose()
    {
        if (_ownsFallback && _fallback is IDisposable disposable) disposable.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsFallback) return;
        if (_fallback is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (_fallback is IDisposable disposable) disposable.Dispose();
    }
}
