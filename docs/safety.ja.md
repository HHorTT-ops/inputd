# 安全性と障害時の動作

[简体中文](safety.md) | [English](safety.en.md) | [日本語](safety.ja.md)

## 対象の同一性

セッション作成時、inputd は対象 HWND を取得または受け取り、PID、実行ファイル名／パス、プロセス開始時刻を検証します。`allowed_processes` が空なら、固定対象のプロセス名だけを含む許可リストに変換します。すべての操作で正確に固定された HWND が前面である必要があります。

Windows UIPI により、通常整合性レベルの inputd から管理者権限の対象への注入が阻止される場合があります。この場合 `SendInput` は Win32 エラーを設定せずゼロを返すことがあります。バックエンドは `INPUT_INJECTION_FAILED` を返し、セッションを Disarm します。

## 停止条件

次の条件では保留作業を取り消し、状態機械が追跡するキー／ボタンを解放し、キューを消去して Arm リースを取り消します：

- Realtime WebSocket 切断；
- Realtime リース期限切れ；
- 対象のフォーカスまたは同一性喪失；
- 固定クライアント終了または PID 再利用；
- Arm 期限切れ；
- Command 取消または実行失敗；
- 明示的 Disarm または緊急解放；
- `Ctrl+Alt+F12`；
- 正常終了、プロセス終了コールバック、未処理例外コールバック。

OS による強制終了、停電、Runtime 破損をユーザーモードソフトウェアで完全に回復することはできません。プロセス終了と未処理例外経路は best effort で、スケジューラが解放を送るまで最大 2 秒待ちます。

## 上限

- Command FIFO 容量：64；
- 1 Command のアクション数：1–256；
- Realtime 保留容量：1；
- 1 軸の相対差分：±32768 counts；
- held keys：32；
- held buttons：5；
- テキスト：16,384 UTF-16 code units；
- Arm 期間：1–3600 秒；
- Realtime リース：50–10,000 ms；
- WebSocket メッセージ：64 KiB。
