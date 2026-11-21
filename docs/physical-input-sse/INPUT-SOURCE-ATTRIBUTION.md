# Mouse input source attribution (Windows)

Status: researched for `feat/physical-input-sse` (Issue #3 review follow-up).  
This document records what Windows actually exposes, what inputd can classify, and what remains undecidable.

## Goal classes

| Class | Meaning |
| --- | --- |
| `HUMAN_PHYSICAL` | Raw Input associated the packet with a non-null `RAWINPUTHEADER.hDevice` (registered mouse/HID device). |
| `EXECUTOR_INJECTED` | inputd's own `SendInput` tagged with `ExecutorEventTag` (`0x494E505554440001`). |
| `UNATTRIBUTED` | Mouse Raw Input without a reliable non-zero device handle (includes untagged software injection). |

## Signals examined

### 1. Raw Input `hDevice`

- Present on every `WM_INPUT` / `GetRawInputData` header.
- Physical USB/Bluetooth mice normally yield a non-null handle that can be queried via `GetRawInputDeviceInfo`.
- `SendInput` / `mouse_event` injection commonly yields `hDevice == NULL`.
- **Limit:** virtual HID drivers and some filter drivers can also present a non-null `hDevice`. Therefore non-null means “device-attributed”, not “proven human hand”.

### 2. `WM_INPUT` itself

- Carries no separate “injected” bit beyond header/device and the mouse/keyboard payload `ExtraInformation`.
- Cannot alone prove human vs software when `hDevice` is non-null (virtual devices) or when ExtraInfo is forged.

### 3. `WH_MOUSE_LL` / `LLMHF_INJECTED`

- Low-level mouse hooks expose `LLMHF_INJECTED` / `LLMHF_LOWER_IL_INJECTED` on `MSLLHOOKSTRUCT.flags`.
- inputd's production SSE path does **not** install LL hooks (hooks are opt-in diagnostics for tagged executor replay only).
- Even with LL hooks: injected flag detects many synthetic paths, but does not prove the complementary set is a physical human mouse; combining LL hooks with Raw Input also reintroduces known hook-fragility risks (swallowed input / ordering).
- **Not used** for SSE attribution in this phase.

### 4. `SendInput` `dwExtraInfo` / Raw `ulExtraInformation`

- inputd sets `ExecutorEventTag` when `TagInjectedEvents` is enabled (default for the executor backend).
- Tagged frames raise `TaggedRawFrameObserved` and are **excluded** from the SSE mouse stream; `heartbeat.tagged_frames_seen` counts them.
- Untagged injection (`ExtraInformation == 0`), including `user32.mouse_event`, is **indistinguishable from many physical packets by ExtraInfo alone**.
- **Do not** treat `ExtraInformation == 0` as human-physical.

### 5. Cross-process untagged injection

- Another process can call `SendInput`/`mouse_event` without inputd's tag.
- Those packets typically appear as `Device == "mouse"` with `hDevice == 0` → `UNATTRIBUTED`.
- They must not be used as authoritative `HUMAN_CONTROL` evidence.

## Classification implemented in SSE

```
if InjectedTagged / ExecutorEventTag → EXECUTOR_INJECTED (not emitted as event:mouse; diagnostic counter only)
else if hDevice != 0                 → HUMAN_PHYSICAL (emitted; best-effort device attribution)
else                                 → UNATTRIBUTED (emitted; not authoritative human evidence)
```

SSE `event:mouse` payload fields:

- `source_class`: `HUMAN_PHYSICAL` | `UNATTRIBUTED`
- `raw_device_handle`: hex string from `hDevice` (e.g. `0x0`, `0x1A2B`)

## Honest limitations (do not claim otherwise)

1. Windows does **not** provide a public API that certifies “a human hand moved this physical mouse” for every packet.
2. `HUMAN_PHYSICAL` here means **device-attributed Raw Input**, not courtroom-grade human proof.
3. Virtual HID can be mis-labeled `HUMAN_PHYSICAL`.
4. Untagged software injection is `UNATTRIBUTED`, same bucket as any other zero-handle mouse Raw Input.
5. Therefore Agent-Company-OS must **not** map this stream 1:1 to ownership `HUMAN_CONTROL` without additional policy (and must never treat `UNATTRIBUTED` as human).

## Evidence expectations

| Stimulus | Expected `source_class` | Notes |
| --- | --- | --- |
| Hand-operated physical mouse | mostly `HUMAN_PHYSICAL` | Requires Founder to operate real hardware |
| inputd tagged `SendInput` | no `event:mouse`; `tagged_frames_seen` increases | `EXECUTOR_INJECTED` |
| Untagged `mouse_event` | `UNATTRIBUTED` | Confirmed previously; ExtraInfo=0 |
