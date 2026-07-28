# Architecture

## Process and thread model

`inputd` is an independent Windows process. ASP.NET Core handles loopback HTTP and WebSocket transport, but request threads never call `SendInput`.

```text
HTTP Command API ----> bounded FIFO (64) --------> dedicated Input Scheduler thread
WebSocket frames ----> latest-wins slot (1) ----->            |
                                                               v
                                                        input state machine
                                                               |
                                                               v
                                                   Windows SendInput backend
```

Dashboard trajectory replay can replace only the relative-mouse portion with `WindowsInterceptionInputBackend`. That hybrid backend emits relative motion, mouse buttons, and wheels through an installed mouse-class filter driver, while scan-code keys, Unicode text, and absolute desktop movement stay on the normal `SendInput` fallback. The backend is still owned and disposed by the same scheduler thread lifecycle; HTTP/UI threads never call either injector directly.

The scheduler handles control/safety work first. It processes at most eight consecutive realtime frames before allowing a queued command, preventing starvation. A command sequence is one FIFO work item and cannot be interleaved with another sequence.

The realtime slot contains at most one pending frame. Replacing it completes the displaced frame with `status: "superseded"`. A frame already executing cannot be retroactively replaced.

## Projects

- `InputExecutor.Core`: wire models, validation, action executor, state machine, trajectory records, and streaming fixed-rate training-frame generation.
- `InputExecutor.Windows`: source-generated `LibraryImport` plus P/Invoke declarations, scan-code map, `SendInput`, optional Interception relative-mouse adapter, target identity/activation guard, bound-window capture, Raw Input recorder, emergency hotkey.
- `InputExecutor.Scheduler`: scheduler thread, queues, session ownership, arm leases, watchdog coordination.
- `InputExecutor.Service`: loopback HTTP, WebSocket framing, bootstrap token, hosted watchdogs.
- `InputExecutor.Dashboard`: Windows Forms live keyboard/mouse visualization, service health/start controls, start/stop JSONL demonstration recording, crash-safe shortcut redaction, validated timed trajectory replay, and fixed-rate training-data generation.
- `InputExecutor.ObsBridge`: current-user-only named-pipe host that reuses the
  Raw Input recorder for OBS lifecycle capture, spools active trajectories,
  performs integrity finalization, and pairs files with the final OBS output.
- `InputExecutor.Cli`: diagnostic, session management, and offline `resample` client.
- `InputExecutor.TestMonitor`: dedicated visible target for real computer-use acceptance tests.

The Raw Input recorder owns a message-only window on its own Win32 message thread. Its message pump dispatches `WM_INPUT`, and its callback places immutable trajectory records onto an unbounded, ordered, single-reader writer channel; it never serializes JSON or writes files from the window procedure. The writer gathers events for up to 8 ms or 1024 records and commits each batch through a WriteThrough stream. Stop waits for the exact enqueue watermark to drain, flushes the sink, and reports pending/faulted state instead of silently dropping a full queue.

The OBS Lua frontend callback never records input or writes trajectory data. It
sends a small JSON lifecycle message to `inputd-obs-bridge.exe`. Start waits only
for recorder activation. Stop uses an acknowledged deferred finalization: OBS is
released immediately, while the bridge drains the lossless queue, hashes the
JSONL, writes a manifest, and atomically moves the spool file to the final video
stem.

Production events come from `WM_INPUT`. Production/game sessions install no low-level hooks. Diagnostic hooks are installed dynamically only while a recording explicitly enables executor events, observe only events carrying `WindowsSendInputBackend.ExecutorEventTag`, and are uninstalled at Stop. Synthetic validation therefore remains distinguishable from physical demonstrations without leaving a global hook active while games run. The dashboard subscribes to the same immutable frames, but coalesces only its visual refresh to 33 ms. Persistence still receives every source event in sequence.

The offline training generator streams the source JSONL and writes half-open fixed-rate windows. Integer rational boundaries avoid accumulated timing drift. It sums every mouse/wheel delta, carries held state across empty frames, and keeps ordered key/button edge transitions so sub-frame clicks cannot disappear. Source and output SHA-256 hashes plus sequence-gap counts are written to a sidecar manifest.

## Transport evolution

Realtime v1 is WebSocket JSON at `/v1/realtime`. The transport boundary is independent of scheduling and state management. A future Named Pipe transport is reserved as `\\.\pipe\inputd-realtime-v1`; it must feed the same `SubmitRealtimeFrameAsync` coordinator API and must not call the backend directly.
