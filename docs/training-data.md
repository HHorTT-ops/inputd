# 固定帧率训练数据

`inputd` 可以把 Dashboard 或 Raw Input recorder 生成的事件级 JSONL，重采样为固定频率的训练 `ActionFrame`。内置快捷频率为 20、40、60Hz，也支持 1–1000Hz 的任意整数频率。

## 从界面生成

1. 启动 `inputd-dashboard.exe`，完成一次录制，并等待录制状态显示“已完整保存”。
2. 点击“生成训练数据”。
3. 选择源 `.jsonl`、20/40/60Hz 预设或自定义频率及输出文件。
4. 点击“生成训练数据”。生成器会同时创建训练 JSONL 和 `<输出文件>.manifest.json`。

录制过程中不能启动生成，避免读取仍在增长的源文件。`允许源 sequence 断号` 默认关闭；正式训练不应开启它。

## 从命令行生成

发布目录中的 `inputctl.exe` 可离线运行，不要求 `inputd` 服务正在运行：

```powershell
inputctl.exe resample .\episode.jsonl --hz 20
inputctl.exe resample .\episode.jsonl --hz 40
inputctl.exe resample .\episode.jsonl --hz 60
inputctl.exe resample .\episode.jsonl .\episode.37hz.jsonl --hz 37 --overwrite
```

未提供输出路径时，默认文件名为 `<源文件名>.<频率>hz.training.jsonl`。源序列有断号时命令默认失败；只有明确接受不完整历史数据时才可添加 `--allow-gaps`。

## 无事件缺失语义

每帧使用半开时间窗口 `[window_start_us, window_end_us)`。边界时刻的事件进入下一帧。窗口边界用整数有理数计算，因此 60Hz 不会因浮点累计误差产生重叠或空隙。

- 帧内所有鼠标 `dx`、`dy`、`wheel_x`、`wheel_y` 分别求和，使用 64 位整数，正负移动不会相互丢失。
- `keyboard.held` 和 `mouse.held_buttons` 是该帧结束时的期望状态；空帧会继承上一帧状态。
- `pressed`、`released`、`pressed_buttons`、`released_buttons` 保留帧内边沿。
- `transitions` 按源顺序保留每次按下/释放及其帧内 `offset_us` 和 `source_sequence`。因此同一帧内的短点击或短按不会因最终 held 状态相同而消失。
- `source_event_count`、`source_first_sequence`、`source_last_sequence` 可用于逐帧审计源事件覆盖范围。
- 默认要求 `sequence` 从 1 开始并严格连续，同时要求单调时间戳和单一 session。任何异常都会中止并删除临时输出，而不是产出看似成功的不完整文件。

输出采用临时文件生成，完成后再原子替换目标。清单保存源文件与输出文件的 SHA-256、源事件数、输出帧数、空帧数和断号数，可用于训练前完整性校验。

## 帧结构

训练 JSONL 每行是一帧，核心字段如下：

```json
{
  "version": "1.0",
  "episode_id": "inputd-dashboard-preview",
  "frames_per_second": 60,
  "frame_index": 42,
  "window_start_us": 5000700000,
  "window_end_us": 5000716666,
  "duration_us": 16666,
  "source_event_count": 3,
  "source_first_sequence": 87,
  "source_last_sequence": 89,
  "keyboard": {
    "held": ["KeyW"],
    "pressed": ["KeyW"],
    "released": []
  },
  "mouse": {
    "dx": 13,
    "dy": -4,
    "wheel_x": 0,
    "wheel_y": 0,
    "held_buttons": [],
    "pressed_buttons": ["left"],
    "released_buttons": ["left"]
  },
  "transitions": [
    { "offset_us": 1200, "source_sequence": 87, "device": "keyboard", "control": "KeyW", "action": "down" },
    { "offset_us": 4300, "source_sequence": 88, "device": "mouse", "control": "left", "action": "down" },
    { "offset_us": 6900, "source_sequence": 89, "device": "mouse", "control": "left", "action": "up" }
  ]
}
```

对只需要状态策略的模型，可直接使用 `held`、`held_buttons` 和鼠标增量；对需要复现短点击、连点或精确边沿的模型，还应读取 `transitions`。

## 训练前校验

正式训练至少检查：

1. 清单 `sequence_gaps` 为 0。
2. 源文件和输出文件 SHA-256 与清单一致。
3. 所有帧 `frame_index` 连续，前一帧 `window_end_us` 等于后一帧 `window_start_us`。
4. 所有帧 `source_event_count` 之和等于清单 `source_events`。
5. 所有帧鼠标增量总和与源事件总和一致。
