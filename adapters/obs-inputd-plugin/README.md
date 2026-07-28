# inputd OBS paired-input plugin

This OBS Lua extension listens to the official frontend recording lifecycle. On
`RECORDING_STARTED` it starts the bundled .NET 10 Raw Input bridge. On
`RECORDING_STOPPED` it drains the complete capture queue and writes beside the
video:

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

The OBS scripting thread only exchanges a small JSON message over a
current-user-only Windows named pipe. Raw Input capture and durable JSONL writes
run in the independent `inputd-obs-bridge.exe` process. OBS 30 reports only the
configured directory when recording first starts, so active data is written to
`%LOCALAPPDATA%\inputd\obs-spool` and atomically moved to the exact video stem
after OBS reports its final file. An interrupted session is retained as
`*.partial` with an incomplete manifest; it is never presented as a complete
training trajectory. Stop finalization is acknowledged before draining and
hashing, so a large input file cannot freeze the OBS frontend.

Install `inputd-obs.lua` through **Tools > Scripts > +**. Keep the default helper
path or select the bundled `bridge\inputd-obs-bridge.exe`. OBS and the helper
must run at the same or higher integrity level as an elevated game.
