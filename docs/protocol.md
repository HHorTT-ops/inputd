# 协议 v1

[简体中文](protocol.md) | [English](protocol.en.md) | [日本語](protocol.ja.md)

所有 JSON 使用 UTF-8，时长使用毫秒。`timestamp_us`、`deadline_us` 和响应中的 `monotonic_us` 使用 Windows 单调运行时间微秒（`Environment.TickCount64 * 1000`），同一台电脑上的客户端无需同步墙上时钟即可比较 deadline。

## 启动令牌与会话令牌

创建会话需要 `X-Inputd-Access-Token`。默认令牌位于：

```text
%LOCALAPPDATA%\inputd\access.token
```

每次成功创建会话都会返回独立的 `session_id` 和 `capability_token`。后续 HTTP 请求通过 `X-Inputd-Token` 或 `Authorization: Bearer` 发送 capability token。

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

创建会话可使用 `bind_to_foreground: true`、显式 `target` 或 `target_process_id`。PID 绑定会解析主 HWND，并固定 HWND、PID、路径和进程启动时间。`/focus` 只激活已经绑定的 HWND；Arm 和每个输入动作仍会检查前台窗口。

`snapshot` 返回 `image/png`。Frame ID、虚拟桌面原点、尺寸和采集时间通过 `X-Inputd-*` 响应头返回。基于截图坐标的适配器必须使用返回的原点换算 `(x,y)`，并拒绝过期 Frame ID。

Raw Input 录制属于会话范围，与 Command/Realtime 动作使用相同的 JSONL sink。`include_executor_events` 默认为 false。启用后，诊断 Hook 只把带 inputd 标记的注入保存为 `*_hook_injected`，不会伪装成物理 Raw Input。

Command 示例：

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

动作类型包括 `mouse_move_relative`、`mouse_move_absolute`、`mouse_button`、`mouse_wheel`、`key`、`text`、`wait`、`release_all` 和 `barrier`。物理键使用类似 DOM `KeyboardEvent.code` 的名称并映射到 Windows 扫描码；文本单独使用 Unicode `KEYEVENTF_UNICODE`。

## Realtime WebSocket

连接：

```text
ws://127.0.0.1:17841/v1/realtime?session_id={id}
Authorization: Bearer {capability_token}
```

持续发送当前期望状态帧：

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

状态机根据上一个已执行帧计算 held 集合差异。响应状态为 `executed`、`stale`、`expired`、`superseded` 或 `rejected`。关闭 WebSocket 会 Disarm 会话并释放全部 held 输入。

JSON Schema 位于仓库的 `schemas` 目录。
