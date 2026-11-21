# Physical Input SSE smoke

Automated Windows smoke against `GET /v1/physical-input/events`.

- Stimulus: untagged `user32.mouse_event` move / click / wheel (`ExtraInformation = 0`)
- Hand-operated physical mouse: NOT_RUN
- Recording Start: not called
- Result: HTTP 200, 63 mouse events, 3 heartbeats, 0 faults
- Sample timestamps and dx/dy are in `smoke-20261121-145524.json`
