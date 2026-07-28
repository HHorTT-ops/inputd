# OBS 配对操作录制

[简体中文](obs.md) | [English](obs.en.md) | [日本語](obs.ja.md)

## 输出结果

OBS 扩展跟随官方前端录制生命周期，为每段完成的视频生成一份操作轨迹。JSONL 和完整性清单与视频位于同一目录并使用同一文件名主体：

```text
D:\recordings\boss-fight.mkv
D:\recordings\boss-fight.input.jsonl
D:\recordings\boss-fight.input.jsonl.manifest.json
```

清单记录视频和轨迹的绝对路径、开始/停止 UTC 时间、事件数量、待写/丢弃数量、终止原因以及轨迹 SHA-256。只有待写和丢弃事件均为零时，文件才会标记为完整。

## 为什么使用 OBS 脚本和独立进程

OBS 官方支持 Lua 自动化和前端事件回调。Lua 扩展只负责通知录制生命周期；独立的 `net10.0-windows` bridge 负责 Raw Input 窗口、有序记录通道、批量持久化写入，以及仅允许当前用户访问的命名管道。

OBS 30 在 `RECORDING_STARTED` 时可能只能返回配置的输出目录，因此采集会立即写入：

```text
%LOCALAPPDATA%\inputd\obs-spool
```

收到 `RECORDING_STOPPED` 后，Lua 通过 `obs_frontend_get_last_recording()` 得到实际视频路径，bridge 排空记录并把轨迹原子移动到相同目录和文件名主体。若 OBS 或 bridge 提前退出，`*.partial` 和不完整清单会保留在 spool 中以便恢复，不会被当成完整训练数据。

停止通知在 bridge 接受最终化请求后即可返回。排空队列、计算 SHA-256 和移动文件继续在 bridge 中进行，因此大型输入轨迹不会阻塞 OBS 前端回调。

## 构建与安装

在仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-obs-plugin.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1
```

然后在 **OBS > 工具 > 脚本 > +** 中添加：

```text
%APPDATA%\obs-studio\scripts\inputd-obs\inputd-obs.lua
```

无人值守注册需要先关闭 OBS：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-obs-plugin.ps1 -Register
```

注册脚本会为当前场景集合创建带时间戳的备份，只原子更新 `modules["scripts-tool"]`，重复运行不会加入重复条目。

## 完整性与权限

- OBS 必须与目标游戏处于相同或更高的 Windows 完整性级别；bridge 继承 OBS 的令牌。
- 命名管道使用 `PipeOptions.CurrentUserOnly`，不监听 TCP 端口。
- 生产录制排除带 inputd 标记的合成执行器事件。
- 只有精确入队水位全部排空并 Flush 后，最终 JSONL 才会出现。
- 文件名冲突会明确失败并保留 partial，不会静默覆盖已有训练文件。

官方参考：[OBS frontend API](https://docs.obsproject.com/reference-frontend-api) 与 [OBS scripting guide](https://docs.obsproject.com/scripting)。
