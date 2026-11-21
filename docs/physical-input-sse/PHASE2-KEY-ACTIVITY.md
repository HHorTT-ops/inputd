# Phase 2 — key_activity SSE

Branch: `feat/physical-input-sse-key-activity`  
Baseline: `4154605`

## Changes

- `GET /v1/physical-input/events` now also emits `event: key_activity`
- Payload fields only: `sequence`, `timestamp_us`, `source_class`, `raw_device_handle`, optional `edge` (`down`|`up`)
- Never includes `ChangedKey`, key names, held keys, or text
- Still does not Start Recording / write trajectories
- Tagged keyboard (ExecutorEventTag) excluded from stream; counted in `tagged_frames_seen`
- Mouse SSE behavior unchanged

## Source classes

Unchanged: `DEVICE_ATTRIBUTED` | `EXECUTOR_INJECTED` | `UNATTRIBUTED`
