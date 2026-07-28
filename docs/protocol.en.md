# Protocol v1

[简体中文](protocol.md) | [English](protocol.en.md) | [日本語](protocol.ja.md)

All JSON uses UTF-8 and durations use milliseconds. `timestamp_us`, `deadline_us`, and response `monotonic_us` use Windows monotonic uptime microseconds (`Environment.TickCount64 * 1000`), allowing clients on the same machine to compare deadlines without wall-clock synchronization.

## Bootstrap and session tokens

Creating a session requires `X-Inputd-Access-Token`. The default token is stored at:

```text
%LOCALAPPDATA%\inputd\access.token
```

Each successful session creation returns an independent `session_id` and `capability_token`. Send the capability token as `X-Inputd-Token` or `Authorization: Bearer` on later HTTP requests.

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

Session creation can use `bind_to_foreground: true`, an explicit `target`, or `target_process_id`. PID binding resolves the main HWND and freezes HWND, PID, executable path, and process start time. `/focus` activates only the bound HWND; Arm and every input action still verify foreground ownership.

`snapshot` returns `image/png`. Frame ID, virtual-desktop origin, dimensions, and capture time are returned in `X-Inputd-*` headers. Screenshot-relative adapters must translate `(x,y)` with the returned origin and reject stale frame IDs.

Raw Input recording is session-scoped and uses the same JSONL sink as Command/Realtime actions. `include_executor_events` defaults to false. When enabled, diagnostic hooks store only inputd-tagged injection as `*_hook_injected`; those events are never presented as physical Raw Input.

Command example:

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

Actions are `mouse_move_relative`, `mouse_move_absolute`, `mouse_button`, `mouse_wheel`, `key`, `text`, `wait`, `release_all`, and `barrier`. Physical keys use DOM `KeyboardEvent.code`-style names mapped to Windows scan codes. Text separately uses Unicode `KEYEVENTF_UNICODE`.

## Realtime WebSocket

Connect to:

```text
ws://127.0.0.1:17841/v1/realtime?session_id={id}
Authorization: Bearer {capability_token}
```

Continuously send the current desired input state:

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

The state machine diffs held sets against the previous executed frame. Response status is `executed`, `stale`, `expired`, `superseded`, or `rejected`. Closing the WebSocket disarms the session and releases every held input.

JSON Schemas are in the repository's `schemas` directory.
