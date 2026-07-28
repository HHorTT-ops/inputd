# 架构

[简体中文](architecture.md) | [English](architecture.en.md) | [日本語](architecture.ja.md)

## 进程、线程与队列

`inputd` 是独立的 Windows 本地进程。ASP.NET Core 负责回环 HTTP 和 WebSocket 通信，但请求线程不会直接调用 `SendInput`。

```text
HTTP Command API ──> 有界 FIFO（64）──────┐
WebSocket 状态帧 ──> latest-wins（1）─────┼─> 独立 Input Scheduler 线程
                                           │
                                           ├─> 输入状态机
                                           └─> Windows 输入后端
```

Command 序列作为一个完整 FIFO 工作项执行，两个序列不会互相穿插。调度器优先处理安全控制工作，最多连续执行八个实时帧后会让出机会给等待中的 Command，避免饥饿。

实时队列最多保留一个待执行状态帧。新帧替换旧帧时，旧帧返回 `superseded`；已经开始执行的帧不能被追溯替换。

Dashboard 回放仍由单独的 `InputScheduler` 串行执行。默认组合为：相对鼠标使用 `WindowsLegacyMouseReplayBackend`，其余动作使用 `WindowsSendInputBackend`。显式启用可选兼容模式时，相对移动、鼠标按钮和滚轮可交给 `WindowsInterceptionInputBackend`；扫描码键盘、Unicode 文本和绝对移动仍走 `SendInput`。

## 项目组成

- `InputExecutor.Core`：协议模型、校验、动作执行器、状态机、轨迹记录和固定帧率训练数据生成。
- `InputExecutor.Windows`：`LibraryImport`/PInvoke、扫描码映射、`SendInput`、可选兼容后端、目标窗口保护、截图、Raw Input 记录和紧急停止热键。
- `InputExecutor.Scheduler`：单线程调度器、有界队列、会话所有权、Arm 租约和看门狗协调。
- `InputExecutor.Service`：回环 HTTP、WebSocket、启动访问令牌和后台看门狗。
- `InputExecutor.Dashboard`：WinForms 状态界面、服务控制、JSONL 录制、快捷键清理、轨迹回放和训练数据生成。
- `InputExecutor.ObsBridge`：只允许当前用户访问的命名管道服务，复用 Raw Input 记录器完成 OBS 生命周期采集和文件配对。
- `InputExecutor.Cli`：诊断、会话管理和离线 `resample`。
- `InputExecutor.TestMonitor`：真实 Computer Use 验收使用的可见测试目标。

## 无损记录路径

Raw Input 记录器在自己的 Win32 消息线程上维护 message-only window。`WM_INPUT` 回调只把不可变事件放入有序、单读者写入通道，不在窗口过程内序列化 JSON 或写磁盘。

写入器最多收集 8 ms 或 1024 条记录，通过 WriteThrough 文件流提交。停止录制时，它会等待精确的入队水位全部排空、刷新文件，并报告待写数量或故障；不会因固定大小队列已满而静默丢弃。

生产录制只读取 `WM_INPUT`，不安装低级全局 Hook。只有显式启用“合成输入诊断”时，记录期间才临时安装 Hook，并且只识别带 `ExecutorEventTag` 的 inputd 合成事件；停止后立即卸载。

Dashboard 只把界面刷新合并到约 33 ms 一次，持久化路径仍接收每个原始事件。

## OBS 路径

OBS Lua 回调只发送很小的 JSON 生命周期消息，不直接记录输入或写轨迹。`inputd-obs-bridge.exe` 负责启动记录器、排空队列、计算 SHA-256、写清单，并把临时文件原子移动到最终视频旁。

停止事件在 bridge 接受最终化请求后即可返回，因此大型轨迹的排空和哈希不会阻塞 OBS 前端线程。

## 训练数据转换

离线生成器流式读取事件 JSONL，并写入半开区间的固定频率窗口。整数有理数边界避免累计时间漂移；帧内全部鼠标和滚轮增量会求和，held 状态会跨空帧继承，按键和按钮边沿按源顺序保存在 `transitions` 中。

输出清单包含源文件和结果文件的 SHA-256、事件数量、帧数量、空帧数量和序列断号数量。

## 通信演进

Realtime v1 使用 `/v1/realtime` 上的 WebSocket JSON。通信层与调度器、状态机分离。未来预留的 Named Pipe 为 `\\.\pipe\inputd-realtime-v1`；它必须调用相同的 `SubmitRealtimeFrameAsync` 协调接口，不能直接调用输入后端。
