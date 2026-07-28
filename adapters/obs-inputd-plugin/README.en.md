# inputd OBS Paired-Input Plugin

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md)

This OBS Lua extension follows the official frontend recording lifecycle:

- `RECORDING_STARTED`: starts the published .NET 10 Raw Input bridge.
- `RECORDING_STOPPED`: drains the complete capture queue and writes input files beside the video.

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

The OBS scripting thread exchanges only small JSON messages over a current-user-only Windows named pipe. Raw Input capture and durable JSONL writes run in the independent `inputd-obs-bridge.exe` process.

OBS 30 may report only the configured directory when recording starts, so active data first goes to:

```text
%LOCALAPPDATA%\inputd\obs-spool
```

After OBS reports the final video path, the bridge atomically moves the trajectory to the exact matching stem. Interrupted sessions remain `*.partial` with incomplete manifests and are never presented as complete training trajectories. Stop finalization is acknowledged before drain and hashing continue in the bridge, so a large input file cannot freeze the OBS frontend.

Install `inputd-obs.lua` through **Tools > Scripts > +**. Keep the default helper path or select the published `bridge\inputd-obs-bridge.exe`. OBS and the bridge must run at the same or a higher integrity level when recording an elevated game.

See [`docs/obs.en.md`](../../docs/obs.en.md) for complete build, installation, and integrity guidance.
