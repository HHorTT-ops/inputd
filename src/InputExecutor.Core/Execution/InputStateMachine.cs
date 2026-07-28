using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Core.Execution;

public sealed class InputStateMachine
{
    private readonly HashSet<string> _heldKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _heldButtons = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> HeldKeys => _heldKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> HeldButtons => _heldButtons.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public async ValueTask<IReadOnlyList<EmittedInput>> SetKeyAsync(
        IInputBackend backend, string code, bool isDown, CancellationToken cancellationToken)
    {
        if (isDown == _heldKeys.Contains(code))
        {
            return Array.Empty<EmittedInput>();
        }

        var emitted = await backend.SetKeyAsync(code, isDown, cancellationToken);
        if (isDown) _heldKeys.Add(code); else _heldKeys.Remove(code);
        return new[] { emitted };
    }

    public async ValueTask<IReadOnlyList<EmittedInput>> SetMouseButtonAsync(
        IInputBackend backend, string button, bool isDown, CancellationToken cancellationToken)
    {
        if (isDown == _heldButtons.Contains(button))
        {
            return Array.Empty<EmittedInput>();
        }

        var emitted = await backend.SetMouseButtonAsync(button, isDown, cancellationToken);
        if (isDown) _heldButtons.Add(button); else _heldButtons.Remove(button);
        return new[] { emitted };
    }

    public async ValueTask<IReadOnlyList<EmittedInput>> ApplyRealtimeAsync(
        IInputBackend backend,
        RealtimeFrame frame,
        bool noCoalesce,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? repeatedKeyDowns = null)
    {
        var requestedKeys = new HashSet<string>(frame.Keyboard.Held, StringComparer.OrdinalIgnoreCase);
        var requestedButtons = new HashSet<string>(frame.Mouse.HeldButtons, StringComparer.OrdinalIgnoreCase);
        var previouslyHeldKeys = new HashSet<string>(_heldKeys, StringComparer.OrdinalIgnoreCase);
        var events = new List<EmittedInput>();

        foreach (var key in _heldKeys.Except(requestedKeys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray())
        {
            events.AddRange(await SetKeyAsync(backend, key, false, cancellationToken));
        }
        foreach (var button in _heldButtons.Except(requestedButtons, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray())
        {
            events.AddRange(await SetMouseButtonAsync(backend, button, false, cancellationToken));
        }
        foreach (var key in requestedKeys.Except(_heldKeys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            events.AddRange(await SetKeyAsync(backend, key, true, cancellationToken));
        }
        foreach (var button in requestedButtons.Except(_heldButtons, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            events.AddRange(await SetMouseButtonAsync(backend, button, true, cancellationToken));
        }
        // Realtime policy frames are state snapshots, so duplicate downs are normally
        // suppressed. A lossless physical-trajectory replay is different: Windows emits
        // typematic down packets while a key is held and some games consume those packets.
        // Re-emit only explicit repeats that were already held before this frame; the first
        // down is still produced exactly once by the state transition above.
        if (repeatedKeyDowns is not null)
        {
            foreach (var key in repeatedKeyDowns.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                if (previouslyHeldKeys.Contains(key) && requestedKeys.Contains(key))
                    events.Add(await backend.SetKeyAsync(key, true, cancellationToken));
            }
        }
        if (frame.Mouse.Dx != 0 || frame.Mouse.Dy != 0)
        {
            events.Add(await backend.MoveMouseRelativeAsync(frame.Mouse.Dx, frame.Mouse.Dy, noCoalesce, cancellationToken));
        }
        if (frame.Mouse.WheelX != 0 || frame.Mouse.WheelY != 0)
        {
            events.Add(await backend.ScrollMouseAsync(frame.Mouse.WheelX, frame.Mouse.WheelY, cancellationToken));
        }

        return events;
    }

    public async ValueTask<IReadOnlyList<EmittedInput>> ReleaseAllAsync(
        IInputBackend backend, CancellationToken cancellationToken)
    {
        var events = new List<EmittedInput>();
        Exception? firstError = null;

        foreach (var key in _heldKeys.OrderBy(x => x).ToArray())
        {
            try { events.Add(await backend.SetKeyAsync(key, false, cancellationToken)); }
            catch (Exception ex) { firstError ??= ex; }
        }
        foreach (var button in _heldButtons.OrderBy(x => x).ToArray())
        {
            try { events.Add(await backend.SetMouseButtonAsync(button, false, cancellationToken)); }
            catch (Exception ex) { firstError ??= ex; }
        }

        _heldKeys.Clear();
        _heldButtons.Clear();
        if (firstError is not null)
        {
            throw new InputExecutorException("RELEASE_ALL_FAILED", "One or more held inputs could not be released.", 500, inner: firstError);
        }
        return events;
    }
}
