# 安全与故障行为

[简体中文](safety.md) | [English](safety.en.md) | [日本語](safety.ja.md)

## 目标身份

创建会话时，inputd 会捕获或接受目标 HWND，并验证其 PID、可执行文件名/路径和进程启动时间。若请求中的 `allowed_processes` 为空，服务会建立只包含绑定目标进程名的单项白名单。每个动作都要求精确绑定的 HWND 位于前台。

Windows UIPI 可能阻止普通完整性级别的 inputd 向管理员权限目标注入。此时 `SendInput` 可能返回零但不设置 Win32 错误；后端会返回 `INPUT_INJECTION_FAILED` 并 Disarm 会话。

## 停止条件

以下情况都会取消待执行工作、释放状态机记录的键和按钮、清空队列，并撤销 Arm 租约：

- Realtime WebSocket 断开；
- Realtime 租约超时；
- 目标焦点或身份丢失；
- 绑定客户端退出或 PID 被复用；
- Arm 到期；
- Command 取消或执行失败；
- 显式 Disarm 或紧急释放；
- `Ctrl+Alt+F12`；
- 正常关闭、进程退出回调或未处理异常回调。

操作系统强制终止、断电或运行时损坏无法由用户态软件完全恢复。进程退出和未处理异常路径属于尽最大努力处理，最多等待调度器线程两秒以发出释放事件。

## 限制

- Command FIFO 容量：64；
- 单个 Command 动作数：1–256；
- Realtime 待执行容量：1；
- 单轴相对位移：±32768 counts；
- held keys：32；
- held buttons：5；
- 文本：16,384 个 UTF-16 code units；
- Arm 时长：1–3600 秒；
- Realtime 租约：50–10,000 ms；
- WebSocket 消息：64 KiB。
