# UU Remote Input Path Diagnostics

Tasks: `INPUTD-UU-REMOTE-INPUT-DIAGNOSTICS-001` · `INPUTD-PHASE1.1-REVIEW-FIX-001` · **`INPUTD-PHASE1.2-DPI-COORDINATE-ALIGNMENT-001`**  
Branch: `feat/physical-input-sse`  
Baseline before Phase 1.2: `3f3b954`  
Controller platform: **UNKNOWN** (not guessed)

## Engineering check

| Item | Status |
| --- | --- |
| Existing evidence | Preserved (prior folders not overwritten) |
| Source classes | Unchanged: `EXECUTOR_INJECTED` / `DEVICE_ATTRIBUTED` / `UNATTRIBUTED` |
| ACOS production | Not modified |
| Process DPI mode | **Not changed** — alignment is explicit / measured |
| Fake UU results | **None** |
| Hardcoded 2× scale | **Forbidden** |

## Phase 1.2 — DPI / coordinate alignment

### PART 1 — Diagnostics (`coordinate-diagnostics.json`)

Each capture (and `--mode coordinate-snapshot`) records:

- Process DPI awareness (`GetProcessDpiAwareness`)
- Thread DPI awareness context
- `GetDpiForSystem`
- `GetDpiForMonitor` per monitor (when available)
- `GetSystemMetrics` vs `GetSystemMetricsForDpi`
- `EnumDisplayMonitors` + `GetMonitorInfo` bounds
- Virtual desktop range
- `GetCursorPos` vs `GetPhysicalCursorPos`
- Documented coordinate spaces for Raw absolute, metrics mapping, and `MSLLHOOKSTRUCT.pt`

### PART 2 — Absolute mapping

- Preserves `lLastX` / `lLastY` / `usFlags`
- Distinguishes primary vs virtual desktop (`MOUSE_VIRTUAL_DESKTOP`)
- Emits **before** (`screen_x_metrics`) and **after** (`screen_x` hook-aligned) coordinates
- Aligns via `CoordinateSpaceConverter` (LogicalToPhysical → cursor ratio → system DPI/96 → metrics-for-dpi). **Never hardcodes 2.0**
- Supports negative virtual origins

### PART 3 — Correlation

- Absolute (`usFlags` ABSOLUTE) with dx=dy=0 → kind **`move`** (no longer `other`)
- ButtonFlags still classify button/wheel kinds first
- **`other` is never kind-compatible** with any hook message
- Position match uses hook-aligned coords (optional aligner for metrics-only events)
- One-to-one; ties → `AMBIGUOUS`; no forced matches

### PART 4 — Manual test flow

Script prompts and `stimulus-verdict.json` now enforce:

| Test | Requirement | FAIL if |
| --- | --- | --- |
| A | Move only | wheel/click present, or ZERO |
| B-left / B-right | Separate left then right | missing down+up |
| C-wheel / C-hwheel | Separate vertical then horizontal | missing message |
| D | Hold left, move ≥200px, release | no MOVE between down/up, or travel short |
| G | Console physical, or explicit NOT_RUN | ZERO without NOT_RUN |

### PART 5 — Device compare

`--mode device-snapshot` / `device-compare` diffs handle sets, names, usage, VID/PID.  
`VHF_DEVICE_CANDIDATE` ≠ UU ownership.

## Automated tests (Phase 1.2)

```
dotnet test InputExecutor.sln -c Release
  InputExecutor.IntegrationTests: 14 passed
  InputExecutor.Tests:            70 passed / 13 skipped
```

(Prior PR text cited 57/13 before Phase 1.2 added correlation/DPI/verdict unit tests.)

## Captures (honest)

### Founder UU re-capture @ `3f3b954` (pre–Phase 1.2)

| Capture | Verdict | Notes |
| --- | --- | --- |
| A move | Mislabel | All wheel — not move-only |
| B clicks | Partial | Left only; no right |
| B2 right | ZERO_EVENTS | |
| C wheel | OBSERVED | wheel + hwheel mixed with moves |
| D drag | Not a drag | LBUTTON down/up same point; no MOVE between |
| G | NOT_RUN | |
| Correlation | **Not passed** | Absolute moves were `other`; coords ~2:1 vs hook (e.g. raw 852,436 vs hook 1706,873) |

Stable UU signature when events present: `hDevice=0` → `UNATTRIBUTED`; absolute+virtual desktop moves; buttons/wheel relative; Hook `LLMHF_INJECTED`; no ExecutorTag.

### Phase 1.2 local evidence (engineering, not Founder UU retest)

| Capture | Result |
| --- | --- |
| `*-phase12-coordinate-env-coordinate-snapshot` | Process_DPI_Unaware; dpi_for_system=96; SM 1920×1080; cursor logical==physical; alignment **identity** on this host *right now* |
| `*-test-e-untagged-mouse-event-phase12` | raw=24/hook=24; matched=4 ambiguous=20 unmatched=0 |
| `*-test-f-tagged-sendinput-phase12` | raw=24/hook=24; ExecutorTag=24 |
| SUMMARY JSON | `SUMMARY-*.json` under this folder |

### Founder UU retest session 2026-11-21 ~21:40 (Phase 1.2 + empirical fix)

| Capture | Events | Stimulus verdict | Notes |
| --- | --- | --- | --- |
| Most A / B-left / B-right / C-v windows | 0 | FAIL ZERO / missing | UU not injecting in those windows — **not PASS** |
| `214316-test-c-uu-hwheel` | 421 | FAIL (no HWHEEL) | Contaminated: moves + **vertical** wheel×93 + left×3; useful UU stream |
| `214330-test-d-uu-drag-select` | 249 | FAIL (no buttons) | Moves only — not a drag |
| G | — | **NOT_RUN** | |
| Device before/after | 21=21 | — | **No** handle add/remove; VHF still candidate-only |

**Coordinate finding (critical):** DPI APIs reported **identity** (Process_DPI_Unaware, dpi=96, cursor logical==physical), but live UU absolute Raw vs Hook was still ~**2.00×** (e.g. raw screen 971,352 vs hook pt 1943,705).  

**Empirical alignment** (measured from Raw↔Hook pairs, not hardcoded): median scale ≈ **2.003** over 322 pairs on `214316`. After reprocess: unmatched_raw **0**; matched 130 / ambiguous 291 (dense absolute stream → honest ties). D reprocess: matched 162 / amb 87 / unmatched 0.

Stimulus PASS for clean A/B-right/C-h/D drag: **still not achieved** this session (operator timing / UU intermittent).

## Ownership note

Any non-`EXECUTOR_INJECTED` external activity remains a conservative ExternalInputActivity pause candidate — not a human claim. UU-shaped traffic stays **external injected**, not `DEVICE_ATTRIBUTED`.

## Founder retest

```powershell
cd <inputd>
dotnet build InputExecutor.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-uu-remote-input-diagnostics.ps1 -ControllerPlatform UNKNOWN
```

Follow on-screen prompts (device before/after, A move-only, B-left, B-right, C-v, C-h, D drag≥200px, G or NOT_RUN).  
Do not mark PASS for actions that did not occur.
