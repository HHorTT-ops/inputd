# UU Remote Input Path Diagnostics

Task: `INPUTD-UU-REMOTE-INPUT-DIAGNOSTICS-001`  
Branch: `feat/physical-input-sse`  
Baseline: `1142ac0440f0f3a884682b260137fab6aaf0c8d4`  
Controller platform: **UNKNOWN** (not guessed)

## Engineering check

| Item | Status |
| --- | --- |
| Remote branch `origin/feat/physical-input-sse` | At baseline before this work |
| Local dirty state | Clean at start |
| Existing evidence | Preserved (no overwrite of prior attribution-*.json) |
| Source classes | Unchanged: `EXECUTOR_INJECTED` / `DEVICE_ATTRIBUTED` / `UNATTRIBUTED` |

## What was added

1. **Absolute Raw mouse fix** in `WindowsRawInputRecorder.ProcessMouse` (+ tagged path) via `MouseAbsoluteCoordinateTracker` (virtual desktop / multi-monitor, baseline reset on mode/range switch; click/wheel preserved).
2. **Independent dual-channel diagnostics** (`WindowsRemoteInputDiagnosticsSession`):
   - Raw Input JSONL (`raw-input.jsonl`) with hDevice, usFlags, lLastX/Y, ExtraInfo, buttons, ABSOLUTE/VIRTUAL_DESKTOP, derived dx/dy
   - WH_MOUSE_LL JSONL (`ll-hook.jsonl`) for **all** mouse messages with LLMHF_* and dwExtraInfo
   - Bounded channels; hook only enqueues; always `CallNextHookEx`
3. **Session environment** (`session-environment.json`): SM_REMOTESESSION, ProcessIdToSessionId, WTSClientProtocolType (when available), monitors, virtual desktop, Raw Input device list (redacted names)
4. Runner: `tests/InputExecutor.RemoteInputDiagnostics` + scripts under `scripts/run-uu-remote-input-diagnostics.ps1`

## Automated Release tests

```
dotnet build InputExecutor.sln -c Release  -> PASS
dotnet test InputExecutor.sln -c Release   -> IntegrationTests 14 pass; Tests 46 pass / 13 skip
```

Includes new `MouseAbsoluteCoordinateTrackerTests` (3).

## Captured runs

### TEST E — untagged `mouse_event` (PASS)

Folder: `20261121-190752-test-e-untagged-mouse-event`

| Metric | Value |
| --- | --- |
| RawEvents | 24 |
| HookEvents | 24 |
| RawAbsolute / RawRelative | 0 / 24 |
| RawHDeviceZero | 24 |
| HookInjected (LLMHF_INJECTED) | 24 |
| HookExtraInfoExecutorTag | 0 |

### TEST F — tagged `SendInput` (PASS / regression)

Folder: `20261121-190759-test-f-tagged-sendinput`

| Metric | Value |
| --- | --- |
| RawEvents | 24 |
| HookEvents | 24 |
| RawRelative | 24 |
| RawHDeviceZero | 24 |
| HookInjected | 24 |
| HookExtraInfoExecutorTag | **24** |

Tagged ExtraInfo is visible on the LL hook channel; Raw still shows relative packets with hDevice=0.

### TEST A–D — UU interactive

| Test | Result |
| --- | --- |
| A move (5s) | **ZERO_EVENTS** — `20261121-190812-test-a-uu-move` |
| B/C/D discrete | Not separately captured (agent window missed operator) |
| UU bundle 20s | **ZERO_EVENTS** — `20261121-190827-test-uu-interactive-bundle` |

Session environment files **were** written for these folders (SM_REMOTESESSION / device list available for review). No Raw/Hook mouse packets arrived during agent-driven capture windows.

### TEST G — console physical

**NOT_RUN** (cannot attest console-only physical mouse during this agent session).

## Cross-check answers (evidence-bounded)

| Question | Answer from this round |
| --- | --- |
| 1. Does UU mouse enter Raw Input? | **INCONCLUSIVE** this round (0 UU packets captured). Prior SSE hand run under UU showed Raw-derived SSE events exist in general. |
| 2. Does UU set LLMHF_INJECTED? | **INCONCLUSIVE** for UU. Untagged `mouse_event` **does** set LLMHF_INJECTED (TEST E). |
| 3. Is hDevice always 0? | For TEST E/F: **yes (24/24)**. For prior Founder hand SSE: also all `0x0`. |
| 4. Absolute vs relative? | TEST E/F: **relative**. Absolute path is implemented/tested in unit tests; UU absolute usage not observed this round. |
| 5. Clicks vs moves same path? | TEST E: move/click/wheel all appeared on **both** Raw and LL hook with injected flag. |
| 6. UU vs untagged mouse_event? | Cannot compare until UU packets are captured. |
| 7. Virtual HID present? | **Yes candidates** on this host: e.g. `VIRTUAL_DIGITIZER` flagged `looks_like_virtual_hid=true` in session-environment.json (TEST E). Also physical Logitech HID devices listed. |

### Session environment snapshot (TEST E)

- `sm_remote_session`: **false** (must not be read as “no UU”)
- `wts_client_protocol_note`: Console
- `monitor_count`: 1; virtual desktop 1920×1080
- Raw devices include `VIRTUAL_DIGITIZER` and Logitech HID entries

## Ownership implication (diagnostic only)

- Keep three source classes; do **not** rename `UNATTRIBUTED` to human.
- Recommend future Input Ownership treat **any non-`EXECUTOR_INJECTED` external activity** as a conservative **ExternalInputActivity** pause candidate for Qwen — without claiming human authorship.
- `SM_REMOTESESSION=false` must never be treated as “no remote control” under UU.

## How Founder re-runs UU interactive (when ready)

```powershell
cd <inputd>
dotnet build InputExecutor.sln -c Release
# Set controller if known: WINDOWS_CONTROLLER or MACOS_CONTROLLER; else UNKNOWN
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-uu-remote-input-diagnostics.ps1 -ControllerPlatform UNKNOWN
```

Or single capture while actively controlling via UU:

```powershell
dotnet run --project tests\InputExecutor.RemoteInputDiagnostics\InputExecutor.RemoteInputDiagnostics.csproj -c Release -- `
  --out docs\physical-input-sse\uu-remote-diagnostics --label test-a-uu-move --seconds 8 --controller UNKNOWN --stimulus none
```
