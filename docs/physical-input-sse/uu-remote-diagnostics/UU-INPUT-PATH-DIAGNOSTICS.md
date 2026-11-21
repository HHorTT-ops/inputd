# UU Remote Input Path Diagnostics

Task: `INPUTD-UU-REMOTE-INPUT-DIAGNOSTICS-001` + review fix `INPUTD-PHASE1.1-REVIEW-FIX-001`  
Branch: `feat/physical-input-sse`  
Prior tip: `675cc5c` · This fix tip: *(see git HEAD after push)*  
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

### After review fix

| Capture | Result |
| --- | --- |
| `*-review-fix-env-smoke` | Session env with SM_REMOTECONTROL + candidate_class + HID info |
| `*-test-e-untagged-mouse-event-reviewfix` | PASS — raw=24/hook=24; correlation stimulus-scoped; many AMBIGUOUS (honest ties) |

### Still waiting on Founder

Real UU interactive A–D and console physical G — **not fabricated**.

## Ownership note

Any non-`EXECUTOR_INJECTED` external activity remains a conservative ExternalInputActivity pause candidate — not a human claim.

## Founder UU re-run

```powershell
cd <inputd>
dotnet build InputExecutor.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-uu-remote-input-diagnostics.ps1 -ControllerPlatform UNKNOWN
```
