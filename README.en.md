# inputd

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md)

A Windows-local keyboard and mouse executor for visual agents, game policies, and training-data capture.

`inputd` is built with C# and .NET 10. It centralizes Win32 input in an independent process and one scheduler thread, while providing a low-frequency HTTP Command API, a persistent WebSocket Realtime API, lossless Raw Input recording, trajectory replay, a Windows Dashboard, and OBS-paired capture.

> This is an early Windows-only release. It is not an anti-cheat bypass. Follow the target application's terms of service and applicable laws.

## Highlights

- Relative and absolute mouse movement, five mouse buttons, and horizontal/vertical wheel input.
- Scan-code physical keyboard input and Unicode text input.
- A bounded FIFO command queue and a capacity-one latest-wins realtime queue.
- Desired-state frames are diffed into key and mouse-button down/up events.
- Automatic `release_all` on disconnect, lease timeout, focus loss, target exit, shutdown, or emergency stop.
- Event-level Windows Raw Input JSONL that preserves high-polling-rate mouse deltas and ordering.
- Recording validation, original-timing replay, and lossless fixed-rate training-frame conversion.
- WinForms Dashboard for live state, recording, replay, file selection, and configurable global hotkeys.
- OBS Lua script plus an independent .NET bridge for paired video/input files.

## Architecture

```text
AI / scripts / plugins
   ├─ HTTP JSON Command API ── bounded FIFO ─┐
   └─ WebSocket Realtime API ─ latest wins ──├─ Input Scheduler ─ Win32 SendInput
                                             └─ state machine / safety watchdog

Physical input ─ Windows Raw Input ─ lossless JSONL ─ validate / replay / convert
OBS ─ Lua script ─ Named Pipe ─ inputd-obs-bridge ─ sidecar input file
```

HTTP and WebSocket handlers never call `SendInput` directly. Every injected event is serialized by the single Input Scheduler thread, preventing concurrent commands from interleaving.

## Requirements

- Windows 10 or Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- When the target is elevated, run Dashboard/inputd at the same or a higher integrity level

## Quick start

```powershell
git clone https://github.com/irregular-haguruma/inputd.git
cd inputd
dotnet build InputExecutor.sln -c Release
dotnet run --project src/InputExecutor.Dashboard -c Release
```

The Dashboard is the graphical interface. To publish a double-clickable Windows bundle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-dashboard.ps1
```

Run:

```text
artifacts\inputd-dashboard-win-x64\inputd-dashboard.exe
```

The Dashboard requests elevation once and can start the adjacent `inputd.exe`. Recordings default to `Documents\inputd-recordings`.

Service-only usage:

```powershell
dotnet run --project src/InputExecutor.Service -c Release
dotnet run --project src/InputExecutor.Cli -c Release -- health
dotnet run --project src/InputExecutor.Cli -c Release -- create
```

On first start, inputd creates `%LOCALAPPDATA%\inputd\access.token`. Keep the intended target in the foreground while creating a session, save the returned Session ID and capability token, and explicitly arm the session.

## Recording and replay

Default Dashboard hotkeys:

| Action | Default |
|---|---|
| Start recording | `Ctrl+Alt+F9` |
| Stop recording | `Ctrl+Alt+F10` |
| Play trajectory | `Ctrl+Alt+F7` |
| Stop playback | `Ctrl+Alt+F8` |
| Emergency stop | `Ctrl+Alt+F12` |

Hotkeys are configurable. Recording-boundary hotkeys are removed from the final trajectory. The recorder writes high-rate input through an independent queue and durable sink; the UI reads only a roughly 30 FPS coalesced snapshot, so a slow UI refresh does not drop training events.

Completed recordings can be validated, replayed at their original relative timing, or converted to 20/40/60 Hz and custom fixed-rate training frames. Conversion verifies event totals and aggregate `dx/dy/wheel` values. See [recording and replay](docs/raw-input-replay.en.md) and [training data](docs/training-data.en.md).

## Relative mouse movement in games

Normal injection uses `SendInput`. Some games accept injected keys and clicks but ignore relative `SendInput` camera movement. For replay, the Dashboard therefore uses the Windows `mouse_event` compatibility path only for relative movement by default; all other actions remain on `SendInput`.

The optional Interception backend is for explicit compatibility testing only. Its third-party DLL and kernel driver are not bundled or installed automatically. Do not use any backend to evade anti-cheat controls.

## OBS-paired capture

The OBS integration lives in [`adapters/obs-inputd-plugin`](adapters/obs-inputd-plugin):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

Starting an OBS recording starts Raw Input capture. Stopping it writes:

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

Interrupted data remains `*.partial` and is never reported as a complete training trajectory. See the [OBS guide](docs/obs.en.md).

## API and safety

- Command API: `http://127.0.0.1:17841`
- Realtime API: `ws://127.0.0.1:17841/v1/realtime`
- Numeric loopback binding only; remote binding is rejected.
- The service starts disarmed and session creation requires a random access token.
- Sessions bind to HWND, PID, executable path/start time, and a process-name allowlist.
- Every action validates foreground ownership and request limits.
- Realtime frames carry `sequence`, monotonic timestamp, `deadline`, held keys/buttons, relative `dx/dy`, and wheel state.
- New frames supersede pending old frames; expired frames are discarded.

See [protocol.en.md](docs/protocol.en.md) and [safety.en.md](docs/safety.en.md).

## Tests

```powershell
dotnet test InputExecutor.sln -c Release
```

Real Win32 integration tests move the physical pointer and press/release real keys, so they require an explicit opt-in:

```powershell
$env:INPUTD_RUN_WIN32_TESTS='1'
dotnet test tests/InputExecutor.Tests -c Release --filter Category=WindowsIntegration
```

See [testing.en.md](docs/testing.en.md) for the full test matrix and real-game acceptance procedure.

## Agent integrations

Codex, OpenClaw, and MCP adapters are maintained separately:

[irregular-haguruma/inputd-agent-integrations](https://github.com/irregular-haguruma/inputd-agent-integrations)

## Documentation

- [Architecture](docs/architecture.en.md)
- [Dashboard](docs/dashboard.en.md)
- [Protocol](docs/protocol.en.md)
- [Safety](docs/safety.en.md)
- [Training data](docs/training-data.en.md)
- [OBS](docs/obs.en.md)
- [Testing](docs/testing.en.md)

## License

Licensed under the [Apache License 2.0](LICENSE).
