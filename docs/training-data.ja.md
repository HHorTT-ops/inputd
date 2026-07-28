# 固定レート学習データ

[简体中文](training-data.md) | [English](training-data.en.md) | [日本語](training-data.ja.md)

inputd は Dashboard または Raw Input 記録器が生成したイベント単位 JSONL を、固定レートの学習用 `ActionFrame` へ再サンプリングできます。既定プリセットは 20、40、60 Hz で、1～1000 Hz の任意整数レートにも対応します。

## Dashboard から生成

1. `inputd-dashboard.exe` を起動し、記録を完了して「完全保存」になるまで待ちます。
2. **学習データ生成**を選びます。
3. 元 `.jsonl`、プリセットまたは任意レート、出力先を指定します。
4. 生成すると学習 JSONL と `<出力>.manifest.json` が作られます。

記録中は増加中の元ファイルを読まないよう生成を開始できません。**元 sequence の欠落を許可**は既定で無効で、本番学習では有効にしないでください。

## コマンドラインから生成

`inputctl.exe` はオフラインで動作し、inputd サービスを必要としません：

```powershell
inputctl.exe resample .\episode.jsonl --hz 20
inputctl.exe resample .\episode.jsonl --hz 40
inputctl.exe resample .\episode.jsonl --hz 60
inputctl.exe resample .\episode.jsonl .\episode.37hz.jsonl --hz 37 --overwrite
```

出力先を省略すると `<元ファイル>.<レート>hz.training.jsonl` になります。sequence 欠落は既定で失敗し、不完全な履歴データを意図的に受け入れる場合だけ `--allow-gaps` を使います。

## イベントを失わない意味論

各フレームは半開区間 `[window_start_us, window_end_us)` を使います。境界上のイベントは次フレームに入ります。整数有理数境界により、60 Hz などでも浮動小数点ドリフトが発生しません。

- フレーム内の `dx`、`dy`、`wheel_x`、`wheel_y` をそれぞれ 64 ビット整数で合計します。
- `keyboard.held` と `mouse.held_buttons` はフレーム終了時の期待状態で、空フレームは前状態を継承します。
- `pressed`、`released`、`pressed_buttons`、`released_buttons` はフレーム内エッジを保持します。
- `transitions` は全 down/up エッジを元順序、`offset_us`、`source_sequence` とともに保持するため、最終 held が同じ短クリックも消えません。
- `source_event_count`、`source_first_sequence`、`source_last_sequence` でフレームごとの元イベント範囲を監査できます。
- 既定では sequence が 1 から連続し、時刻が単調で、単一 session である必要があります。異常時は中止し、一時出力を削除します。

出力は一時ファイルへ書き、完了後だけ原子的に置換します。manifest には元／出力 SHA-256、元イベント数、出力フレーム数、空フレーム数、sequence 欠落数を記録します。

## フレーム構造

学習 JSONL の各行が 1 フレームです：

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

状態だけを使うポリシーは `held`、`held_buttons`、マウス差分を利用できます。短クリック、連打、正確なエッジを再現するモデルは `transitions` も読み取ってください。

## 学習前の検証

最低限、次を確認します：

1. manifest の `sequence_gaps` が 0；
2. 元／出力 SHA-256 が manifest と一致；
3. `frame_index` が連続し、隣接時間窓が正確に接続；
4. `source_event_count` 合計が manifest `source_events` と一致；
5. マウス差分合計が元軌跡と一致。
