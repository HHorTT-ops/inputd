# 鼠标回放兼容后端

## 为什么桌面光标会动，游戏视角却不动

操作记录中的鼠标 `dx/dy` 是物理 Raw Input count。Windows `SendInput` 的相对移动可以改变桌面光标位置，普通 Raw Input 窗口也能收到精确增量；但部分游戏不会把这条 `SendInput` 相对移动路径用于镜头。默认 Dashboard 回放因此只把相对移动切换到 Win32 `mouse_event`，并继续以相同的有符号 count、相对时间和 held 状态执行。实际兼容性仍需在离线或目标明确允许的环境中验证。

播放器仍会把 held 状态相同、时间跨度不超过 2ms 的高轮询率微小鼠标包合并后注入。该处理严格保留每个源事件和 `dx/dy/wheel` 的有符号总和，解决 `±1` 位移被 Windows 指针速度舍入的问题；它不能改变目标游戏对合成输入来源的接受策略。

## Dashboard 的两种模式

- 默认不勾选“内核鼠标兼容”：相对移动使用 `WindowsLegacyMouseReplayBackend`，其余动作委托给无自定义标记的 `WindowsSendInputBackend`。这是当前目标游戏已经完成真实回放验证的推荐模式。
- 勾选“内核鼠标兼容”：相对移动、鼠标按钮和滚轮使用 `WindowsInterceptionInputBackend`。它只供其他仍拒绝默认路径的游戏做显式兼容测试；缺少运行库、驱动、可用鼠标设备或驱动拒绝报告时，播放会在任何输入注入前明确失败。

无论使用哪个后端，播放都只能由独立 `InputScheduler` 线程串行执行。停止播放、目标失焦、目标进程退出、程序关闭或紧急停止会清空队列并执行 `release_all`。

## Interception 部署边界

仓库只提供适配器，不捆绑或自动安装第三方 `Interception` 内核驱动和 DLL。启用前需要：

1. 用户明确批准在 Windows 安装第三方鼠标类过滤驱动；
2. 使用管理员权限安装驱动，并按安装程序要求重新启动；
3. 把与发布架构匹配的 x64 `interception.dll` 放在 `inputd-dashboard.exe` 同一目录；
4. 重新打开 Dashboard，确认选项不再显示“未安装”。

程序通过 `DefaultDllImportSearchPaths(ApplicationDirectory)` 只从应用目录加载 DLL，不从当前工作目录或 `PATH` 搜索同名文件。驱动或 DLL 不能使用时，程序不会退回到一个会产生假成功结果的游戏回放模式。

Interception 的公开版本较旧，公开说明主要覆盖 Windows 10；非商业和商业许可条件不同。安装前应自行审核其源代码、发布文件和许可证。受保护联网游戏还可能禁止此类驱动，或由反作弊系统阻止运行。本项目不提供绕过反作弊的功能；请先确认游戏服务条款并只在允许的测试环境中使用。

## 长期生产方案

长期可控的方案是为 inputd 开发并签名专用的 Windows Virtual HID Framework (VHF) 内核驱动。VHF 的 HID source 端只能运行在内核模式，需要 WDK、驱动签名、安装包和重启测试。该路线能减少第三方依赖，但不属于无需授权即可修改系统的普通应用更新。
