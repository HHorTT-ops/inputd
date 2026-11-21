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

## REVIEW-02 / REVIEW-03 observation notes (ACOS consumer)

- `tagged_frames_seen` increments only via `TaggedRawFrameObserved` (Raw Input ExtraInfo == ExecutorEventTag low 32 bits).
- Injectors that use `SetCursorPos` or untagged `mouse_event`/`keybd_event` will **not** grow the counter and may appear as external activity.
- ACOS Phase 2 retargets Qwen PyAutoGUI paths to tagged `SendInput` for move/click/wheel/key so runtime verification is possible.
- REVIEW-03: ACOS requires **independent** tagged growth for mouse_move, mouse_click, mouse_wheel, and keyboard before opening QWEN; InputD restart invalidates prior runtime verify.
- Mouse SSE payload includes `held_buttons` / `dx` / `dy` for handoff button correlation on the consumer.
- No Virtual HID. No silent PASS when observation is impossible — consumers must report `NOT_RUN` / FAIL honestly.
- This branch's SSE contract is unchanged; ACOS fail-closed readiness + generation cancel live in Agent-Company-OS.
