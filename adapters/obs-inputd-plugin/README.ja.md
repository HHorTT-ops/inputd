# inputd OBS 入力連携プラグイン

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md)

この OBS Lua 拡張は公式フロントエンド録画ライフサイクルに従います：

- `RECORDING_STARTED`：公開済み .NET 10 Raw Input bridge を起動；
- `RECORDING_STOPPED`：収録キューを完全に排出し、映像横に入力ファイルを作成。

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

OBS スクリプトスレッドが交換するのは、現在のユーザーだけが利用できる Windows Named Pipe 上の小さな JSON メッセージだけです。Raw Input 収録と永続 JSONL 書き込みは独立した `inputd-obs-bridge.exe` が行います。

OBS 30 は録画開始時に設定ディレクトリしか返さない場合があるため、活動中データを最初に次へ書きます：

```text
%LOCALAPPDATA%\inputd\obs-spool
```

OBS が最終映像パスを返した後、bridge は同じファイル名本体へ軌跡を原子的に移動します。中断セッションは `*.partial` と不完全 manifest のまま残り、完全な学習軌跡として扱われません。停止最終化を先に受理し、排出とハッシュは bridge 内で続けるため、大きな入力ファイルでも OBS フロントエンドを停止させません。

**ツール > スクリプト > +** から `inputd-obs.lua` を導入します。既定 helper パス、または公開物の `bridge\inputd-obs-bridge.exe` を指定してください。管理者権限ゲームを記録する場合、OBS と bridge は同等以上の整合性レベルで実行します。

詳しいビルド、導入、整合性の説明は [`docs/obs.ja.md`](../../docs/obs.ja.md) を参照してください。
