# Dashboard ガイド

[简体中文](dashboard.md) | [English](dashboard.en.md) | [日本語](dashboard.ja.md)

## 起動

Windows x64 と .NET 10 Desktop Runtime が必要です。.NET 10 SDK には Runtime も含まれます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-dashboard.ps1
```

EXE だけをコピーせず、公開されたディレクトリ全体を保持して次を実行します：

```text
artifacts\inputd-dashboard-win-x64\inputd-dashboard.exe
```

アプリは管理者権限を要求します。管理者権限のゲームを記録する場合、低い整合性レベルの記録器にはゲームが前面の間 Raw Input が届かなくなることがあるため、昇格が必要です。

ソースから起動する場合：

```powershell
dotnet run --project src/InputExecutor.Dashboard -c Release
```

## 画面機能

- キーボード表示では押下中のキーが黒くなり、解放すると戻ります。
- マウス表示ではボタン、最新の相対方向、`ΔX/ΔY`、ホイール、画面位置を確認できます。
- **inputd を起動**：Dashboard 横の独立サービスを起動します。
- **記録開始／停止**：イベント単位 JSONL を作成し、安全に最終化します。
- **記録フォルダーを開く**：出力先を開きます。
- **学習データ生成**：完全な JSONL を 20/40/60 Hz または 1–1000 Hz の任意レートへ変換し、整合性 manifest を作成します。
- **ホットキー設定**：記録と再生のグローバルショートカットを変更します。
- **操作記録を選択**：`human_raw_input` JSONL を注入前に検証します。
- **再生／停止**：held 状態、符号付きマウス差分、ボタン、ホイールを元の相対タイミングで再生し、停止時にすべて解放します。
- **状態をクリア**：表示カウンターだけを消去し、記録ファイルは削除しません。

待機中は直近にフォーカスされた外部ウィンドウを記憶します。ゲームを一度前面にし、Dashboard に戻って記録を開始すると、その HWND、PID、パス、タイトルが記録中の対象として固定されます。

既定の出力先：

```text
%USERPROFILE%\Documents\inputd-recordings
```

起動前に `INPUTD_DASHBOARD_RECORDING_DIR` で変更できます。

## グローバルホットキー

- `Ctrl+Alt+F9`：記録開始
- `Ctrl+Alt+F10`：記録停止
- `Ctrl+Alt+F7`：選択した軌跡を再生
- `Ctrl+Alt+F8`：再生停止
- `Ctrl+Alt+F12`：固定の緊急停止

設定保存先：

```text
%LOCALAPPDATA%\inputd\dashboard.settings.json
```

ホットキーは低レベル Hook ではなく `RegisterHotKey` を使います。重複、不完全な組み合わせ、登録競合は拒否されます。

記録開始／停止では境界ショートカットが完全に解放されるまで待ちます。停止後、ストリーム式の原子的書き換えで開始／停止ショートカットの物理コードと held 状態だけを除去し、イベント行や同時入力は保持します。書き換えに失敗した場合も元の永続ファイルは残ります。

## 再生と相対マウス互換性

再生はローカル軌跡を読み、専用単一スレッド `InputScheduler` の完了を待ちます。イベントごとに HTTP を送信しません。同じ held 状態で間隔が 2 ms 以下の高ポーリングマウスパケットは、符号付き `dx/dy/wheel` 合計を厳密に保持したまま注入時だけ結合できます。元の学習ファイルは変更しません。

既定で未選択の **カーネルマウス互換** モードでは、相対カメラ移動に Win32 `mouse_event`、スキャンコードキー、ボタン、ホイール、絶対移動、テキストに `SendInput` を使います。ドライバーは不要ですが、オフラインまたは明示的に許可された対象で互換性を確認してください。

任意モードを選ぶと、相対移動、ボタン、ホイールに `WindowsInterceptionInputBackend` を使います。`interception.dll` とカーネルドライバーは同梱も自動インストールもされません。依存関係がなければ入力前に失敗します。[相対マウス再生バックエンド](raw-input-replay.ja.md)を参照してください。

フォーカス喪失、対象終了、停止操作、Dashboard 終了、`Ctrl+Alt+F12` では再生を中止して `release_all` を実行します。記録と再生は同時に実行できません。

## 物理入力と診断入力

Dashboard は起動直後から物理 Raw Input をプレビューします。

**合成入力診断** が無効なら、JSONL には物理キーボード／マウスだけを保存し、グローバル低レベル Hook は導入しません。明示的に有効にすると、その記録中だけ Hook を使い、inputd のタグ付きイベントを `injected_tagged: true` として保存し、停止時に直ちに解除します。

ゲーム記録では診断を無効にしてください。本番収録は `WM_INPUT + RIDEV_INPUTSINK` のみを使用し、ゲームプロセスへ注入／Hook せず、アンチチート回避も試みません。

## 高頻度入力とデータ整合性

画面表示と永続記録は別経路です：

- すべての Raw Input イベントを連続 `sequence` の順序付き非破棄チャネルへ入れる；
- UI は約 33 ms ごとの集約スナップショットだけを読む；
- 表示集約は JSONL をサンプリング・削除しない；
- ライターは最大 1024 件をまとめ、WriteThrough で書く；
- 停止時は保留書き込みがゼロになるまで待ち、最後に Flush する；
- イベントが存在し、保留と失敗がゼロの記録だけを完全と判定する。

高ポーリングレートのマウスには空き容量のあるローカルストレージを使用してください。容量不足や I/O 障害時は成功を装わず、記録を利用不可として表示します。

固定レート生成器は全差分を加算し、短いキー／ボタンエッジを `transitions` に保持します。[学習データ](training-data.ja.md)を参照してください。サービス側入力は対象固定セッションを明示的に Arm するまで Disarmed のままで、安全障害時には `release_all` が実行されます。
