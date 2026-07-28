# Protocol v1

All JSON uses UTF-8. Durations are milliseconds. `timestamp_us`, `deadline_us`, and response `monotonic_us` use Windows monotonic uptime microseconds (`Environment.TickCount64 * 1000`), so clients on the same machine can compare deadlines without wall-clock synchronization.

## Bootstrap and session tokens

Creating a session requires `X-Inputd-Access-Token`. By default this token is read from `%LOCALAPPDATA%\inputd\access.token`. Every successful create response returns a separate `session_id` and `capability_token`. Send that capability as `X-Inputd-Token` or `Authorization: Bearer` for subsequent HTTP calls.

## Command API

```text
POST   /v1/sessions
GET    /v1/sessions/{id}
POST   /v1/sessions/{id}/arm
POST   /v1/sessions/{id}/focus
POST   /v1/sessions/{id}/disarm
POST   /v1/sessions/{id}/commands
POST   /v1/sessions/{id}/cancel
POST   /v1/sessions/{id}/release-all
GET    /v1/sessions/{id}/snapshot
POST   /v1/sessions/{id}/recording/start
GET    /v1/sessions/{id}/recording
POST   /v1/sessions/{id}/recording/stop
DELETE /v1/sessions/{id}
```

Session creation may use `bind_to_foreground: true`, an explicit `target`, or `target_process_id`. PID binding resolves the process main HWND and freezes HWND/PID/path/start-time identity. `/focus` explicitly activates only that bound HWND; Arm and every injected action still perform the normal foreground check.

`snapshot` returns `image/png`. Frame ID, virtual-desktop origin, size, and capture time are returned as `X-Inputd-*` headers. Screenshot-relative adapters must translate `(x,y)` using the returned origin and reject stale frame IDs.

Raw Input recording is session-scoped and persisted in the same JSONL trajectory sink as command/realtime actions. `include_executor_events` is false by default. When true, diagnostic low-level hooks persist inputd-tagged injection separately as `*_hook_injected`; these records are never represented as physical Raw Input.

Example command:

```json
{
  "version": "1.0",
  "sequence": 102,
  "deadline_us": 184521920000,
  "exclusive": true,
  "actions": [
    { "type": "mouse_move_relative", "dx": 120, "dy": -30, "duration_ms": 80 },
    { "type": "mouse_button", "button": "left", "action": "click" },
    { "type": "key", "code": "Enter", "action": "press" },
    { "type": "text", "text": "Hello" }
  ]
}
```

Supported action types are `mouse_move_relative`, `mouse_move_absolute`, `mouse_button`, `mouse_wheel`, `key`, `text`, `wait`, `release_all`, and `barrier`. Physical keys use DOM `KeyboardEvent.code`-style names and are mapped to Windows scan codes. Text uses Unicode `KEYEVENTF_UNICODE` and is separate from physical game keys.

## Realtime WebSocket

Connect to:

```text
ws://127.0.0.1:17841/v1/realtime?session_id={id}
Authorization: Bearer {capability_token}
```

Send state frames continuously:

```json
{
  "version": "1.0",
  "sequence": 8841,
  "timestamp_us": 184521904214,
  "deadline_us": 184521920000,
  "keyboard": { "held": ["KeyW", "ShiftLeft"] },
  "mouse": {
    "dx": 12,
    "dy": -4,
    "held_buttons": ["right"],
    "wheel_x": 0,
    "wheel_y": 0
  }
}
```

The state machine diffs held sets against the previous executed frame. Responses have `status` equal to `executed`, `stale`, `expired`, `superseded`, or `rejected`. Closing the WebSocket disarms the session and releases all held input.

Schemas are in the repository `schemas` directory.
