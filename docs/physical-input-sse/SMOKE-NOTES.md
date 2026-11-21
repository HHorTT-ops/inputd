# Physical Input SSE smoke

## Prior smoke (pre-attribution)

Automated Windows smoke against `GET /v1/physical-input/events`.

- Stimulus: untagged `user32.mouse_event` move / click / wheel (`ExtraInformation = 0`)
- Hand-operated physical mouse: NOT_RUN
- Recording Start: not called
- Result: HTTP 200, 63 mouse events, 3 heartbeats, 0 faults
- Sample timestamps and dx/dy are in `smoke-20261121-145524.json`

**Interpretation after attribution follow-up:** those 63 events must be treated as `UNATTRIBUTED` (or re-run with `source_class` in payload). They are **not** hand-operated physical-mouse PASS evidence.

## Review follow-up

See `REVIEW-FOLLOWUP-20261121.md` and `INPUT-SOURCE-ATTRIBUTION.md`.

Attribution script: `scripts/run-physical-input-attribution-tests.ps1`  
Modes: `hand` | `untagged-mouse-event` | `tagged-sendinput` | `listen`

Hand-operated physical mouse move / click / wheel: **NOT_RUN** (requires Founder).
