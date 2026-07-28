# inputd OBS 配对输入插件

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md)

这个 OBS Lua 扩展监听官方前端录制生命周期：

- `RECORDING_STARTED`：启动随插件发布的 .NET 10 Raw Input bridge；
- `RECORDING_STOPPED`：排空完整采集队列，并在视频旁生成输入文件。

```text
recording.mkv
recording.input.jsonl
recording.input.jsonl.manifest.json
```

OBS 脚本线程只通过当前用户专用的 Windows 命名管道交换很小的 JSON 消息。Raw Input 采集和持久化 JSONL 写入由独立的 `inputd-obs-bridge.exe` 进程完成。

OBS 30 开始录制时可能只返回配置目录，因此活动数据先写入：

```text
%LOCALAPPDATA%\inputd\obs-spool
```

OBS 报告最终视频路径后，bridge 会把轨迹原子移动到精确匹配的视频文件名主体。中断的会话保留为 `*.partial` 和不完整清单，不会被当作完整训练轨迹。停止最终化会先确认接收，再在 bridge 内排空和计算哈希，因此大型输入文件不会卡住 OBS 前端。

通过 **工具 > 脚本 > +** 安装 `inputd-obs.lua`。使用默认 helper 路径，或选择发布包内的 `bridge\inputd-obs-bridge.exe`。录制管理员权限游戏时，OBS 和 bridge 必须处于相同或更高完整性级别。

更完整的构建、安装和完整性说明见 [`docs/obs.md`](../../docs/obs.md)。
