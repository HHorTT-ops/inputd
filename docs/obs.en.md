# OBS Paired Operation Recording

[简体中文](obs.md) | [English](obs.en.md) | [日本語](obs.ja.md)

## Output

The OBS extension follows the official frontend recording lifecycle and creates one operation trajectory for every completed video. JSONL and its integrity manifest use the video's directory and stem:

```text
D:\recordings\boss-fight.mkv
D:\recordings\boss-fight.input.jsonl
D:\recordings\boss-fight.input.jsonl.manifest.json
```

The manifest records absolute video and trajectory paths, start/stop UTC times, event, pending and dropped counts, termination reason, and trajectory SHA-256. A file is complete only when both pending and dropped counts are zero.

## Why a script and a helper process

OBS officially supports Lua automation and frontend callbacks. The Lua extension reports lifecycle events only. An independent `net10.0-windows` bridge owns the Raw Input window, ordered capture channel, batched durable writer, and current-user-only named pipe.

OBS 30 may expose only the configured output directory at `RECORDING_STARTED`, so capture begins immediately in:

```text
%LOCALAPPDATA%\inputd\obs-spool
```

At `RECORDING_STOPPED`, `obs_frontend_get_last_recording()` provides the actual video path. The bridge drains the recorder and atomically moves the trajectory to the matching directory and stem. If OBS or the bridge exits first, `*.partial` and an incomplete manifest remain recoverable in the spool and are never presented as complete training data.

The Stop callback returns once the bridge accepts finalization. Queue drain, SHA-256, and file movement continue in the bridge, so a large trajectory cannot block the OBS frontend callback.

## Build and install

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

Add this once through **OBS > Tools > Scripts > +**:

```text
%APPDATA%\obs-studio\scripts\inputd-obs\inputd-obs.lua
```

For unattended registration, close OBS first:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1 -Register
```

Registration creates a timestamped backup of the current scene collection and atomically updates only `modules["scripts-tool"]`. Repeating it does not create duplicate entries.

## Integrity and privilege

- Run OBS at the same or a higher Windows integrity level than the game; the bridge inherits the OBS token.
- The pipe uses `PipeOptions.CurrentUserOnly` and exposes no TCP listener.
- Production recording excludes inputd-tagged synthetic executor events.
- Final JSONL appears only after the exact enqueue watermark drains and the sink is flushed.
- A filename collision fails explicitly and retains partial data; existing training data is never silently overwritten.

Official references: [OBS frontend API](https://docs.obsproject.com/reference-frontend-api) and [OBS scripting guide](https://docs.obsproject.com/scripting).
