# Physical Input SSE smoke / attribution notes

## Pre-attribution smoke (historical)

`smoke-20261121-145524.json` — untagged `user32.mouse_event` stimulus before `source_class` existed.

- Do **not** treat as hand-operated physical-mouse PASS.
- Under current semantics those frames would be `UNATTRIBUTED`.

## Current attribution (Phase 1 final)

Classes: `DEVICE_ATTRIBUTED` | `EXECUTOR_INJECTED` | `UNATTRIBUTED`

- `DEVICE_ATTRIBUTED` = non-zero Raw Input `hDevice` only (not proof of human hand).
- Script: `scripts/run-physical-input-attribution-tests.ps1`
- Modes: `hand` | `untagged-mouse-event` | `tagged-sendinput` | `listen`

Hand-operated physical mouse (Founder, 2026-11-21): **RAN** — see `attribution-hand-move-click-wheel-20261121-175157.json`.

- 255 mouse events, 0 faults
- All classified `UNATTRIBUTED` (`raw_device_handle=0x0`); **0** `DEVICE_ATTRIBUTED`
- Verdict: `EVENTS_SEEN_BUT_NOT_DEVICE_ATTRIBUTED` — not a device-attribution PASS
- UI script encoding fixed (Chinese via Base64 at runtime)
