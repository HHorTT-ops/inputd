# Fixed-Rate Training Data

[简体中文](training-data.md) | [English](training-data.en.md) | [日本語](training-data.ja.md)

inputd can resample event-level JSONL produced by the Dashboard or Raw Input recorder into fixed-rate training `ActionFrame` records. Built-in presets are 20, 40, and 60 Hz; any integer rate from 1 to 1000 Hz is supported.

## Generate from the Dashboard

1. Start `inputd-dashboard.exe`, complete a recording, and wait until it is marked fully saved.
2. Select **Generate training data**.
3. Choose the source `.jsonl`, a preset or custom rate, and an output path.
4. Generate. The tool writes both training JSONL and `<output>.manifest.json`.

Generation is disabled while recording so the source cannot change underneath the reader. **Allow source sequence gaps** is disabled by default and should remain disabled for production training.

## Generate from the command line

`inputctl.exe` works offline and does not require the inputd service:

```powershell
inputctl.exe resample .\episode.jsonl --hz 20
inputctl.exe resample .\episode.jsonl --hz 40
inputctl.exe resample .\episode.jsonl --hz 60
inputctl.exe resample .\episode.jsonl .\episode.37hz.jsonl --hz 37 --overwrite
```

Without an explicit output path, the file is named `<source>.<rate>hz.training.jsonl`. A sequence gap fails by default; use `--allow-gaps` only when incomplete historical data is deliberately accepted.

## No-event-loss semantics

Each frame uses a half-open interval `[window_start_us, window_end_us)`. An event exactly on a boundary enters the next frame. Integer rational boundaries prevent floating-point drift at rates such as 60 Hz.

- All in-frame `dx`, `dy`, `wheel_x`, and `wheel_y` values are summed independently with 64-bit integers.
- `keyboard.held` and `mouse.held_buttons` describe expected state at frame end; empty frames carry state forward.
- `pressed`, `released`, `pressed_buttons`, and `released_buttons` retain in-frame edges.
- `transitions` retain every ordered down/up edge with `offset_us` and `source_sequence`, so a short click cannot disappear when final held state is unchanged.
- `source_event_count`, `source_first_sequence`, and `source_last_sequence` make source coverage auditable per frame.
- By default, sequence must start at 1 and remain contiguous, timestamps must be monotonic, and the source must contain one session. Any violation aborts and deletes the temporary output.

Output is written to a temporary file and atomically replaces the destination only after completion. The manifest records source/output SHA-256, source events, output frames, empty frames, and sequence gaps.

## Frame shape

Each training JSONL line is one frame:

```json
{
  "version": "1.0",
  "episode_id": "inputd-dashboard-preview",
  "frames_per_second": 60,
  "frame_index": 42,
  "window_start_us": 5000700000,
  "window_end_us": 5000716666,
  "duration_us": 16666,
  "source_event_count": 3,
  "source_first_sequence": 87,
  "source_last_sequence": 89,
  "keyboard": {
    "held": ["KeyW"],
    "pressed": ["KeyW"],
    "released": []
  },
  "mouse": {
    "dx": 13,
    "dy": -4,
    "wheel_x": 0,
    "wheel_y": 0,
    "held_buttons": [],
    "pressed_buttons": ["left"],
    "released_buttons": ["left"]
  },
  "transitions": [
    { "offset_us": 1200, "source_sequence": 87, "device": "keyboard", "control": "KeyW", "action": "down" },
    { "offset_us": 4300, "source_sequence": 88, "device": "mouse", "control": "left", "action": "down" },
    { "offset_us": 6900, "source_sequence": 89, "device": "mouse", "control": "left", "action": "up" }
  ]
}
```

State-only policies can consume `held`, `held_buttons`, and mouse deltas. Models that must reproduce short clicks, repeated clicks, or exact edges should also consume `transitions`.

## Pre-training validation

At minimum verify:

1. manifest `sequence_gaps` is 0;
2. source and output SHA-256 values match the manifest;
3. `frame_index` is continuous and adjacent time windows meet exactly;
4. summed `source_event_count` equals manifest `source_events`;
5. summed mouse deltas equal the source trajectory.
