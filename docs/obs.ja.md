# OBS 操作連携記録

[简体中文](obs.md) | [English](obs.en.md) | [日本語](obs.ja.md)

## 出力

OBS 拡張は公式フロントエンドの録画ライフサイクルに従い、完成した映像ごとに一つの操作軌跡を作成します。JSONL と整合性 manifest は映像と同じディレクトリ、同じファイル名本体を使います：

```text
D:\recordings\boss-fight.mkv
D:\recordings\boss-fight.input.jsonl
D:\recordings\boss-fight.input.jsonl.manifest.json
```

manifest には映像／軌跡の絶対パス、開始／停止 UTC 時刻、イベント数、保留／破棄数、終了理由、軌跡 SHA-256 を記録します。保留と破棄がともにゼロの場合だけ完全と判定します。

## OBS スクリプトと独立プロセスを使う理由

OBS は Lua 自動化とフロントエンドコールバックを公式に提供しています。Lua 拡張はライフサイクル通知だけを担当します。独立した `net10.0-windows` bridge が Raw Input ウィンドウ、順序付き収録チャネル、バッチ永続化、現在のユーザーだけがアクセスできる Named Pipe を所有します。

OBS 30 は `RECORDING_STARTED` 時点で設定済み出力ディレクトリしか返さない場合があるため、収録は直ちに次へ書き始めます：

```text
%LOCALAPPDATA%\inputd\obs-spool
```

`RECORDING_STOPPED` では `obs_frontend_get_last_recording()` から実際の映像パスを取得します。bridge は記録を排出し、同じディレクトリとファイル名本体へ軌跡を原子的に移動します。OBS または bridge が先に終了した場合、`*.partial` と不完全 manifest は spool に回復可能な状態で残り、完全な学習データとして扱われません。

Stop コールバックは bridge が最終化を受理した時点で返ります。キュー排出、SHA-256、ファイル移動は bridge 内で継続するため、大きな軌跡でも OBS フロントエンドを停止させません。

## ビルドとインストール

リポジトリのルートで実行します：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

**OBS > ツール > スクリプト > +** から一度だけ追加します：

```text
%APPDATA%\obs-studio\scripts\inputd-obs\inputd-obs.lua
```

無人登録では先に OBS を終了します：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1 -Register
```

登録処理は現在のシーンコレクションをタイムスタンプ付きでバックアップし、`modules["scripts-tool"]` だけを原子的に更新します。再実行しても重複しません。

## 整合性と権限

- OBS はゲームと同等以上の Windows 整合性レベルで実行し、bridge は OBS のトークンを継承します。
- Pipe は `PipeOptions.CurrentUserOnly` を使い、TCP ポートを公開しません。
- 本番記録では inputd タグ付き合成イベントを除外します。
- 正確な入隊 watermark が空になり Flush された後だけ最終 JSONL が現れます。
- ファイル名競合は明示的に失敗して partial を保持し、既存学習データを上書きしません。

公式資料：[OBS frontend API](https://docs.obsproject.com/reference-frontend-api)、[OBS scripting guide](https://docs.obsproject.com/scripting)。
