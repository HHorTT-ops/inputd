# OBS paired operation recording

## Result

The OBS extension follows the official frontend recording lifecycle and creates
one operation trajectory for one completed OBS recording. The final JSONL and
its integrity manifest use the video's directory and stem:

```text
D:\recordings\boss-fight.mkv
D:\recordings\boss-fight.input.jsonl
D:\recordings\boss-fight.input.jsonl.manifest.json
```

The manifest links the absolute video and trajectory paths, records start/stop
UTC timestamps, event counts, pending/drop counts, termination reason, and the
trajectory SHA-256. A complete file requires zero dropped and zero pending
events.

## Why an OBS script plus a helper process

OBS officially supports Lua automation and frontend event callbacks. The Lua
extension handles only lifecycle notification. A self-contained
`net10.0-windows` helper owns the Raw Input window, unbounded ordered capture
channel, batched durable writer, and current-user-only named pipe.

OBS 30 can return only the configured output directory at
`RECORDING_STARTED`. Capture therefore begins immediately in
`%LOCALAPPDATA%\inputd\obs-spool`. At `RECORDING_STOPPED`,
`obs_frontend_get_last_recording()` supplies the actual output file and the
helper atomically moves the drained trajectory to the matching directory and
stem. If OBS or the helper exits first, the partial file and an incomplete
manifest remain recoverable in the spool.

Stop notification returns after the helper accepts finalization. Queue drain,
SHA-256 calculation, and file movement continue in the helper process, so a
large input trajectory cannot block the OBS frontend callback.

## Build and install

From PowerShell in the workspace:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

Then add this once through **OBS > Tools > Scripts > +**:

```text
%APPDATA%\obs-studio\scripts\inputd-obs\inputd-obs.lua
```

For an unattended installation, close OBS first and use:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1 -Register
```

The registration path creates a timestamped backup of the active scene
collection and atomically updates only `modules["scripts-tool"]`. Repeating it
does not create duplicate entries.

## Integrity and privilege

- Run OBS at the same or higher Windows integrity level as the game. The helper
  inherits OBS's token, which is required for Raw Input from an elevated game.
- The pipe uses `PipeOptions.CurrentUserOnly`; there is no listening TCP port.
- Production recording excludes inputd-tagged synthetic executor events.
- The final JSONL appears only after the recorder's exact enqueue watermark is
  drained and its sink is flushed.
- A filename collision is rejected and the partial data is preserved; an
  existing paired training file is never silently overwritten.

OBS frontend events and output-path functions are documented in the official
[OBS frontend API](https://docs.obsproject.com/reference-frontend-api). OBS Lua
extension loading is documented in the official
[scripting guide](https://docs.obsproject.com/scripting).
