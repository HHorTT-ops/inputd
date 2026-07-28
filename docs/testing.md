# 测试与验收

[简体中文](testing.md) | [English](testing.en.md) | [日本語](testing.ja.md)

inputd 将确定性的自动测试、需要明确启用的 Win32 注入测试和真实应用验收分开。不能用合成输入测试结果代替受保护或管理员权限游戏中的物理 Raw Input 记录证据。

## 自动测试

运行默认 Release 测试：

```powershell
dotnet test InputExecutor.sln -c Release
```

默认测试不会主动移动真实指针或按下真实键，覆盖：

- 有符号相对鼠标与扫描码 down/up；
- 多键、多按钮 held 状态；
- Realtime 状态差异、latest-wins 和 deadline 过期；
- 有界 Command FIFO 顺序及并发序列不交叉；
- 断连、租约、失焦、目标退出、Disarm、关闭时的释放；
- JSONL 序号、清单、原子完成和快捷键清理；
- 原始时间回放与固定频率训练数据转换；
- OBS bridge 路径、消息、队列排空和 partial 处理；
- HTTP 与 WebSocket 服务集成。

## 明确启用的 Windows 注入测试

这些测试会真实移动 Windows 指针并按键。关闭敏感应用，准备好紧急停止，只对一次性测试窗口运行：

```powershell
$env:INPUTD_RUN_WIN32_TESTS='1'
dotnet test tests/InputExecutor.Tests -c Release --filter Category=WindowsIntegration
```

测试范围包括正负鼠标移动、扫描码边沿、多键 held、全局热键生命周期、诊断记录器、回放清理以及适用时的指针恢复。

## 调度与安全验收

手工验收必须确认：

1. HTTP/WebSocket 处理器只入队，不直接调用后端。
2. Command 保持 FIFO，两个并发序列不互相穿插。
3. 只执行最新的待处理 Realtime 状态帧。
4. 过期 `deadline` 不产生注入。
5. Realtime 连接关闭后释放全部执行器 held 状态。
6. 绑定目标失焦后释放 held 状态并暂停会话。
7. 目标或客户端退出、Disarm、紧急停止、服务关闭后没有残留 held 输入。
8. 服务关闭时在销毁调度器前尽最大努力执行 `release_all`。

## Raw Input 记录验收

生产记录只使用 Windows Raw Input，不得安装低级全局键盘或鼠标 Hook。诊断模式可临时用 Hook 检查带 inputd 标记的合成事件，但 Stop 后必须卸载。

物理输入记录流程：

1. 目标为管理员权限时，以管理员权限启动 Dashboard。
2. 绑定精确目标并开始新记录。
3. 按下和释放至少两个键，点击鼠标按钮、滚动，并向正负两个方向移动鼠标。
4. 停止并等待待写数量归零。
5. 确认 `sequence` 从 1 开始且没有断号。
6. 确认最终 held keys/buttons 为空。
7. 确认清单标记完整且 SHA-256 与 JSONL 一致。
8. 确认录制边界快捷键被移除，同时发生的无关输入仍保留。

高频验收应在持续输入鼠标包时观察 Dashboard 仍然响应。界面可以合并显示，但持久化 JSONL 必须保留完整有序事件，结束时 dropped 和 pending 都为零。

## 回放验收

使用 Test Monitor 或其他一次性离线应用：

1. 选择通过校验的完整 JSONL。
2. 保持精确绑定目标位于前台。
3. 播放并观察物理键 held、按钮边沿、滚轮和有符号相对移动。
4. 分别用停止快捷键和切换焦点中断一次。
5. 两种情况下都确认清空队列并释放全部输入。
6. 完整播放后比较发出事件总数和 `dx/dy/wheel` 聚合值。

部分游戏会忽略相对 `SendInput` 视角移动。默认兼容路径与可选 Interception 路径必须分开测试并记录后端。不得用兼容性测试规避反作弊。

## 固定频率训练数据验收

把完整事件轨迹转换为 20、40、60 Hz 和至少一个自定义频率。每个输出都应满足：

- 帧索引和时间窗口连续；
- `source_event_count` 之和等于源事件数；
- 空帧继承 held 状态；
- 帧内短按/释放边沿仍存在；
- 有符号 `dx/dy/wheel` 总和与源一致；
- 源和输出 SHA-256 与清单一致；
- 除非明确允许，否则拒绝 partial 或断号数据。

## OBS 验收

安装 Lua 脚本和 bridge 后进行短录制与持续录制：

1. `RECORDING_STARTED` 启动 bridge 采集且不阻塞脚本线程。
2. 活跃数据以 `*.partial` 写入当前用户 spool。
3. `RECORDING_STOPPED` 排空队列、生成哈希并原子移动到最终视频旁。
4. 视频、`.input.jsonl` 和清单使用同一文件名主体。
5. OBS 或 bridge 异常退出时保留 partial，但不标记完整。
6. 大型轨迹排空和哈希期间 OBS 仍保持响应。

## 真实游戏验收

只能在离线或明确允许的环境中进行。记录目标进程身份、完整性级别、输入后端、显示模式、鼠标设置、事件数、断号数、dropped/pending、最终 held 状态和清单哈希。

通过的记录应在游戏拥有前台焦点时包含物理按键边沿和正负相对鼠标值。重复多轮开始/停止以及至少一轮持续记录。私人录像、截图、账号标识和机器路径不得提交到仓库。
