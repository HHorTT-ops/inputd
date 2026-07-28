# inputd

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md)

面向视觉智能体、游戏策略与训练数据采集的 Windows 本地键盘鼠标执行器。

`inputd` 使用 C#/.NET 10 构建。它把所有 Win32 输入集中到一个独立进程和单一调度线程中，同时提供低频 HTTP Command API、持续连接的 WebSocket Realtime API、Raw Input 无损记录、轨迹回放、Windows Dashboard，以及 OBS 配对录制。

> 当前为早期开发版本，仅支持 Windows。它不是反作弊绕过工具；在游戏中使用前，请遵守游戏服务条款及当地规定。

## 主要功能

- 相对/绝对鼠标移动、鼠标按键、横纵滚轮。
- 基于扫描码的物理键盘输入和 Unicode 文本输入。
- 有界 FIFO 命令队列与容量为 1 的 latest-wins 实时队列。
- 根据期望状态帧自动产生按键和鼠标按钮的 down/up 差异。
- 连接断开、租约超时、失焦、目标退出、服务关闭和紧急停止时自动 `release_all`。
- Windows Raw Input 事件级 JSONL 记录，保留高轮询率鼠标的小位移和原始顺序。
- 记录校验、原速回放，以及无丢失的固定帧率训练数据转换。
- WinForms Dashboard：实时状态、开始/停止录制、选择轨迹、播放/停止播放、快捷键设置。
- OBS Lua 脚本与独立 .NET bridge：视频和输入轨迹同名配对保存。

## 架构

```text
AI / 脚本 / 插件
   ├─ HTTP JSON Command API ── 有界 FIFO ─┐
   └─ WebSocket Realtime API ─ latest wins ├─ Input Scheduler ─ Win32 SendInput
                                           └─ 状态机 / 安全看门狗

物理键鼠 ─ Windows Raw Input ─ 无损 JSONL ─ 校验 / 回放 / 训练帧转换
OBS ─ Lua 脚本 ─ Named Pipe ─ inputd-obs-bridge ─ 视频旁路输入文件
```

HTTP 和 WebSocket 请求处理线程不会直接调用 `SendInput`。所有注入都由唯一的 Input Scheduler 线程串行执行，避免并发动作互相穿插。

## 系统要求

- Windows 10 或 Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 录制或控制管理员权限游戏时，Dashboard/inputd 需要以相同或更高完整性级别运行

## 快速开始

```powershell
git clone https://github.com/irregular-haguruma/inputd.git
cd inputd
dotnet build InputExecutor.sln -c Release
dotnet run --project src/InputExecutor.Dashboard -c Release
```

Dashboard 是本项目的图形界面。发布可双击运行的 Windows 版本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-dashboard.ps1
```

随后运行：

```text
artifacts\inputd-dashboard-win-x64\inputd-dashboard.exe
```

Dashboard 会显示一次 UAC 提示，并可启动同目录中的 `inputd.exe`。默认录制目录为 `Documents\inputd-recordings`。

也可以只启动服务和命令行：

```powershell
dotnet run --project src/InputExecutor.Service -c Release
dotnet run --project src/InputExecutor.Cli -c Release -- health
dotnet run --project src/InputExecutor.Cli -c Release -- create
```

服务首次启动时会生成 `%LOCALAPPDATA%\inputd\access.token`。创建会话时让目标窗口位于前台，保存返回的 Session ID 和 capability token，然后显式 Arm 会话。

## 录制与回放

Dashboard 默认快捷键：

| 功能 | 默认快捷键 |
|---|---|
| 开始录制 | `Ctrl+Alt+F9` |
| 停止录制 | `Ctrl+Alt+F10` |
| 播放轨迹 | `Ctrl+Alt+F7` |
| 停止播放 | `Ctrl+Alt+F8` |
| 紧急停止 | `Ctrl+Alt+F12` |

快捷键可在界面中修改。开始/停止录制的边界快捷键不会进入最终轨迹。记录器将高频输入写入独立队列和持久化文件；界面只读取约 30 FPS 的合并快照，因此界面刷新不会造成训练数据缺失。

轨迹完成后可：

- 校验序列是否连续、文件是否完整、结束时是否仍有按住状态；
- 按原始相对时间回放键盘、按钮、滚轮和有符号相对鼠标位移；
- 转换为 20/40/60 Hz 或自定义固定频率训练帧，并校验事件总数及 `dx/dy/wheel` 聚合值。

详见[录制与回放](docs/raw-input-replay.md)及[训练数据](docs/training-data.md)。

## 游戏内相对鼠标

常规输入使用 `SendInput`。部分游戏接受键盘和点击，却不接受 `SendInput` 的相对鼠标视角移动，因此 Dashboard 回放默认只对相对鼠标使用 Windows `mouse_event` 兼容路径，其他动作仍使用 `SendInput`。

可选 Interception 后端仅用于明确的兼容性测试；第三方 DLL 和内核驱动不随仓库分发，也不会自动安装。不要以规避反作弊为目的使用任何后端。

## OBS 配对录制

OBS 集成位于 [`adapters/obs-inputd-plugin`](adapters/obs-inputd-plugin)。安装脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

OBS 开始录制时自动开始 Raw Input 采集，停止后在视频旁生成：

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

未正常结束的数据保留为 `*.partial`，不会被误标成完整训练轨迹。详见 [OBS 指南](docs/obs.md)。

## API 与安全

- Command API：`http://127.0.0.1:17841`
- Realtime API：`ws://127.0.0.1:17841/v1/realtime`
- 只允许数字回环地址，默认禁止远程绑定。
- 服务默认 Disarmed；创建会话需要随机访问令牌。
- 会话绑定 HWND、PID、进程路径/启动时间和进程名白名单。
- 每个动作都检查目标前台窗口并限制范围、文本长度和队列容量。
- WebSocket 实时帧包含 `sequence`、单调时钟时间戳、`deadline`、held keys/buttons、`dx/dy` 和滚轮状态。
- 旧帧被新帧覆盖，过期帧不会执行。

协议细节见 [protocol.md](docs/protocol.md)，安全模型见 [safety.md](docs/safety.md)。

## 测试

```powershell
dotnet test InputExecutor.sln -c Release
```

真实 Win32 集成测试会移动真实鼠标并按下/释放真实按键，必须显式启用：

```powershell
$env:INPUTD_RUN_WIN32_TESTS='1'
dotnet test tests/InputExecutor.Tests -c Release --filter Category=WindowsIntegration
```

完整测试范围和真人游戏验收方法见 [testing.md](docs/testing.md)。

## 智能体集成

Codex、OpenClaw 与 MCP 适配器已拆分到：

[irregular-haguruma/inputd-agent-integrations](https://github.com/irregular-haguruma/inputd-agent-integrations)

## 文档

- [架构](docs/architecture.md)
- [Dashboard](docs/dashboard.md)
- [协议](docs/protocol.md)
- [安全](docs/safety.md)
- [训练数据](docs/training-data.md)
- [OBS](docs/obs.md)
- [测试](docs/testing.md)

## 许可证

本项目采用 [Apache License 2.0](LICENSE)。
