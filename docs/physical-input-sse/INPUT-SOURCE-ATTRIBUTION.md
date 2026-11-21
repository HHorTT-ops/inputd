# Mouse input source attribution (Windows)

Status: Phase 1 final naming — `DEVICE_ATTRIBUTED` / `EXECUTOR_INJECTED` / `UNATTRIBUTED`.  
This document records what Windows actually exposes, what inputd classifies, and what remains undecidable.

## Goal classes

| Class | Meaning |
| --- | --- |
| `DEVICE_ATTRIBUTED` | Raw Input associated the packet with a non-null `RAWINPUTHEADER.hDevice` (registered mouse/HID device handle). **Not** proof of human hand operation. |
| `EXECUTOR_INJECTED` | inputd's own `SendInput` tagged with `ExecutorEventTag` (`0x494E505554440001`). |
| `UNATTRIBUTED` | Mouse Raw Input without a reliable non-zero device handle (includes untagged software injection). |

> Renamed from the earlier working name `HUMAN_PHYSICAL`. Non-zero `hDevice` must never be documented or consumed as “proven human physical input”.

## Signals examined

### 1. Raw Input `hDevice`

- Present on every `WM_INPUT` / `GetRawInputData` header.
- Physical USB/Bluetooth mice normally yield a non-null handle.
- `SendInput` / `mouse_event` injection commonly yields `hDevice == NULL`.
- **Limit:** virtual HID drivers can also present a non-null `hDevice`. Non-null ⇒ device-attributed only.

### 2. `WM_INPUT` itself

- No separate “injected” bit beyond header/device and payload `ExtraInformation`.

### 3. `WH_MOUSE_LL` / `LLMHF_INJECTED`

- Exists on low-level hooks, but the production SSE path does **not** install LL hooks.
- Not used for SSE attribution in this phase.

### 4. `SendInput` `dwExtraInfo` / Raw `ulExtraInformation`

- inputd tags with `ExecutorEventTag` when enabled.
- Tagged frames are excluded from the SSE mouse stream; `heartbeat.tagged_frames_seen` counts them.
- **Do not** treat `ExtraInformation == 0` as human-physical.

### 5. Cross-process untagged injection

- Another process can inject without inputd's tag → typically `UNATTRIBUTED`.
- Must not be used as authoritative `HUMAN_CONTROL` evidence.

## Classification implemented in SSE

```
if InjectedTagged / ExecutorEventTag → EXECUTOR_INJECTED (not emitted as event:mouse; diagnostic counter only)
else if hDevice != 0                 → DEVICE_ATTRIBUTED (emitted; device-handle attribution only)
else                                 → UNATTRIBUTED (emitted; not authoritative human evidence)
```

SSE `event:mouse` payload fields:

- `source_class`: `DEVICE_ATTRIBUTED` | `UNATTRIBUTED`
- `raw_device_handle`: hex string from `hDevice` (e.g. `0x0`, `0x1A2B`)

## Honest limitations

1. Windows does **not** certify “a human hand moved this physical mouse” per packet.
2. `DEVICE_ATTRIBUTED` ≠ courtroom-grade human proof; virtual HID can appear here.
3. Untagged software injection is `UNATTRIBUTED`.
4. Agent-Company-OS must **not** map this stream 1:1 to ownership `HUMAN_CONTROL` without additional policy.

## Evidence expectations

| Stimulus | Expected `source_class` | Notes |
| --- | --- | --- |
| Hand-operated physical mouse | expected mostly `DEVICE_ATTRIBUTED` if OS reports non-zero `hDevice` | **Observed on Founder host 2026-11-21:** 255 events all `UNATTRIBUTED` / `hDevice=0x0` — device-handle rule insufficient on this machine |
| inputd tagged `SendInput` | no `event:mouse`; `tagged_frames_seen` ↑ | `EXECUTOR_INJECTED` |
| Untagged `mouse_event` | `UNATTRIBUTED` | ExtraInfo=0 |
