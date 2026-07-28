# プロトコル v1

[简体中文](protocol.md) | [English](protocol.en.md) | [日本語](protocol.ja.md)

すべての JSON は UTF-8、期間はミリ秒です。`timestamp_us`、`deadline_us`、応答の `monotonic_us` は Windows の単調稼働時間マイクロ秒（`Environment.TickCount64 * 1000`）を使うため、同じ PC 上のクライアントは実時刻同期なしで deadline を比較できます。

## 起動トークンとセッショントークン

セッション作成には `X-Inputd-Access-Token` が必要です。既定の保存先：

```text
%LOCALAPPDATA%\inputd\access.token
```

作成に成功すると独立した `session_id` と `capability_token` が返ります。以後の HTTP リクエストでは `X-Inputd-Token` または `Authorization: Bearer` で capability token を送ります。

## Command API

```text
POST   /v1/sessions
GET    /v1/sessions/{id}
POST   /v1/sessions/{id}/arm
POST   /v1/sessions/{id}/focus
POST   /v1/sessions/{id}/disarm
POST   /v1/sessions/{id}/commands
POST   /v1/sessions/{id}/cancel
POST   /v1/sessions/{id}/release-all
GET    /v1/sessions/{id}/snapshot
POST   /v1/sessions/{id}/recording/start
GET    /v1/sessions/{id}/recording
POST   /v1/sessions/{id}/recording/stop
DELETE /v1/sessions/{id}
```

セッション作成では `bind_to_foreground: true`、明示的 `target`、または `target_process_id` を使えます。PID 固定では主 HWND を解決し、HWND、PID、実行ファイルパス、開始時刻を固定します。`/focus` は固定済み HWND だけをアクティブ化し、Arm と各入力操作でも前面所有を再確認します。

`snapshot` は `image/png` を返します。Frame ID、仮想デスクトップ原点、サイズ、取得時刻は `X-Inputd-*` ヘッダーに入ります。スクリーンショット相対アダプターは返された原点で `(x,y)` を変換し、古い Frame ID を拒否する必要があります。

Raw Input 記録はセッション単位で、Command/Realtime と同じ JSONL sink を使います。`include_executor_events` の既定値は false です。有効時も診断 Hook は inputd タグ付き注入だけを `*_hook_injected` として保存し、物理 Raw Input として表示しません。

コマンド例：

```json
{
  "version": "1.0",
  "sequence": 102,
  "deadline_us": 184521920000,
  "exclusive": true,
  "actions": [
    { "type": "mouse_move_relative", "dx": 120, "dy": -30, "duration_ms": 80 },
    { "type": "mouse_button", "button": "left", "action": "click" },
    { "type": "key", "code": "Enter", "action": "press" },
    { "type": "text", "text": "Hello" }
  ]
}
```

アクションは `mouse_move_relative`、`mouse_move_absolute`、`mouse_button`、`mouse_wheel`、`key`、`text`、`wait`、`release_all`、`barrier` です。物理キーは DOM `KeyboardEvent.code` 形式の名前を Windows スキャンコードへ変換します。テキストは別経路の Unicode `KEYEVENTF_UNICODE` を使います。

## Realtime WebSocket

接続先：

```text
ws://127.0.0.1:17841/v1/realtime?session_id={id}
Authorization: Bearer {capability_token}
```

現在の期待入力状態を継続送信します：

```json
{
  "version": "1.0",
  "sequence": 8841,
  "timestamp_us": 184521904214,
  "deadline_us": 184521920000,
  "keyboard": { "held": ["KeyW", "ShiftLeft"] },
  "mouse": {
    "dx": 12,
    "dy": -4,
    "held_buttons": ["right"],
    "wheel_x": 0,
    "wheel_y": 0
  }
}
```

状態機械は直前に実行されたフレームとの差分から held 集合を更新します。応答 status は `executed`、`stale`、`expired`、`superseded`、`rejected` のいずれかです。WebSocket を閉じるとセッションを Disarm し、すべての held 入力を解放します。

JSON Schema はリポジトリの `schemas` ディレクトリにあります。
