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

Hand-operated physical mouse move / click / wheel: **NOT_RUN** until Founder runs `hand` mode on real hardware. Injection must not substitute.
