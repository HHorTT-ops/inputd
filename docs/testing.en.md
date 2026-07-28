# Testing and Acceptance

[简体中文](testing.md) | [English](testing.en.md) | [日本語](testing.ja.md)

inputd separates deterministic automated tests, explicitly enabled Win32 injection tests, and real-application acceptance. Synthetic injection results must not be presented as proof of physical Raw Input capture in a protected or elevated game.

## Automated suite

Run the default Release suite:

```powershell
dotnet test InputExecutor.sln -c Release
```

Default tests do not intentionally move the real pointer or press real keys. They cover:

- signed relative mouse values and scan-code down/up;
- multiple held keys and buttons;
- realtime state diffing, latest-wins, and deadline expiry;
- bounded Command FIFO ordering and non-interleaving concurrent sequences;
- release on disconnect, lease, focus loss, target exit, disarm, and shutdown;
- JSONL sequence, manifests, atomic completion, and hotkey redaction;
- original-timing replay and fixed-rate training conversion;
- OBS bridge paths, messages, drain, and partial handling;
- HTTP and WebSocket service integration.

## Opt-in Windows injection tests

These tests move the real Windows pointer and press keys. Close sensitive applications, keep an emergency-stop path available, and use a disposable test window:

```powershell
$env:INPUTD_RUN_WIN32_TESTS='1'
dotnet test tests/InputExecutor.Tests -c Release --filter Category=WindowsIntegration
```

They cover positive/negative movement, scan-code edges, multi-key holds, global hotkey lifecycle, diagnostic recording, replay cleanup, and pointer restoration where applicable.

## Scheduler and safety acceptance

Manual acceptance must verify:

1. HTTP/WebSocket handlers enqueue work and never invoke the backend directly.
2. Commands remain FIFO and concurrent sequences do not interleave.
3. Only the newest pending realtime desired-state frame executes.
4. An expired `deadline` produces no injection.
5. Closing realtime releases every executor-held input.
6. Target focus loss releases held state and pauses the session.
7. Target/client exit, disarm, emergency stop, and service shutdown leave no held input.
8. Shutdown makes a best-effort `release_all` before scheduler disposal.

## Raw Input recording acceptance

Production recording uses Windows Raw Input and must install no global low-level keyboard or mouse hook. Diagnostic mode may temporarily use hooks for tagged inputd events, but Stop must remove them.

Physical-input procedure:

1. Run the Dashboard elevated when the target is elevated.
2. Bind the exact target and begin a new recording.
3. Hold and release at least two keys, click a mouse button, scroll, and move in both signed directions.
4. Stop and wait for pending writes to reach zero.
5. Confirm `sequence` starts at 1 with no gaps.
6. Confirm final held keys/buttons are empty.
7. Confirm the manifest is complete and SHA-256 matches the JSONL.
8. Confirm recording-boundary hotkeys are absent while unrelated simultaneous input remains.

High-rate acceptance should sustain mouse packets while the Dashboard remains responsive. Visual refresh may be coalesced; durable JSONL must retain the complete ordered stream with zero dropped and pending events.

## Replay acceptance

Use Test Monitor or another disposable offline application:

1. Select a validated complete JSONL.
2. Keep the exact target foreground.
3. Play and observe physical-key holds, button edges, wheel, and signed relative movement.
4. Interrupt once by Stop hotkey and once by focus change.
5. Confirm both paths clear the queue and release every input.
6. After complete playback, compare emitted event totals and aggregate `dx/dy/wheel`.

Some games ignore relative `SendInput` camera movement. Test the default compatibility path separately from optional Interception and record the backend. Never use compatibility testing to evade anti-cheat.

## Fixed-rate training acceptance

Convert a complete trajectory to 20, 40, 60 Hz and at least one custom rate. Every output must have:

- continuous frame indexes and time windows;
- summed `source_event_count` equal to source events;
- held state carried through empty frames;
- short in-frame press/release edges preserved;
- signed `dx/dy/wheel` totals equal to the source;
- source/output SHA-256 matching manifests;
- partial or gapped data rejected unless explicitly allowed.

## OBS acceptance

After installing the Lua script and bridge, run short and sustained recordings:

1. `RECORDING_STARTED` starts bridge capture without blocking the script thread.
2. Active data is written as `*.partial` in the current-user spool.
3. `RECORDING_STOPPED` drains, hashes, and atomically moves data beside the final video.
4. Video, `.input.jsonl`, and manifest share one stem.
5. An interrupted OBS/bridge retains partial data but never marks it complete.
6. OBS remains responsive during drain and hashing of a large trajectory.

## Real-game acceptance

Use only offline or explicitly permitted environments. Record target identity, integrity level, backend, display mode, mouse settings, event and gap counts, dropped/pending counts, final held state, and manifest hash.

A passing run contains physical key edges and positive/negative relative mouse values while the game owns focus. Repeat several start/stop cycles and at least one sustained run. Never commit private recordings, screenshots, account identifiers, or machine-specific paths.
