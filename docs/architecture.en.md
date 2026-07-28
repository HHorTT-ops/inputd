# Architecture

[简体中文](architecture.md) | [English](architecture.en.md) | [日本語](architecture.ja.md)

## Processes, threads, and queues

`inputd` is an independent Windows-local process. ASP.NET Core handles loopback HTTP and WebSocket transport, but request threads never call `SendInput` directly.

```text
HTTP Command API ──> bounded FIFO (64) ─────┐
WebSocket frames ──> latest-wins slot (1) ──┼─> dedicated Input Scheduler thread
                                             │
                                             ├─> input state machine
                                             └─> Windows input backend
```

A command sequence is one FIFO work item, so two sequences cannot interleave. Safety work has priority. After at most eight consecutive realtime frames, the scheduler gives a waiting command a chance to run to prevent starvation.

The realtime queue retains at most one pending desired-state frame. Replacing it completes the displaced frame with `superseded`; a frame that has already started cannot be replaced retroactively.

Dashboard replay is serialized by its own `InputScheduler`. The default hybrid uses `WindowsLegacyMouseReplayBackend` for relative motion and `WindowsSendInputBackend` for other actions. When the optional compatibility mode is explicitly enabled, relative motion, mouse buttons, and wheels can use `WindowsInterceptionInputBackend`; scan-code keys, Unicode text, and absolute movement remain on `SendInput`.

## Projects

- `InputExecutor.Core`: protocol models, validation, action execution, state machine, trajectory records, and fixed-rate training-data generation.
- `InputExecutor.Windows`: `LibraryImport`/PInvoke, scan-code mapping, `SendInput`, optional compatibility backends, target guard, screenshots, Raw Input recording, and the emergency hotkey.
- `InputExecutor.Scheduler`: the single-thread scheduler, bounded queues, session ownership, arm leases, and watchdog coordination.
- `InputExecutor.Service`: loopback HTTP, WebSocket framing, the bootstrap access token, and hosted watchdogs.
- `InputExecutor.Dashboard`: WinForms status UI, service controls, JSONL recording, hotkey redaction, trajectory replay, and training-data generation.
- `InputExecutor.ObsBridge`: a current-user-only named-pipe service that reuses the Raw Input recorder for OBS lifecycle capture and file pairing.
- `InputExecutor.Cli`: diagnostics, session management, and offline `resample`.
- `InputExecutor.TestMonitor`: a visible target for real Computer Use acceptance.

## Lossless recording path

The Raw Input recorder owns a message-only window on its own Win32 message thread. Its `WM_INPUT` callback only places immutable events onto an ordered, single-reader writer channel; it never serializes JSON or writes files from the window procedure.

The writer gathers up to 8 ms or 1024 records and commits each batch through a WriteThrough stream. Stop waits for the exact enqueue watermark to drain, flushes the file, and reports pending or faulted state instead of silently dropping a full bounded queue.

Production recording reads `WM_INPUT` and installs no global low-level hooks. Hooks are installed temporarily only when synthetic-input diagnostics are explicitly enabled, and they recognize only inputd events carrying `ExecutorEventTag`. They are removed immediately at Stop.

The Dashboard coalesces only visual refresh to roughly 33 ms; persistence still receives every source event.

## OBS path

The OBS Lua callback sends only a small JSON lifecycle message. It does not record input or write trajectories. `inputd-obs-bridge.exe` starts the recorder, drains the queue, calculates SHA-256, writes the manifest, and atomically moves the spool file beside the final video.

The Stop callback returns after the bridge accepts finalization, so draining and hashing a large trajectory cannot block the OBS frontend thread.

## Training-data conversion

The offline generator streams event JSONL into half-open fixed-rate windows. Integer rational boundaries avoid accumulated timing drift. It sums every mouse and wheel delta, carries held state across empty frames, and preserves ordered key/button edges in `transitions`.

The output manifest records source and output SHA-256 values, event and frame counts, empty-frame counts, and sequence gaps.

## Transport evolution

Realtime v1 is WebSocket JSON at `/v1/realtime`. Transport is separated from scheduling and state management. The reserved future Named Pipe is `\\.\pipe\inputd-realtime-v1`; it must call the same `SubmitRealtimeFrameAsync` coordinator API and must never invoke an input backend directly.
