# inputd

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md)

視覚エージェント、ゲームポリシー、学習データ収集のための Windows ローカル・キーボード／マウス実行基盤です。

`inputd` は C# と .NET 10 で実装されています。Win32 入力を独立プロセスと単一スケジューラスレッドに集約し、低頻度の HTTP Command API、常時接続の WebSocket Realtime API、Raw Input のロスレス記録、軌跡再生、Windows Dashboard、OBS 連携収録を提供します。

> 現在は Windows 専用の初期開発版です。アンチチート回避を目的としたツールではありません。対象アプリケーションの利用規約と適用法令を守ってください。

## 主な機能

- 相対／絶対マウス移動、5 ボタン、水平／垂直ホイール。
- スキャンコードによる物理キー入力と Unicode テキスト入力。
- 有界 FIFO コマンドキューと、容量 1 の latest-wins リアルタイムキュー。
- 期待入力状態フレームの差分からキー／マウスボタンの down/up を生成。
- 切断、リース期限切れ、フォーカス喪失、対象終了、サービス停止、緊急停止時の自動 `release_all`。
- 高ポーリングレートのマウス差分と順序を保持するイベント単位 Raw Input JSONL。
- 記録検証、元のタイミングでの再生、固定フレームレート学習データへのロスレス変換。
- 状態表示、録画、再生、ファイル選択、ホットキー設定を行う WinForms Dashboard。
- 映像と入力を同名で保存する OBS Lua スクリプトと独立 .NET bridge。

## 構成

```text
AI / スクリプト / プラグイン
   ├─ HTTP JSON Command API ── 有界 FIFO ─┐
   └─ WebSocket Realtime API ─ latest wins ├─ Input Scheduler ─ Win32 SendInput
                                           └─ 状態機械 / 安全 watchdog

物理入力 ─ Windows Raw Input ─ ロスレス JSONL ─ 検証 / 再生 / 変換
OBS ─ Lua ─ Named Pipe ─ inputd-obs-bridge ─ サイドカー入力ファイル
```

HTTP／WebSocket ハンドラーは `SendInput` を直接呼びません。すべての入力は唯一の Input Scheduler スレッドで直列化され、同時リクエストの混在を防ぎます。

## 動作要件

- Windows 10 または Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 対象が管理者権限で動作している場合、Dashboard/inputd も同等以上の整合性レベルで実行

## クイックスタート

```powershell
git clone https://github.com/irregular-haguruma/inputd.git
cd inputd
dotnet build InputExecutor.sln -c Release
dotnet run --project src/InputExecutor.Dashboard -c Release
```

Dashboard が本プロジェクトの GUI です。ダブルクリック可能な Windows バンドルを作成するには：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-dashboard.ps1
```

次を実行します：

```text
artifacts\inputd-dashboard-win-x64\inputd-dashboard.exe
```

Dashboard は一度 UAC 昇格を要求し、同じディレクトリの `inputd.exe` を起動できます。既定の保存先は `Documents\inputd-recordings` です。

サービスのみを使う場合：

```powershell
dotnet run --project src/InputExecutor.Service -c Release
dotnet run --project src/InputExecutor.Cli -c Release -- health
dotnet run --project src/InputExecutor.Cli -c Release -- create
```

初回起動時に `%LOCALAPPDATA%\inputd\access.token` が生成されます。対象ウィンドウを前面にしてセッションを作成し、返された Session ID と capability token を保存してから明示的に Arm してください。

## 記録と再生

Dashboard の既定ホットキー：

| 操作 | 既定値 |
|---|---|
| 記録開始 | `Ctrl+Alt+F9` |
| 記録停止 | `Ctrl+Alt+F10` |
| 軌跡再生 | `Ctrl+Alt+F7` |
| 再生停止 | `Ctrl+Alt+F8` |
| 緊急停止 | `Ctrl+Alt+F12` |

ホットキーは変更できます。記録の開始／停止に使った境界ホットキーは最終軌跡から除外されます。高頻度入力は独立キューと永続化先へ書き込まれ、UI は約 30 FPS の集約スナップショットだけを読むため、表示更新が遅くても学習イベントは欠落しません。

完成した記録は、連番と終端状態を検証し、元の相対タイミングで再生できます。また、20/40/60 Hz または任意の固定レートへ変換し、イベント総数と `dx/dy/wheel` の集計値を検証できます。[記録と再生](docs/raw-input-replay.md)および[学習データ](docs/training-data.md)を参照してください。

## ゲーム内の相対マウス

通常の入力には `SendInput` を使います。一部のゲームはキーやクリックを受け付けても、`SendInput` の相対マウスによる視点移動を無視します。そのため Dashboard の再生では、既定で相対移動だけ Windows の `mouse_event` 互換経路を使い、その他の入力は `SendInput` のままです。

任意の Interception バックエンドは明示的な互換性試験専用です。第三者 DLL とカーネルドライバーは同梱も自動インストールもされません。アンチチート回避目的で使用しないでください。

## OBS 連携収録

OBS 連携は [`adapters/obs-inputd-plugin`](adapters/obs-inputd-plugin) にあります：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

OBS の録画開始と同時に Raw Input を記録し、停止後に次を生成します：

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

異常終了したデータは `*.partial` のまま保持され、完全な学習軌跡として扱われません。[OBS ガイド](docs/obs.md)を参照してください。

## API と安全性

- Command API：`http://127.0.0.1:17841`
- Realtime API：`ws://127.0.0.1:17841/v1/realtime`
- 数値表記のループバックだけを許可し、リモートバインドを拒否。
- サービスは Disarmed で起動し、セッション作成にはランダムなアクセストークンが必要。
- セッションを HWND、PID、実行ファイルパス／開始時刻、プロセス名許可リストに固定。
- 各操作で前面ウィンドウとリクエスト上限を検証。
- リアルタイムフレームは `sequence`、単調時刻、`deadline`、held keys/buttons、相対 `dx/dy`、ホイール状態を保持。
- 保留中の旧フレームは新フレームで置換され、期限切れフレームは破棄。

詳細は [protocol.md](docs/protocol.md) と [safety.md](docs/safety.md) を参照してください。

## テスト

```powershell
dotnet test InputExecutor.sln -c Release
```

実 Win32 統合テストは実際のポインターを動かし、キーを押下／解放するため、明示的な有効化が必要です：

```powershell
$env:INPUTD_RUN_WIN32_TESTS='1'
dotnet test tests/InputExecutor.Tests -c Release --filter Category=WindowsIntegration
```

全テスト項目と実ゲーム受け入れ手順は [testing.md](docs/testing.md) を参照してください。

## エージェント連携

Codex、OpenClaw、MCP アダプターは別リポジトリで管理します：

[irregular-haguruma/inputd-agent-integrations](https://github.com/irregular-haguruma/inputd-agent-integrations)

## ドキュメント

- [アーキテクチャ](docs/architecture.md)
- [Dashboard](docs/dashboard.md)
- [プロトコル](docs/protocol.md)
- [安全性](docs/safety.md)
- [学習データ](docs/training-data.md)
- [OBS](docs/obs.md)
- [テスト](docs/testing.md)

## ライセンス

本プロジェクトは [Apache License 2.0](LICENSE) の下で公開されています。
