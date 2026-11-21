# Review follow-up — inputd PR #1

Date: 2026-11-21  
Branch: `feat/physical-input-sse`  
Responds to independent review on https://github.com/HHorTT-ops/inputd/pull/1

## 1. SSE connect-time race

**Before:** `Flush` → `Subscribe` (frames arriving between flush and subscribe were lost).  
**After:** authenticate → `Subscribe` (`await using`) → SSE headers → `Flush` → initial heartbeat → read loop.

Covered by `PhysicalMouseSse_EventsPublishedDuringConnectAreNotLost`.

## 2. Source attribution (not physical-human-only)

| Class | Rule | SSE behavior |
| --- | --- | --- |
| `EXECUTOR_INJECTED` | `InjectedTagged` / `ExecutorEventTag` | Not emitted as `event: mouse`; `heartbeat.tagged_frames_seen` only |
| `HUMAN_PHYSICAL` | `Device=="mouse"` and non-zero `RAWINPUTHEADER.hDevice` | Emitted with `source_class` + `raw_device_handle` |
| `UNATTRIBUTED` | mouse Raw Input with zero/missing device handle (includes untagged `mouse_event` / `SendInput`) | Emitted; **must not** be treated as human evidence |

Research notes: `docs/physical-input-sse/INPUT-SOURCE-ATTRIBUTION.md`.

Honest limits: non-zero `hDevice` is device-attributed Raw Input, not proof of a human hand (virtual HID possible). `ExtraInformation == 0` is never used as human proof.

## 3. Overflow fault on the real Handler

`PhysicalMouseSseHandler_OverflowWritesFault_ViaRealChannelBackpressure` calls `PhysicalMouseSseEndpoint.HandleAsync` with a gated response body and hub capacity 1. It blocks `WriteAsync` after the heartbeat so `Broadcast` hits the real `TryWrite` failure path, then asserts `event: fault` / `OVERFLOW` in the written SSE body. This does not rely on TestServer TCP backpressure.

Hub-level overflow without silent drop remains covered by `Hub_OverflowFaultsSubscriber_WithoutSilentDrop`.

## 4. Automated tests (this follow-up)

```
dotnet build InputExecutor.sln -c Release
dotnet test InputExecutor.sln -c Release
```

Expected PhysicalMouseSse coverage includes: auth, hub filter/tag exclusion, source_class classification, connect no-loss, client disconnect release, HTTP/handler overflow fault, tagged exclusion on stream.

## 5. Windows stimulus scripts

`scripts/run-physical-input-attribution-tests.ps1`

| Mode | Purpose |
| --- | --- |
| `hand` | Founder-operated physical mouse (move/click/wheel) — **not substituted by injection** |
| `untagged-mouse-event` | Expect `UNATTRIBUTED` |
| `tagged-sendinput` | Expect no `event: mouse`; `tagged_frames_seen` increases |

### Windows evidence captured this follow-up

| Mode | Result |
| --- | --- |
| `untagged-mouse-event` | PASS — 28× `UNATTRIBUTED`, 0× `HUMAN_PHYSICAL` (`attribution-untagged-mouse-event-20261121-155410.json`) |
| `tagged-sendinput` | PASS — 0 mouse events, `tagged_frames_seen=24` (`attribution-tagged-sendinput-20261121-155426.json`) |
| `hand` move/click/wheel | **NOT_RUN** — requires Founder on real hardware |

Hand-operated evidence remains **NOT_RUN** until Founder executes `hand` mode on real hardware.

## 6. Out of scope (unchanged)

- No Agent-Company-OS Python Input Ownership changes
- No Qwen / Tool Grant / Harness / CUE-0.8 changes
- No PR merge
- Upstream remote retained
