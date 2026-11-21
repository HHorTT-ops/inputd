# UU Remote Input Path Diagnostics

Task: `INPUTD-UU-REMOTE-INPUT-DIAGNOSTICS-001` + review fix `INPUTD-PHASE1.1-REVIEW-FIX-001`  
Branch: `feat/physical-input-sse`  
Binary tip at capture: `eec0b7a`  
Controller platform: **UNKNOWN** (not guessed)

## Engineering check

| Item | Status |
| --- | --- |
| Existing evidence | Preserved (prior folders not overwritten) |
| Source classes | Unchanged: `EXECUTOR_INJECTED` / `DEVICE_ATTRIBUTED` / `UNATTRIBUTED` |
| ACOS production | Not modified |
| Fake UU results | **None** |

## Phase 1.1 review fixes

1. **Absolute coords**: map with `(width-1)` / `(height-1)`; negative virtual origin; unit tests for primary edges, virtual four corners, 65535 clamp.
2. **Channel drops**: `FullMode.Wait` + non-blocking `TryWrite` via `DiagnosticChannel<T>`; overflow unit test asserts exact dropped count.
3. **Hook callback**: copies `HookSample` POD only; JSON + injected counters on background writer; always `CallNextHookEx`.
4. **Correlation**: one-to-one by time + kind + position; ties → `AMBIGUOUS`; stimulus-scoped labels (E/F must not be read as UU).
5. **Device candidates**: `VHF_DEVICE_CANDIDATE` / `VIRTUAL_HID_CANDIDATE` / `UNKNOWN` (+ `HID_DEVICE_SYSTEM_VHF`); RIDI_DEVICEINFO usage/VID/PID when available; never assert UU from names.
6. **Remote indicators**: `SM_REMOTECONTROL` + optional `WTSIsRemoteSession` probe (unavailable on this host); caveats retained.

## Automated Release tests (this fix)

```
dotnet build InputExecutor.sln -c Release  -> PASS
dotnet test InputExecutor.sln -c Release   -> IntegrationTests 14; Tests 57 pass / 13 skip
```

## Captures (honest)

### Prior (675cc5c era) — still valid baselines

| Test | Result |
| --- | --- |
| E untagged mouse_event | PASS — relative, hDevice=0, LLMHF_INJECTED |
| F tagged SendInput | PASS — ExecutorTag on hook |
| A / UU bundle | ZERO_EVENTS |
| B/C/D / G | NOT_RUN |

### After review fix (pre–Founder UU)

| Capture | Result |
| --- | --- |
| `*-review-fix-env-smoke` | Session env with SM_REMOTECONTROL + candidate_class + HID info |
| `*-test-e-untagged-mouse-event-reviewfix` | PASS — raw=24/hook=24; correlation stimulus-scoped; many AMBIGUOUS (honest ties) |

### Founder UU re-capture (2026-11-21, tip `eec0b7a`)

Session (from `20261121-204346-test-b-uu-clicks/session-environment.json`): Console; `SM_REMOTESESSION`/`SM_REMOTECONTROL` false; `WTSIsRemoteSession` unavailable; virtual screen 1920×1080; device list includes `VIRTUAL_DIGITIZER` (`VIRTUAL_HID_CANDIDATE`) and `HID_DEVICE_SYSTEM_VHF*` (`VHF_DEVICE_CANDIDATE`) — **candidates only, not UU ownership**.

| Capture folder | Verdict | Notes |
| --- | --- | --- |
| `20261121-204149-test-a-uu-move` | OBSERVED (mislabel) | raw=37/hook=37; **all** `WM_MOUSEWHEEL` / relative; hDevice=`0x0`; Injected=37; ExecutorTag=0 |
| `20261121-204206-test-b-uu-clicks` | ZERO_EVENTS | idle window |
| `20261121-204226-test-b-uu-clicks` | ZERO_EVENTS | idle window |
| `20261121-204252-test-bcd-uu-clicks-wheel-drag` | ZERO_EVENTS | idle window |
| `20261121-204323-test-uu-connectivity-probe` | OBSERVED | raw=517/hook=517; abs=453 rel=64; LBUTTON×2; wheel×60; all hDevice0 + Injected |
| `20261121-204346-test-b-uu-clicks` | OBSERVED | raw=707/hook=707; abs=652 rel=55; LBUTTON down/up×1; wheel×51; HWHEEL×2; **no RBUTTON** |
| `20261121-204402-test-c-uu-wheel` | OBSERVED | raw=291/hook=291; wheel×22 + HWHEEL×1 + abs moves |
| `20261121-204418-test-d-uu-drag-select` | OBSERVED (click-like) | raw=109/hook=109; abs moves + LBUTTON down/up at **same** pt (1651,810) — looks like click, not drag path |
| `20261121-204530-test-b2-uu-right-clicks` | ZERO_EVENTS | dedicated right-click window idle |

#### Stable UU dual-channel signature (when events present)

- Raw: `hDevice` always `0x0` → SSE class stays **`UNATTRIBUTED`** (not `DEVICE_ATTRIBUTED`)
- Moves often **absolute + virtual desktop** (`usFlags=3`, `mouse_move_absolute=true`, mapped `screen_x/y`)
- Buttons / wheel often **relative** packets (`usButtonFlags` 1/2/1024/2048) with hook `LLMHF_INJECTED=true`
- Hook: `dwExtraInfo` ExecutorEventTag **never** set on these UU windows
- Drops: RawDropped=0 / HookDropped=0 on all OBSERVED windows
- Correlation: many absolute moves report as `UNMATCHED_RAW` / `AMBIGUOUS` (stimulus-honest; E/F must not be read as UU)

#### Still open

| Item | Status |
| --- | --- |
| UU right-click (RBUTTON) | **NOT_OBSERVED** this round |
| Console physical G | **NOT_RUN** |
| Controller platform | Still **UNKNOWN** |

## Ownership note

Any non-`EXECUTOR_INJECTED` external activity remains a conservative ExternalInputActivity pause candidate — not a human claim. UU-shaped traffic (hDevice=0 + Injected, no ExecutorTag) is **external injected**, not device-attributed human.

## Founder UU re-run

```powershell
cd <inputd>
dotnet build InputExecutor.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-uu-remote-input-diagnostics.ps1 -ControllerPlatform UNKNOWN
```
