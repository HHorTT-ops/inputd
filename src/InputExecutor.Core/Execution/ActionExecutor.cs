using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Core.Execution;

public sealed record ActionExecutorOptions(
    int DefaultPressDurationMs = 30,
    int MotionStepMs = 8,
    int MaxMotionSteps = 240,
    int MaxActionsPerCommand = 256,
    int MaxWaitMs = 30_000,
    int MaxRelativeDelta = 32_768,
    bool DefaultNoCoalesce = true);

public sealed class ActionExecutor
{
    private static readonly HashSet<string> Buttons = new(StringComparer.OrdinalIgnoreCase)
        { "left", "right", "middle", "x1", "x2" };
    private readonly IInputBackend _backend;
    private readonly InputStateMachine _state;
    private readonly IAsyncDelay _delay;
    private readonly ActionExecutorOptions _options;

    public ActionExecutor(IInputBackend backend, InputStateMachine state, IAsyncDelay delay, ActionExecutorOptions? options = null)
    {
        _backend = backend;
        _state = state;
        _delay = delay;
        _options = options ?? new ActionExecutorOptions();
    }

    public void Validate(IReadOnlyList<ActionCommand> actions)
    {
        if (actions.Count == 0) throw Bad("EMPTY_ACTIONS", "At least one action is required.");
        if (actions.Count > _options.MaxActionsPerCommand) throw Bad("TOO_MANY_ACTIONS", $"At most {_options.MaxActionsPerCommand} actions are allowed.");

        foreach (var action in actions)
        {
            var type = action.Type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "mouse_move_relative":
                    RequireDelta(action.Dx, "dx"); RequireDelta(action.Dy, "dy"); ValidateDuration(action.DurationMs, 2_000); break;
                case "mouse_move_absolute":
                    if (action.X is null || action.Y is null) throw Bad("INVALID_ACTION", "mouse_move_absolute requires x and y.");
                    ValidateDuration(action.DurationMs, 2_000); break;
                case "mouse_button":
                    if (action.Button is null || !Buttons.Contains(action.Button)) throw Bad("UNSUPPORTED_MOUSE_BUTTON", "button must be left, right, middle, x1, or x2.");
                    if (action.Action is null || !new[] { "down", "up", "press", "click", "double_click", "hold" }.Contains(action.Action, StringComparer.OrdinalIgnoreCase))
                        throw Bad("INVALID_BUTTON_ACTION", "Mouse action must be down, up, press, click, double_click, or hold.");
                    ValidateDuration(action.DurationMs, _options.MaxWaitMs); break;
                case "mouse_wheel":
                    if (action.DeltaX is null && action.DeltaY is null) throw Bad("INVALID_ACTION", "mouse_wheel requires delta_x or delta_y.");
                    break;
                case "key":
                    if (string.IsNullOrWhiteSpace(action.Code) || !_backend.SupportsKey(action.Code)) throw Bad("UNSUPPORTED_KEY", $"Unsupported physical key code '{action.Code}'.");
                    if (action.Action is null || !new[] { "down", "up", "press", "hold" }.Contains(action.Action, StringComparer.OrdinalIgnoreCase))
                        throw Bad("INVALID_KEY_ACTION", "Key action must be down, up, press, or hold.");
                    ValidateDuration(action.DurationMs, _options.MaxWaitMs); break;
                case "text":
                    if (action.Text is null) throw Bad("INVALID_ACTION", "text requires a text value.");
                    if (action.Text.Length > 16_384) throw Bad("TEXT_TOO_LONG", "A text action is limited to 16,384 UTF-16 code units.");
                    break;
                case "wait":
                    ValidateDuration(action.DurationMs, _options.MaxWaitMs, required: true); break;
                case "release_all":
                case "barrier":
                    break;
                default:
                    throw Bad("UNSUPPORTED_ACTION", $"Unsupported action type '{action.Type}'.");
            }
        }
    }

    public async Task<IReadOnlyList<EmittedInput>> ExecuteAsync(
        IReadOnlyList<ActionCommand> actions,
        Func<CancellationToken, ValueTask>? beforeEachAction,
        CancellationToken cancellationToken)
    {
        Validate(actions);
        var events = new List<EmittedInput>();
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeEachAction is not null) await beforeEachAction(cancellationToken);
            events.AddRange(await ExecuteOneAsync(action, cancellationToken));
        }
        return events;
    }

    private async Task<IReadOnlyList<EmittedInput>> ExecuteOneAsync(ActionCommand action, CancellationToken cancellationToken)
    {
        var events = new List<EmittedInput>();
        switch (action.Type.Trim().ToLowerInvariant())
        {
            case "mouse_move_relative":
                events.AddRange(await MoveRelativeAsync(action.Dx!.Value, action.Dy!.Value, action.DurationMs ?? 0, !(action.Coalesce ?? false), cancellationToken));
                break;
            case "mouse_move_absolute":
                events.Add(await _backend.MoveMouseAbsoluteAsync(action.X!.Value, action.Y!.Value, cancellationToken));
                break;
            case "mouse_button":
                events.AddRange(await ExecuteButtonAsync(action, cancellationToken));
                break;
            case "mouse_wheel":
                events.Add(await _backend.ScrollMouseAsync(action.DeltaX ?? 0, action.DeltaY ?? 0, cancellationToken));
                break;
            case "key":
                events.AddRange(await ExecuteKeyAsync(action, cancellationToken));
                break;
            case "text":
                events.AddRange(await _backend.TypeTextAsync(action.Text!, cancellationToken));
                break;
            case "wait":
                await _delay.DelayAsync(TimeSpan.FromMilliseconds(action.DurationMs!.Value), cancellationToken);
                break;
            case "release_all":
                events.AddRange(await _state.ReleaseAllAsync(_backend, cancellationToken));
                break;
            case "barrier":
                break;
        }
        return events;
    }

    private async Task<IReadOnlyList<EmittedInput>> ExecuteKeyAsync(ActionCommand action, CancellationToken cancellationToken)
    {
        var code = action.Code!;
        var verb = action.Action!.ToLowerInvariant();
        if (verb == "down") return await _state.SetKeyAsync(_backend, code, true, cancellationToken);
        if (verb == "up") return await _state.SetKeyAsync(_backend, code, false, cancellationToken);

        var events = new List<EmittedInput>();
        events.AddRange(await _state.SetKeyAsync(_backend, code, true, cancellationToken));
        await _delay.DelayAsync(TimeSpan.FromMilliseconds(action.DurationMs ?? _options.DefaultPressDurationMs), cancellationToken);
        events.AddRange(await _state.SetKeyAsync(_backend, code, false, cancellationToken));
        return events;
    }

    private async Task<IReadOnlyList<EmittedInput>> ExecuteButtonAsync(ActionCommand action, CancellationToken cancellationToken)
    {
        var button = action.Button!;
        var verb = action.Action!.ToLowerInvariant();
        if (verb == "down") return await _state.SetMouseButtonAsync(_backend, button, true, cancellationToken);
        if (verb == "up") return await _state.SetMouseButtonAsync(_backend, button, false, cancellationToken);

        var clicks = verb == "double_click" ? 2 : 1;
        var events = new List<EmittedInput>();
        for (var i = 0; i < clicks; i++)
        {
            events.AddRange(await _state.SetMouseButtonAsync(_backend, button, true, cancellationToken));
            await _delay.DelayAsync(TimeSpan.FromMilliseconds(action.DurationMs ?? _options.DefaultPressDurationMs), cancellationToken);
            events.AddRange(await _state.SetMouseButtonAsync(_backend, button, false, cancellationToken));
            if (i + 1 < clicks) await _delay.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        return events;
    }

    private async Task<IReadOnlyList<EmittedInput>> MoveRelativeAsync(int dx, int dy, int durationMs, bool noCoalesce, CancellationToken cancellationToken)
    {
        if (durationMs <= 0)
        {
            return new[] { await _backend.MoveMouseRelativeAsync(dx, dy, noCoalesce, cancellationToken) };
        }

        var steps = Math.Clamp((int)Math.Ceiling(durationMs / (double)_options.MotionStepMs), 1, _options.MaxMotionSteps);
        var events = new List<EmittedInput>(steps);
        var emittedX = 0;
        var emittedY = 0;
        for (var i = 1; i <= steps; i++)
        {
            var targetX = (int)Math.Round(dx * i / (double)steps, MidpointRounding.AwayFromZero);
            var targetY = (int)Math.Round(dy * i / (double)steps, MidpointRounding.AwayFromZero);
            var partX = targetX - emittedX;
            var partY = targetY - emittedY;
            if (partX != 0 || partY != 0)
                events.Add(await _backend.MoveMouseRelativeAsync(partX, partY, noCoalesce, cancellationToken));
            emittedX = targetX;
            emittedY = targetY;
            if (i < steps) await _delay.DelayAsync(TimeSpan.FromMilliseconds(durationMs / (double)steps), cancellationToken);
        }
        return events;
    }

    private void RequireDelta(int? value, string name)
    {
        if (value is null || Math.Abs((long)value.Value) > _options.MaxRelativeDelta)
            throw Bad("INVALID_RELATIVE_DELTA", $"{name} is required and must be within ±{_options.MaxRelativeDelta} counts.");
    }

    private static void ValidateDuration(int? duration, int max, bool required = false)
    {
        if (required && duration is null) throw Bad("INVALID_DURATION", "duration_ms is required.");
        if (duration is < 0 || duration > max) throw Bad("INVALID_DURATION", $"duration_ms must be between 0 and {max}.");
    }

    private static InputExecutorException Bad(string code, string message) => new(code, message, 400);
}
