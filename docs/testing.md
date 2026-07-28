# Testing and acceptance

inputd separates deterministic automated tests, opt-in Win32 injection tests, and real-application acceptance. Do not present synthetic input as evidence that physical Raw Input recording works in a protected or elevated game.

## Automated test suite

Run the default Release suite:

```powershell
dotnet test InputExecutor.sln -c Release
```

The default suite does not intentionally move the real pointer or press physical keys. It covers:

- signed relative mouse values;
- scan-code down/up mapping;
- multiple simultaneously held keys and buttons;
- state diffing between realtime frames;
- latest-wins replacement and deadline expiry;
- bounded command ordering and non-interleaving execution;
- disconnect, lease, focus-loss, target-exit, disarm, and shutdown release paths;
- JSONL sequencing, manifest creation, and atomic completion;
- hotkey removal without deleting unrelated input;
- original-timing replay and fixed-rate training-frame conversion;
- OBS bridge paths, protocol messages, queue draining, and incomplete-spool handling;
- HTTP and WebSocket service integration.

## Opt-in Windows injection tests

These tests inject real Windows input. Close sensitive applications, keep an emergency-stop path available, and run them only on a disposable target window.

```powershell
$env:INPUTD_RUN_WIN32_TESTS='1'
dotnet test tests/InputExecutor.Tests -c Release --filter Category=WindowsIntegration
```

The suite verifies positive and negative mouse movement, scan-code key transitions, multi-key holds, global hotkey lifecycle, diagnostic recorder behavior, replay cleanup, and pointer restoration where applicable.

## Scheduler and safety acceptance

For any manual acceptance run, verify all of the following:

1. HTTP and WebSocket handlers enqueue work but never invoke the backend directly.
2. Command actions remain FIFO and two concurrent command requests never interleave inside a sequence.
3. Only the newest pending realtime state frame executes.
4. An expired `deadline` produces no injected event.
5. Closing the realtime connection releases every executor-held key and mouse button.
6. Moving focus away from the bound target releases held state and pauses the session.
7. Exiting the target or client process, disarming, emergency stopping, or closing the service leaves no held input.
8. Service shutdown attempts `release_all` before scheduler disposal.

## Raw Input recording acceptance

Production recording uses Windows Raw Input and must not install global low-level keyboard or mouse hooks. Diagnostic recording may temporarily use hooks to verify inputd-tagged synthetic events; those hooks must be removed on Stop.

For a physical-input recording:

1. Start the elevated Dashboard when the target is elevated.
2. Bind the exact target and start a new recording.
3. Hold and release at least two keys, click a mouse button, scroll, and move the mouse in both signed directions.
4. Stop recording and wait for pending writes to reach zero.
5. Validate that sequence numbers begin at 1 and have no gaps.
6. Confirm the final held-key and held-button sets are empty.
7. Confirm the manifest marks the trajectory complete and its SHA-256 matches the JSONL.
8. Confirm recording start/stop hotkeys are absent while unrelated simultaneous input remains.

High-rate acceptance should generate a sustained mouse packet stream while the Dashboard remains responsive. The UI is allowed to coalesce visual refreshes, but the durable JSONL must retain the complete ordered event stream with zero dropped and pending events at completion.

## Replay acceptance

Use the included Test Monitor or another disposable offline application:

1. Select a validated complete JSONL trajectory.
2. Keep the exact bound target in the foreground.
3. Start playback and observe physical-key holds, button transitions, wheel input, and signed relative movement.
4. Interrupt playback once with the Stop hotkey and once by changing focus.
5. In both cases, confirm the scheduler clears its queue and releases every held input.
6. Replay the complete file and compare emitted event totals and aggregate `dx/dy/wheel` with the source.

Some games ignore relative `SendInput` camera movement. Test the default legacy relative-mouse replay path separately from the optional Interception path, and document the backend used. Never use compatibility testing to evade anti-cheat controls.

## Fixed-rate training-data acceptance

Convert a complete event trajectory to 20, 40, 60, and at least one custom frequency. For every output:

- frame indexes and time windows are continuous;
- source-event counts sum to the original event total;
- held state carries through empty frames;
- short press/release transitions survive within a frame;
- total signed `dx/dy/wheel` exactly equals the source;
- source and output hashes match their manifests;
- partial input is rejected unless explicitly allowed.

## OBS acceptance

Install the Lua script and bridge, then perform both a short and sustained recording:

1. OBS `RECORDING_STARTED` starts the bridge capture without blocking the scripting thread.
2. Active data is written to the per-user spool as `*.partial`.
3. OBS `RECORDING_STOPPED` drains the queue, finalizes hashes, and atomically moves the sidecar beside the final video.
4. The resulting video, `.input.jsonl`, and manifest share the same stem.
5. An interrupted OBS or bridge process retains incomplete data but never labels it complete.
6. OBS remains responsive while the bridge drains and hashes a large trajectory.

## Real-game acceptance

Real-game validation must use an offline or explicitly permitted environment. Record the target process identity, integrity level, input backend, game display mode, mouse settings, event count, sequence-gap count, dropped/pending count, final held state, and manifest hash.

A passing run contains physical key edges and positive/negative relative mouse values while the game owns the foreground. Repeat multiple start/stop cycles and at least one sustained run. Keep private recordings, screenshots, account identifiers, and machine-specific paths outside the repository.
