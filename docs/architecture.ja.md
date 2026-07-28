# アーキテクチャ

[简体中文](architecture.md) | [English](architecture.en.md) | [日本語](architecture.ja.md)

## プロセス、スレッド、キュー

`inputd` は独立した Windows ローカルプロセスです。ASP.NET Core がループバック HTTP と WebSocket を処理しますが、リクエストスレッドから `SendInput` を直接呼ぶことはありません。

```text
HTTP Command API ──> 有界 FIFO（64）────────┐
WebSocket フレーム ─> latest-wins（1）───────┼─> 専用 Input Scheduler スレッド
                                              │
                                              ├─> 入力状態機械
                                              └─> Windows 入力バックエンド
```

コマンドシーケンスは一つの FIFO 作業項目として実行されるため、二つのシーケンスが混在しません。安全処理を優先しつつ、リアルタイムフレームを最大 8 個処理した後は待機中のコマンドに実行機会を与え、飢餓を防ぎます。

リアルタイムキューが保持する未実行の期待状態フレームは最大一つです。新しいフレームに置き換えられたフレームは `superseded` になり、実行開始済みのフレームを遡って置換することはできません。

Dashboard の再生も専用 `InputScheduler` で直列化されます。既定では相対移動に `WindowsLegacyMouseReplayBackend`、その他に `WindowsSendInputBackend` を使います。任意の互換モードを明示的に有効にした場合、相対移動、マウスボタン、ホイールを `WindowsInterceptionInputBackend` に切り替えられますが、スキャンコードキー、Unicode テキスト、絶対移動は `SendInput` のままです。

## プロジェクト

- `InputExecutor.Core`：プロトコルモデル、検証、アクション実行、状態機械、軌跡記録、固定レート学習データ生成。
- `InputExecutor.Windows`：`LibraryImport`/PInvoke、スキャンコード、`SendInput`、任意互換バックエンド、対象保護、スクリーンショット、Raw Input 記録、緊急停止。
- `InputExecutor.Scheduler`：単一スレッドスケジューラ、有界キュー、セッション所有権、Arm リース、watchdog。
- `InputExecutor.Service`：ループバック HTTP、WebSocket、起動アクセストークン、バックグラウンド watchdog。
- `InputExecutor.Dashboard`：WinForms UI、サービス操作、JSONL 記録、ホットキー除去、軌跡再生、学習データ生成。
- `InputExecutor.ObsBridge`：現在のユーザーだけが利用できる Named Pipe サービス。Raw Input 記録器を OBS のライフサイクル収録とファイル対応付けに再利用します。
- `InputExecutor.Cli`：診断、セッション管理、オフライン `resample`。
- `InputExecutor.TestMonitor`：実 Computer Use 受け入れ試験用の可視ターゲット。

## ロスレス記録経路

Raw Input 記録器は独自の Win32 メッセージスレッド上に message-only window を持ちます。`WM_INPUT` コールバックは不変イベントを順序付き単一読者チャネルへ渡すだけで、ウィンドウプロシージャ内では JSON 化もディスク書き込みも行いません。

ライターは最大 8 ms または 1024 レコードをまとめ、WriteThrough ストリームで書き込みます。停止時には正確な入隊 watermark が空になるまで待って Flush し、保留数や障害を報告します。有界キュー満杯を理由に黙ってイベントを破棄しません。

本番記録は `WM_INPUT` のみを使用し、低レベルのグローバル Hook を導入しません。合成入力診断を明示的に有効にした記録中だけ Hook を一時導入し、`ExecutorEventTag` を持つ inputd イベントだけを認識して、停止時に直ちに解除します。

Dashboard が集約するのは約 33 ms ごとの表示更新だけで、永続化経路にはすべての元イベントが届きます。

## OBS 経路

OBS Lua コールバックは小さな JSON ライフサイクル通知だけを送ります。入力記録や軌跡書き込みは行いません。`inputd-obs-bridge.exe` が記録開始、キュー排出、SHA-256、manifest 作成、最終映像横への原子的移動を担当します。

bridge が最終化を受理した時点で Stop コールバックを返せるため、大きな軌跡の排出とハッシュ処理で OBS フロントエンドを停止させません。

## 学習データ変換

オフライン生成器はイベント JSONL をストリーム読み込みし、半開区間の固定レートウィンドウへ変換します。整数有理数境界で時間ドリフトを避け、全マウス／ホイール差分を加算し、空フレームでも held 状態を引き継ぎ、キー／ボタンのエッジを `transitions` に順序付きで保持します。

出力 manifest には元ファイルと出力の SHA-256、イベント数、フレーム数、空フレーム数、シーケンス欠落数を記録します。

## 通信方式の拡張

Realtime v1 は `/v1/realtime` の WebSocket JSON です。通信層はスケジューリングと状態管理から分離されています。将来用 Named Pipe は `\\.\pipe\inputd-realtime-v1` として予約され、同じ `SubmitRealtimeFrameAsync` API を使い、入力バックエンドを直接呼んではなりません。
