# Review follow-up — inputd PR #1 (Phase 1 final)

Date: 2026-11-21  
Branch: `feat/physical-input-sse`  
PR: https://github.com/HHorTT-ops/inputd/pull/1

## 1. SSE connect-time race

**Order:** authenticate → `Subscribe` (`await using`) → SSE headers → `Flush` → initial heartbeat → read loop.  
Test: `PhysicalMouseSse_EventsPublishedDuringConnectAreNotLost`.

## 2. Source attribution

| Class | Rule | SSE behavior |
| --- | --- | --- |
| `EXECUTOR_INJECTED` | `InjectedTagged` / `ExecutorEventTag` | Not emitted as `event: mouse`; `heartbeat.tagged_frames_seen` only |
| `DEVICE_ATTRIBUTED` | `Device=="mouse"` and non-zero `RAWINPUTHEADER.hDevice` | Emitted; **device-handle attribution only — not proof of human hand** |
| `UNATTRIBUTED` | zero/missing device handle (includes untagged `mouse_event` / `SendInput`) | Emitted; not human evidence |

Former working name `HUMAN_PHYSICAL` was renamed to `DEVICE_ATTRIBUTED` to prevent false certainty.

Research: `INPUT-SOURCE-ATTRIBUTION.md`.

## 3. Overflow fault — real Handler test

`PhysicalMouseSseHandler_OverflowWritesFault_ViaRealChannelBackpressure`:

1. Wait for initial heartbeat fully written (`event: heartbeat` + `tagged_frames_seen` + SSE frame terminator).
2. Block subsequent response `WriteAsync`.
3. Publish until hub capacity-1 `TryWrite` fails (real channel overflow).
4. Release writes; assert **both** `event: mouse` and `event: fault` / `OVERFLOW`.

Does not use TestServer TCP backpressure. Hub-level overflow still covered by `Hub_OverflowFaultsSubscriber_WithoutSilentDrop`.

## 4. Automated tests

```
dotnet build InputExecutor.sln -c Release
dotnet test InputExecutor.sln -c Release
```

## 5. Windows stimulus scripts

`scripts/run-physical-input-attribution-tests.ps1`

| Mode | Purpose |
| --- | --- |
| `hand` | Founder-operated physical mouse (move/click/wheel) — **injection must not substitute** |
| `untagged-mouse-event` | Expect `UNATTRIBUTED` |
| `tagged-sendinput` | Expect no `event: mouse`; `tagged_frames_seen` increases |

### Evidence

| Mode | Result |
| --- | --- |
| `untagged-mouse-event` | PASS — 24× `UNATTRIBUTED`, 0× `DEVICE_ATTRIBUTED` (`attribution-untagged-mouse-event-20261121-170714.json`) |
| `tagged-sendinput` | PASS — 0 mouse, `tagged_frames_seen=24` (`attribution-tagged-sendinput-20261121-170726.json`) |
| `hand` move/click/wheel | **RAN** — 255 mouse events, but **0× `DEVICE_ATTRIBUTED` / 255× `UNATTRIBUTED`** (`raw_device_handle=0x0`). Verdict: `EVENTS_SEEN_BUT_NOT_DEVICE_ATTRIBUTED` (`attribution-hand-move-click-wheel-20261121-175157.json`). Not a device-attribution PASS. |
| Automated suite log | `test-results-phase1-final.txt` — IntegrationTests 14 pass; Tests 43 pass / 13 skip |

### Open issue for next step (Founder / reviewer)

On this host, Founder-operated physical mouse Raw Input still reports `hDevice == 0`, so non-zero-handle ⇒ `DEVICE_ATTRIBUTED` does **not** separate hand input from untagged injection here. Next work should investigate why `RAWINPUTHEADER.hDevice` is null for local physical mouse (registration flags, RIDEV_INPUTSINK, absolute/virtual devices, remote desktop, driver stack) without lowering the acceptance bar.

## 6. Out of scope

- No Agent-Company-OS Python Input Ownership changes
- No Qwen / Tool Grant / Harness / CUE-0.8 changes
- No PR merge / no Phase 2
- Upstream remote retained
