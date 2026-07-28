# Dashboard Guide

[简体中文](dashboard.md) | [English](dashboard.en.md) | [日本語](dashboard.ja.md)

## Starting the Dashboard

Windows x64 and the .NET 10 Desktop Runtime are required. The .NET 10 SDK already includes the runtime.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-dashboard.ps1
```

Run the complete published directory, not the EXE by itself:

```text
artifacts\inputd-dashboard-win-x64\inputd-dashboard.exe
```

The application requests administrator elevation. This is required when recording an elevated game: Windows may stop delivering background Raw Input to a lower-integrity recorder while the game owns the foreground.

From source:

```powershell
dotnet run --project src/InputExecutor.Dashboard -c Release
```

## Interface

- The keyboard view turns held keys black and restores them on release.
- The mouse view shows button state, the latest relative direction, `ΔX/ΔY`, wheel values, and screen position.
- **Start inputd** launches the independent service next to the Dashboard.
- **Start/Stop recording** creates and safely finalizes an event-level JSONL trajectory.
- **Open recording directory** opens the output folder.
- **Generate training data** converts a complete JSONL to 20/40/60 Hz or a custom 1–1000 Hz dataset and writes an integrity manifest.
- **Hotkey settings** changes the global record and replay shortcuts.
- **Select recording** validates an event-level `human_raw_input` JSONL before injection.
- **Play/Stop playback** replays the selected held state, signed mouse deltas, buttons, and wheel values at original relative timing, or cancels and releases them.
- **Clear state** resets only the visible counters; it does not delete recordings.

While idle, the Dashboard remembers the most recently focused external window. Focus the game once, return to the Dashboard, and start recording. The target HWND, PID, path, and title are then fixed for that recording.

Default output:

```text
%USERPROFILE%\Documents\inputd-recordings
```

Override it with `INPUTD_DASHBOARD_RECORDING_DIR` before startup.

## Global hotkeys

- `Ctrl+Alt+F9`: start recording
- `Ctrl+Alt+F10`: stop recording
- `Ctrl+Alt+F7`: play the selected trajectory
- `Ctrl+Alt+F8`: stop playback
- `Ctrl+Alt+F12`: fixed emergency stop

Settings are stored in:

```text
%LOCALAPPDATA%\inputd\dashboard.settings.json
```

Hotkeys use `RegisterHotKey`, not a low-level keyboard hook. The Dashboard rejects duplicate or incomplete combinations and reports registration conflicts.

Recording waits for the boundary shortcut to be fully released before starting or stopping. After Stop, a streamed atomic rewrite removes only the start/stop shortcut's physical codes and held state. It retains every event row and unrelated mouse/key value. If rewrite fails, the original durable file remains.

## Replay and relative mouse compatibility

Playback reads the trajectory locally and waits for the dedicated single-thread `InputScheduler`; it does not send one HTTP request per event. Adjacent high-polling mouse packets with identical held state and at most 2 ms spacing can be combined for injection while preserving the exact signed `dx/dy/wheel` sum. The source training file is never modified.

The default unchecked **Kernel mouse compatibility** mode uses Win32 `mouse_event` for relative camera motion and `SendInput` for scan-code keys, buttons, wheel, absolute movement, and text. It requires no driver, but compatibility must be verified in an offline or explicitly permitted target.

The optional checked mode uses `WindowsInterceptionInputBackend` for relative movement, buttons, and wheel. The repository does not bundle or install `interception.dll` or its kernel driver. Playback fails before injection when the dependency is unavailable. See [relative mouse replay backends](raw-input-replay.en.md).

Playback stops and calls `release_all` on focus loss, target exit, Stop, Dashboard shutdown, or `Ctrl+Alt+F12`. Recording and playback are mutually exclusive.

## Physical and diagnostic input

The Dashboard previews physical Raw Input as soon as it starts, whether or not a recording is active.

With **Synthetic input diagnostics** disabled, JSONL contains only physical keyboard/mouse demonstrations and no global low-level hook is installed. When explicitly enabled, hooks exist only for that recording and retain inputd-tagged events as `injected_tagged: true`; they are removed immediately at Stop.

Keep diagnostics disabled for game recording. Production capture uses `WM_INPUT + RIDEV_INPUTSINK`, does not inject into or hook the game process, and does not attempt to bypass anti-cheat controls.

## High-rate input and data integrity

The UI and durable recorder use separate paths:

- every Raw Input event enters an ordered, non-dropping recording channel with continuous `sequence`;
- the UI reads a coalesced snapshot roughly every 33 ms;
- visual coalescing never samples or deletes JSONL events;
- the writer batches up to 1024 records and uses a WriteThrough stream;
- Stop waits for pending writes to reach zero and performs a final Flush;
- only a non-empty run with zero pending and zero failed events is marked complete.

Use local storage with adequate free space for high-polling-rate mice. If storage or I/O fails, the run is marked unusable instead of silently succeeding.

The fixed-rate generator sums all deltas and keeps short key/button edges in `transitions`. See [training data](training-data.en.md). Service-side injection remains Disarmed until a target-bound session is explicitly armed; all safety faults trigger `release_all`.
