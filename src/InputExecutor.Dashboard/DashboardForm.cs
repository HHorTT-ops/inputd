using System.Diagnostics;
using System.Net.Http.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Scheduler;
using InputExecutor.Windows;

namespace InputExecutor.Dashboard;

internal sealed class DashboardForm : Form
{
    private const string PreviewSessionId = "inputd-dashboard-preview";
    private const int StartRecordingHotkeyId = 0x4951;
    private const int StopRecordingHotkeyId = 0x4952;
    private const int StartPlaybackHotkeyId = 0x4953;
    private const int StopPlaybackHotkeyId = 0x4954;
    private readonly bool _diagnosticDefault;
    private readonly SystemMonotonicClock _clock = new();
    private readonly SwitchableTrajectorySink _trajectory = new(TimeSpan.FromSeconds(3));
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:17841"), Timeout = TimeSpan.FromSeconds(2) };
    private readonly KeyboardView _keyboard = new() { Dock = DockStyle.Fill };
    private readonly MouseView _mouse = new() { Dock = DockStyle.Fill };
    private readonly Label _serviceValue = ValueLabel("未运行");
    private readonly Label _monitorValue = ValueLabel("初始化…");
    private readonly Label _recordingValue = ValueLabel("已停止");
    private readonly Label _eventValue = ValueLabel("0");
    private readonly Label _recordedValue = ValueLabel("0");
    private readonly Label _elapsedValue = ValueLabel("00:00:00");
    private readonly Label _lastInputValue = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 9.5f), TextAlign = ContentAlignment.MiddleLeft, Text = "等待输入…" };
    private readonly Label _targetValue = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "目标：等待切换到游戏窗口" };
    private readonly Label _pathValue = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "尚未创建录制文件" };
    private readonly TextBox _eventLog = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), ForeColor = DashboardTheme.Ink };
    private readonly Button _startService = DashboardTheme.Button("启动 inputd", DashboardTheme.Accent);
    private readonly Button _startRecording = DashboardTheme.Button("● 开始录制", DashboardTheme.Danger);
    private readonly Button _stopRecording = DashboardTheme.Button("■ 停止录制", DashboardTheme.Ink);
    private readonly Button _openFolder = DashboardTheme.Button("打开录制目录", Color.FromArgb(71, 85, 105));
    private readonly Button _generateTraining = DashboardTheme.Button("生成训练数据", DashboardTheme.Success);
    private readonly Button _hotkeySettings = DashboardTheme.Button("快捷键设置", Color.FromArgb(124, 58, 237));
    private readonly Button _clear = DashboardTheme.Button("清空状态", Color.FromArgb(100, 116, 139));
    private readonly Button _selectReplay = DashboardTheme.Button("选择操作记录", Color.FromArgb(14, 116, 144));
    private readonly Button _startPlayback = DashboardTheme.Button("▶ 播放操作", DashboardTheme.Success);
    private readonly Button _stopPlayback = DashboardTheme.Button("■ 停止播放", DashboardTheme.Ink);
    private readonly CheckBox _rawInputReplay = new() { AutoSize = true, Text = "内核鼠标兼容", Checked = false, ForeColor = DashboardTheme.Ink, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Anchor = AnchorStyles.None };
    private readonly Label _replayPathValue = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "尚未选择操作记录" };
    private readonly Label _replayStateValue = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Text = "未播放" };
    private readonly CheckBox _includeInjected = new() { AutoSize = true, Text = "合成输入诊断（游戏录制请关闭）", ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 9f), Margin = new Padding(14, 11, 8, 0) };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 2000 };
    private readonly WindowsTargetGuard _targetGuard = new();
    private readonly ToolTip _toolTip = new();
    private DashboardSettings _settings;
    private readonly string? _settingsWarning;
    private WindowsRawInputRecorder? _recorder;
    private InputScheduler? _replayScheduler;
    private TrajectoryReplayPlayer? _replayPlayer;
    private TargetBinding? _target;
    private DateTimeOffset? _recordingStarted;
    private long _observedEvents;
    private long _lastMouseLogAt;
    private readonly object _previewSync = new();
    private HumanInputFrame? _latestPreviewFrame;
    private long _previewDx;
    private long _previewDy;
    private long _previewWheelX;
    private long _previewWheelY;
    private bool _previewDirty;
    private bool _recording;
    private bool _playing;
    private string? _replayPath;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private bool _closing;
    private bool _hotkeysRegistered;
    private int _hotkeyActionActive;

    internal DashboardForm(bool diagnosticDefault)
    {
        _diagnosticDefault = diagnosticDefault;
        _settings = DashboardSettingsStore.Load(out _settingsWarning);
        Text = "inputd 输入状态与录制中心";
        Name = "InputdDashboard";
        BackColor = DashboardTheme.Window;
        ForeColor = DashboardTheme.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 760);
        ClientSize = new Size(1420, 870);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        WireEvents();
    }

    private void BuildLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = DashboardTheme.Header, Padding = new Padding(24, 14, 20, 12) };
        var title = new Label { Text = "inputd", ForeColor = Color.White, Font = new Font("Segoe UI", 22f, FontStyle.Bold), AutoSize = true, Location = new Point(22, 13) };
        var subtitle = new Label { Text = "INPUT MONITOR & RECORDER", ForeColor = Color.FromArgb(148, 163, 184), Font = new Font("Segoe UI", 8f, FontStyle.Bold), AutoSize = true, Location = new Point(25, 54) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 990, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0), BackColor = Color.Transparent };
        _startService.Name = "StartServiceButton";
        _startRecording.Name = "StartRecordingButton";
        _stopRecording.Name = "StopRecordingButton";
        _openFolder.Name = "OpenRecordingFolderButton";
        _generateTraining.Name = "GenerateTrainingDataButton";
        _hotkeySettings.Name = "HotkeySettingsButton";
        _clear.Name = "ClearStateButton";
        _selectReplay.Name = "SelectReplayFileButton";
        _startPlayback.Name = "StartPlaybackButton";
        _stopPlayback.Name = "StopPlaybackButton";
        _stopRecording.Enabled = false;
        _startPlayback.Enabled = false;
        _stopPlayback.Enabled = false;
        actions.Controls.AddRange([_startService, _startRecording, _stopRecording, _openFolder, _generateTraining, _hotkeySettings, _clear]);
        header.Controls.Add(actions);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 16), ColumnCount = 1, RowCount = 5, BackColor = DashboardTheme.Window };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        Controls.Add(root);

        var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, BackColor = Color.Transparent };
        for (var i = 0; i < 6; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.6667f));
        stats.Controls.Add(StatCard("INPUTD 服务", _serviceValue), 0, 0);
        stats.Controls.Add(StatCard("监视器", _monitorValue), 1, 0);
        stats.Controls.Add(StatCard("录制状态", _recordingValue), 2, 0);
        stats.Controls.Add(StatCard("观察事件", _eventValue), 3, 0);
        stats.Controls.Add(StatCard("已写入事件", _recordedValue), 4, 0);
        stats.Controls.Add(StatCard("录制时长", _elapsedValue), 5, 0);
        root.Controls.Add(stats, 0, 0);

        var devices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        devices.Controls.Add(DeviceCard("键盘实时状态", "按键按下时由白色变为黑色", _keyboard), 0, 0);
        devices.Controls.Add(DeviceCard("鼠标实时状态", "按键、相对位移、滚轮与屏幕位置", _mouse), 1, 0);
        root.Controls.Add(devices, 0, 1);

        var statusBar = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8) };
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = Color.Transparent };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        statusLayout.Controls.Add(_lastInputValue, 0, 0);
        statusLayout.Controls.Add(_targetValue, 1, 0);
        statusLayout.Controls.Add(_pathValue, 2, 0);
        _includeInjected.Checked = _diagnosticDefault;
        statusLayout.Controls.Add(_includeInjected, 3, 0);
        statusBar.Controls.Add(statusLayout);
        root.Controls.Add(statusBar, 0, 2);

        var replayBar = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8) };
        _rawInputReplay.Name = "RawInputReplayCheckBox";
        var replayLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = Color.Transparent };
        replayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        replayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        replayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        replayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        replayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        replayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        replayLayout.Controls.Add(_replayPathValue, 0, 0);
        replayLayout.Controls.Add(_replayStateValue, 1, 0);
        replayLayout.Controls.Add(_rawInputReplay, 2, 0);
        replayLayout.Controls.Add(_selectReplay, 3, 0);
        replayLayout.Controls.Add(_startPlayback, 4, 0);
        replayLayout.Controls.Add(_stopPlayback, 5, 0);
        replayBar.Controls.Add(replayLayout);
        root.Controls.Add(replayBar, 0, 3);

        var logCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12) };
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logLayout.Controls.Add(new Label { Text = "最近输入事件", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = DashboardTheme.Ink }, 0, 0);
        logLayout.Controls.Add(_eventLog, 0, 1);
        logCard.Controls.Add(logLayout);
        root.Controls.Add(logCard, 0, 4);

        // WinForms calculates docking in reverse z-order. Add the top header last so
        // the fill panel reserves its height instead of extending underneath it.
        Controls.Add(header);
    }

    private static CardPanel StatCard(string title, Label value)
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(value, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private static CardPanel DeviceCard(string title, string subtitle, Control content)
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 14) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Ink, Font = new Font("Segoe UI", 12f, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Fill, ForeColor = DashboardTheme.Muted, Font = new Font("Segoe UI", 8.5f) }, 0, 1);
        layout.Controls.Add(content, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private static Label ValueLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = DashboardTheme.Ink,
        Font = new Font("Segoe UI", 13f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private void WireEvents()
    {
        Shown += OnShownAsync;
        FormClosing += OnClosingAsync;
        _startService.Click += async (_, _) => await StartServiceAsync();
        _startRecording.Click += async (_, _) => await StartRecordingAsync();
        _stopRecording.Click += async (_, _) => await StopRecordingAsync();
        _openFolder.Click += (_, _) => OpenRecordingFolder();
        _generateTraining.Click += (_, _) => OpenTrainingDataDialog();
        _hotkeySettings.Click += (_, _) => OpenHotkeySettings();
        _clear.Click += (_, _) => ClearState();
        _selectReplay.Click += async (_, _) => await SelectReplayFileAsync();
        _startPlayback.Click += async (_, _) => await StartPlaybackAsync();
        _stopPlayback.Click += async (_, _) => await StopPlaybackAsync("用户停止");
        _uiTimer.Tick += (_, _) => RefreshCounters();
        _healthTimer.Tick += async (_, _) => await RefreshServiceHealthAsync();
    }

    private async void OnShownAsync(object? sender, EventArgs e)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            _target = new TargetBinding
            {
                WindowHandle = $"0x{Handle.ToInt64():X}",
                ProcessId = Environment.ProcessId,
                ProcessName = process.ProcessName,
                ProcessPath = process.MainModule?.FileName,
                ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                WindowTitle = Text
            };
            _targetValue.Text = "目标：等待切换到游戏窗口";
            _recorder = new WindowsRawInputRecorder(_trajectory, _clock);
            _replayScheduler = new InputScheduler(CreateGameReplayBackend(), _clock);
            _replayPlayer = new TrajectoryReplayPlayer(_replayScheduler, _targetGuard, _clock);
            RefreshRawReplayPresentation();
            _recorder.FrameObserved += RecorderOnFrameObserved;
            _monitorValue.Text = "正在监视";
            _monitorValue.ForeColor = DashboardTheme.Success;
            AppendLog("Raw Input 键盘与鼠标监视器已启动。", DashboardTheme.Success);
            if (_settingsWarning is not null) AppendLog(_settingsWarning, DashboardTheme.Danger);
            if (!TryRegisterHotkeys(_settings, out var hotkeyError))
            {
                AppendLog(hotkeyError!, DashboardTheme.Danger);
                MessageBox.Show(this, hotkeyError, "无法注册录制快捷键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                RefreshHotkeyPresentation();
                AppendLog($"全局快捷键：录制 {_settings.StartRecording.DisplayText}/{_settings.StopRecording.DisplayText}，回放 {_settings.StartPlayback.DisplayText}/{_settings.StopPlayback.DisplayText}。", DashboardTheme.Success);
            }
            _uiTimer.Start();
            _healthTimer.Start();
            await RefreshServiceHealthAsync();
        }
        catch (Exception ex)
        {
            _monitorValue.Text = "启动失败";
            _monitorValue.ForeColor = DashboardTheme.Danger;
            AppendLog("监视器启动失败：" + ex.Message, DashboardTheme.Danger);
            MessageBox.Show(this, ex.Message, "Raw Input 初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RecorderOnFrameObserved(object? sender, HumanInputFrame frame)
    {
        if (IsDisposed || Disposing) return;
        Interlocked.Increment(ref _observedEvents);
        lock (_previewSync)
        {
            _latestPreviewFrame = frame;
            _previewDx += frame.Mouse.Dx;
            _previewDy += frame.Mouse.Dy;
            _previewWheelX += frame.Mouse.WheelX;
            _previewWheelY += frame.Mouse.WheelY;
            _previewDirty = true;
        }
    }

    private void ApplyLatestPreview()
    {
        HumanInputFrame? frame;
        long dx, dy, wheelX, wheelY;
        lock (_previewSync)
        {
            if (!_previewDirty) return;
            frame = _latestPreviewFrame;
            dx = _previewDx;
            dy = _previewDy;
            wheelX = _previewWheelX;
            wheelY = _previewWheelY;
            _previewDx = _previewDy = _previewWheelX = _previewWheelY = 0;
            _previewDirty = false;
        }
        if (frame is null) return;

        var displayDx = ClampToInt(dx);
        var displayDy = ClampToInt(dy);
        var displayWheelX = ClampToInt(wheelX);
        var displayWheelY = ClampToInt(wheelY);
        _keyboard.SetPressed(frame.Keyboard.Held);
        var cursor = Cursor.Position;
        _mouse.UpdateState(frame.Mouse.HeldButtons, displayDx, displayDy, displayWheelX, displayWheelY, cursor);
        var source = frame.InjectedTagged ? "inputd" : "物理";
        if (frame.Device.Contains("keyboard", StringComparison.OrdinalIgnoreCase))
        {
            var action = frame.KeyDown == true ? "按下" : "释放";
            _lastInputValue.Text = $"{source}键盘：{action} {frame.ChangedKey}";
            AppendLog($"{source}键盘  {action,-4} {frame.ChangedKey}", frame.KeyDown == true ? DashboardTheme.Accent : DashboardTheme.Muted);
        }
        else
        {
            _lastInputValue.Text = $"{source}鼠标：Δ({displayDx:+#;-#;0}, {displayDy:+#;-#;0})  按住 [{string.Join(", ", frame.Mouse.HeldButtons)}]";
            var now = Environment.TickCount64;
            if (now - _lastMouseLogAt > 250)
            {
                _lastMouseLogAt = now;
                AppendLog($"{source}鼠标  ΔX={displayDx:+#;-#;0} ΔY={displayDy:+#;-#;0} buttons=[{string.Join(",", frame.Mouse.HeldButtons)}] wheel=({displayWheelX},{displayWheelY})", DashboardTheme.Muted);
            }
        }
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private async Task StartRecordingAsync()
    {
        if (_recording || _playing || _recorder is null || _target is null) return;
        try
        {
            var directory = RecordingDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"inputd-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            await _trajectory.StartAsync(path, _includeInjected.Checked);
            _recorder.Start(PreviewSessionId, _target, includeExecutorEvents: _includeInjected.Checked);
            _recording = true;
            _recordingStarted = DateTimeOffset.Now;
            _startRecording.Enabled = false;
            _stopRecording.Enabled = true;
            _includeInjected.Enabled = false;
            _generateTraining.Enabled = false;
            _hotkeySettings.Enabled = false;
            _selectReplay.Enabled = false;
            _startPlayback.Enabled = false;
            _rawInputReplay.Enabled = false;
            _recordingValue.Text = "● 录制中";
            _recordingValue.ForeColor = DashboardTheme.Danger;
            _pathValue.Text = path;
            AppendLog($"开始录制：{path}", DashboardTheme.Danger);
            AppendLog($"录制目标：{_target.ProcessName} (PID {_target.ProcessId}) · {_target.WindowTitle}", DashboardTheme.Accent);
            AppendLog(_includeInjected.Checked
                ? "诊断模式：临时启用了低级键鼠 Hook；请勿用于正式游戏录制。"
                : "游戏兼容模式：仅使用 Raw Input，不安装全局键鼠 Hook。",
                _includeInjected.Checked ? DashboardTheme.Danger : DashboardTheme.Success);
        }
        catch (Exception ex)
        {
            await _trajectory.StopAsync();
            MessageBox.Show(this, ex.Message, "无法开始录制", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (!_recording || _recorder is null) return;
        try
        {
            _stopRecording.Enabled = false;
            _recordingValue.Text = "正在落盘…";
            _recordingValue.ForeColor = DashboardTheme.Accent;
            var status = await _recorder.StopAsync(PreviewSessionId, CancellationToken.None);
            await _trajectory.StopAsync();
            _recording = false;
            _recordingStarted = null;
            _startRecording.Enabled = true;
            _stopRecording.Enabled = false;
            _includeInjected.Enabled = true;
            _generateTraining.Enabled = true;
            _hotkeySettings.Enabled = true;
            _selectReplay.Enabled = true;
            _startPlayback.Enabled = _replayPath is not null;
            _rawInputReplay.Enabled = true;
            var integrityComplete = !status.Faulted && status.EventsDropped == 0 && status.EventsPending == 0 && status.EventsRecorded == _trajectory.PersistedCount;
            var hasData = _trajectory.PersistedCount > 0;
            var complete = integrityComplete && hasData;
            _recordingValue.Text = complete ? "已完整保存" : integrityComplete ? "无输入数据" : "写入异常";
            _recordingValue.ForeColor = complete ? DashboardTheme.Success : DashboardTheme.Danger;
            _keyboard.SetPressed([]);
            _mouse.ClearState(Cursor.Position);
            AppendLog($"录制已停止：写入 {_trajectory.PersistedCount} 条，待写 {status.EventsPending} 条，失败 {status.EventsDropped} 条。",
                complete ? DashboardTheme.Success : DashboardTheme.Danger);
            if (!hasData)
                MessageBox.Show(this, "本轮录制没有捕获到任何输入，已保留空文件但不能用于训练。请确认游戏与录制器权限一致，并在开始与停止之间实际操作游戏。", "未捕获输入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (!integrityComplete)
                MessageBox.Show(this, status.LastError ?? "录制完整性检查失败；请勿将该文件直接用于训练。", "录制写入异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            AppendLog("停止录制失败：" + ex.Message, DashboardTheme.Danger);
        }
    }

    private async Task SelectReplayFileAsync()
    {
        if (_recording || _playing || _replayPlayer is null) return;
        using var dialog = new OpenFileDialog
        {
            Title = "选择要回放的 inputd 操作记录",
            Filter = "inputd 操作记录 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(RecordingDirectory()) ? RecordingDirectory() : null
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _selectReplay.Enabled = false;
        _startPlayback.Enabled = false;
        _replayStateValue.Text = "正在校验…";
        try
        {
            var summary = await _replayPlayer.ValidateAsync(dialog.FileName);
            _replayPath = summary.Path;
            _replayPathValue.Text = $"操作记录：{summary.Path}";
            _toolTip.SetToolTip(_replayPathValue, summary.Path);
            _replayStateValue.Text = $"{summary.EventCount:N0} 条";
            _replayStateValue.ForeColor = DashboardTheme.Success;
            _startPlayback.Enabled = true;
            var durationText = TimeSpan.FromMilliseconds(summary.DurationMicroseconds / 1000d).ToString(@"hh\:mm\:ss\.fff");
            AppendLog($"操作记录校验通过：{summary.EventCount:N0} 条，时长 {durationText}；鼠标移动 {summary.MouseMoveEventCount:N0} 条，累计 ΔX={summary.TotalMouseDx:+#;-#;0} ΔY={summary.TotalMouseDy:+#;-#;0}。", DashboardTheme.Success);
        }
        catch (Exception ex)
        {
            _replayPath = null;
            _replayPathValue.Text = "所选操作记录无效";
            _replayStateValue.Text = "校验失败";
            _replayStateValue.ForeColor = DashboardTheme.Danger;
            MessageBox.Show(this, ex.Message, "无法回放操作记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _selectReplay.Enabled = true; }
    }

    private async Task StartPlaybackAsync()
    {
        if (_recording || _playing || _replayPlayer is null || _replayScheduler is null || _replayPath is null) return;
        if (_target is null || !_targetGuard.Validate(_target, requireForeground: false).IsValid)
        {
            MessageBox.Show(this, "请先切换到要接收回放操作的游戏窗口，再返回并选择播放；也可以在游戏获得焦点时使用播放快捷键。", "没有有效目标窗口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!await ConfigureReplayBackendAsync()) return;
        if (!_targetGuard.TryActivate(_target))
        {
            MessageBox.Show(this, "无法把已记住的游戏窗口切换到前台。为避免操作发送到错误窗口，回放没有开始。", "目标窗口未获得焦点", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _playing = true;
        _playbackCancellation = new CancellationTokenSource();
        _startPlayback.Enabled = false;
        _stopPlayback.Enabled = true;
        _selectReplay.Enabled = false;
        _startRecording.Enabled = false;
        _hotkeySettings.Enabled = false;
        _rawInputReplay.Enabled = false;
        _replayStateValue.Text = "播放中 0%";
        _replayStateValue.ForeColor = DashboardTheme.Accent;
        AppendLog($"开始回放：{_replayPath}", DashboardTheme.Accent);
        AppendLog($"回放目标：{_target.ProcessName} (PID {_target.ProcessId}) · {_target.WindowTitle}", DashboardTheme.Accent);
        _playbackTask = RunPlaybackAsync(_replayPath, _target, _playbackCancellation.Token);
    }

    private async Task RunPlaybackAsync(string path, TargetBinding target, CancellationToken cancellationToken)
    {
        var lastUiUpdate = 0L;
        var progress = new CallbackProgress<TrajectoryReplayProgress>(value =>
        {
            var now = Environment.TickCount64;
            if (value.EventsPlayed != value.EventCount && now - Interlocked.Read(ref lastUiUpdate) < 100) return;
            Interlocked.Exchange(ref lastUiUpdate, now);
            if (IsDisposed || Disposing) return;
            BeginInvoke(new Action(() =>
            {
                if (!_playing) return;
                var percent = value.EventCount == 0 ? 0 : value.EventsPlayed * 100d / value.EventCount;
                _replayStateValue.Text = $"播放中 {percent:0}%";
            }));
        });

        try
        {
            var summary = await Task.Run(() => _replayPlayer!.ReplayAsync(path, target, progress, cancellationToken), cancellationToken);
            AppendLog($"回放完成：已执行 {summary.EventCount:N0} 条源操作；鼠标累计 ΔX={summary.TotalMouseDx:+#;-#;0} ΔY={summary.TotalMouseDy:+#;-#;0}。", DashboardTheme.Success);
            _replayStateValue.Text = "播放完成";
            _replayStateValue.ForeColor = DashboardTheme.Success;
        }
        catch (OperationCanceledException)
        {
            AppendLog("回放已停止，所有回放按键与鼠标按钮均已释放。", DashboardTheme.Muted);
            _replayStateValue.Text = "已停止";
            _replayStateValue.ForeColor = DashboardTheme.Muted;
        }
        catch (Exception ex)
        {
            AppendLog("回放中止：" + ex.Message, DashboardTheme.Danger);
            _replayStateValue.Text = "回放中止";
            _replayStateValue.ForeColor = DashboardTheme.Danger;
            if (!_closing) MessageBox.Show(this, ex.Message, "回放已安全中止", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _playing = false;
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
            _stopPlayback.Enabled = false;
            _selectReplay.Enabled = !_closing;
            _startPlayback.Enabled = !_closing && _replayPath is not null;
            _startRecording.Enabled = !_closing;
            _hotkeySettings.Enabled = !_closing;
            _rawInputReplay.Enabled = !_closing;
        }
    }

    private async Task<bool> ConfigureReplayBackendAsync()
    {
        IInputBackend backend = CreateGameReplayBackend();
        if (_rawInputReplay.Checked)
        {
            try { backend = new WindowsInterceptionInputBackend(backend, _clock); }
            catch (Exception ex)
            {
                AppendLog("内核鼠标兼容后端不可用：" + ex.Message, DashboardTheme.Danger);
                MessageBox.Show(this,
                    ex.Message + Environment.NewLine + Environment.NewLine +
                    "请取消勾选“内核鼠标兼容”，使用默认的管理员权限游戏回放后端。默认模式通过 mouse_event 回放相对镜头，其他输入仍使用 SendInput；请在离线或明确允许的目标中自行验证兼容性。",
                    "内核鼠标兼容后端不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (backend is IDisposable disposable) disposable.Dispose();
                return false;
            }
        }

        var previous = _replayScheduler;
        _replayScheduler = new InputScheduler(backend, _clock);
        _replayPlayer = new TrajectoryReplayPlayer(_replayScheduler, _targetGuard, _clock);
        if (previous is not null) await previous.DisposeAsync();
        AppendLog($"回放输入后端：{_replayScheduler.BackendName}", _rawInputReplay.Checked ? DashboardTheme.Success : DashboardTheme.Muted);
        return true;
    }

    private WindowsLegacyMouseReplayBackend CreateGameReplayBackend() =>
        new(new WindowsSendInputBackend(_clock, new WindowsSendInputOptions(TagInjectedEvents: false)), _clock);

    private void RefreshRawReplayPresentation()
    {
        var libraryPath = Path.Combine(AppContext.BaseDirectory, "interception.dll");
        _rawInputReplay.Text = File.Exists(libraryPath) ? "内核鼠标兼容" : "内核鼠标兼容（未安装）";
        _toolTip.SetToolTip(_rawInputReplay,
            "默认不要勾选；相对镜头使用管理员权限 mouse_event，键盘、点击和绝对坐标使用 SendInput。仅当特定游戏仍拒绝时，才在明确授权后安装兼容驱动与 x64 interception.dll。");
    }

    private async Task StopPlaybackAsync(string reason)
    {
        if (!_playing) return;
        var task = _playbackTask;
        _playbackCancellation?.Cancel();
        try { if (_replayScheduler is not null) await _replayScheduler.ReleaseAllAndClearAsync(reason); }
        catch { }
        try { if (task is not null) await task; }
        catch { }
    }

    private void RefreshCounters()
    {
        RefreshExternalTarget();
        ApplyLatestPreview();
        _eventValue.Text = Interlocked.Read(ref _observedEvents).ToString("N0");
        _recordedValue.Text = _trajectory.PersistedCount.ToString("N0");
        _elapsedValue.Text = _recordingStarted is null ? "00:00:00" : (DateTimeOffset.Now - _recordingStarted.Value).ToString(@"hh\:mm\:ss");
        if (_recording && _recorder is not null)
        {
            var status = _recorder.Status;
            _recordingValue.Text = status.Faulted ? "● 写入异常" : status.EventsPending > 0 ? $"● 录制中 · {status.EventsPending:N0}" : "● 录制中";
            _recordingValue.ForeColor = DashboardTheme.Danger;
        }
        if (_playing && WindowsGlobalHotkeys.IsKeyDown((int)Keys.ControlKey) &&
            WindowsGlobalHotkeys.IsKeyDown((int)Keys.Menu) &&
            WindowsGlobalHotkeys.IsKeyDown((int)Keys.F12))
            _ = StopPlaybackAsync("紧急停止");
    }

    private void RefreshExternalTarget()
    {
        if (_recording || _playing) return;
        TryCaptureExternalTarget();
    }

    private bool TryCaptureExternalTarget()
    {
        try
        {
            var candidate = _targetGuard.CaptureForeground();
            if (candidate.ProcessId == Environment.ProcessId) return false;
            if (_target?.ProcessId == candidate.ProcessId &&
                string.Equals(_target.WindowHandle, candidate.WindowHandle, StringComparison.OrdinalIgnoreCase)) return true;
            _target = candidate;
            _targetValue.Text = $"目标：{candidate.ProcessName} · {candidate.WindowTitle}";
            AppendLog($"已记住外部目标窗口：{candidate.ProcessName} (PID {candidate.ProcessId}) · {candidate.WindowTitle}", DashboardTheme.Accent);
            return true;
        }
        catch
        {
            // A transient shell/secure-desktop foreground must not interrupt recording preview.
            return false;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WindowsGlobalHotkeys.MessageId && !_closing)
        {
            var id = message.WParam.ToInt32();
            if (id is StartRecordingHotkeyId or StopRecordingHotkeyId or StartPlaybackHotkeyId or StopPlaybackHotkeyId)
                BeginInvoke(new Action(() => _ = HandleHotkeyAsync(id)));
        }
        base.WndProc(ref message);
    }

    private async Task HandleHotkeyAsync(int id)
    {
        if (Interlocked.Exchange(ref _hotkeyActionActive, 1) != 0) return;
        try
        {
            if (id == StartRecordingHotkeyId)
            {
                if (_recording)
                {
                    AppendLog("开始录制快捷键已忽略：当前已经在录制。", DashboardTheme.Muted);
                    return;
                }
                if (_playing)
                {
                    AppendLog("开始录制快捷键已忽略：当前正在回放。", DashboardTheme.Muted);
                    return;
                }
                TryCaptureExternalTarget();
                var gesture = _settings.StartRecording;
                AppendLog($"检测到开始录制快捷键 {gesture.DisplayText}，等待按键释放。", DashboardTheme.Accent);
                if (!await WaitForHotkeyReleaseAsync(gesture)) return;
                await StartRecordingAsync();
            }
            else if (id == StopRecordingHotkeyId)
            {
                if (!_recording)
                {
                    AppendLog("停止录制快捷键已忽略：当前没有录制。", DashboardTheme.Muted);
                    return;
                }
                var gesture = _settings.StopRecording;
                var hotkeyTimestampUs = _clock.NowMicroseconds;
                await _trajectory.BeginShortcutSuppressionAsync(gesture.PhysicalCodesToSuppress(), hotkeyTimestampUs);
                AppendLog($"检测到停止录制快捷键 {gesture.DisplayText}，等待按键释放后安全落盘。", DashboardTheme.Accent);
                var released = await WaitForHotkeyReleaseAsync(gesture);
                await _trajectory.CompleteShortcutSuppressionAsync(hotkeyTimestampUs, _clock.NowMicroseconds);
                if (!released) return;
                await StopRecordingAsync();
            }
            else if (id == StartPlaybackHotkeyId)
            {
                if (_recording || _playing || _replayPath is null)
                {
                    AppendLog("播放快捷键已忽略：请先选择操作记录，并确保当前没有录制或回放。", DashboardTheme.Muted);
                    return;
                }
                TryCaptureExternalTarget();
                var gesture = _settings.StartPlayback;
                AppendLog($"检测到播放快捷键 {gesture.DisplayText}，等待按键释放。", DashboardTheme.Accent);
                if (!await WaitForHotkeyReleaseAsync(gesture)) return;
                await StartPlaybackAsync();
            }
            else
            {
                if (!_playing)
                {
                    AppendLog("停止播放快捷键已忽略：当前没有回放。", DashboardTheme.Muted);
                    return;
                }
                var gesture = _settings.StopPlayback;
                if (!await WaitForHotkeyReleaseAsync(gesture)) return;
                await StopPlaybackAsync("停止播放快捷键");
            }
        }
        catch (Exception ex)
        {
            AppendLog("处理全局快捷键失败：" + ex.Message, DashboardTheme.Danger);
        }
        finally { Interlocked.Exchange(ref _hotkeyActionActive, 0); }
    }

    private async Task<bool> WaitForHotkeyReleaseAsync(DashboardHotkeyGesture gesture)
    {
        var keys = gesture.KeysToRelease();
        while (!_closing)
        {
            if (keys.All(key => !WindowsGlobalHotkeys.IsKeyDown(key)))
            {
                // Let the Raw Input message thread consume the matching key-up packets
                // before opening or closing the trajectory boundary.
                await Task.Delay(50);
                if (keys.All(key => !WindowsGlobalHotkeys.IsKeyDown(key))) return true;
            }
            await Task.Delay(10);
        }
        return false;
    }

    private void OpenHotkeySettings()
    {
        var previous = _settings;
        UnregisterHotkeys();
        using var dialog = new HotkeySettingsDialog(previous);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            if (!TryRegisterHotkeys(previous, out var restoreError)) ShowHotkeyError(restoreError!);
            return;
        }

        var candidate = dialog.Settings;
        if (!TryRegisterHotkeys(candidate, out var registrationError))
        {
            ShowHotkeyError(registrationError!);
            if (!TryRegisterHotkeys(previous, out var restoreError)) ShowHotkeyError(restoreError!);
            return;
        }

        try
        {
            DashboardSettingsStore.Save(candidate);
            _settings = candidate;
            RefreshHotkeyPresentation();
            AppendLog($"快捷键已更新：录制 {candidate.StartRecording.DisplayText}/{candidate.StopRecording.DisplayText}，回放 {candidate.StartPlayback.DisplayText}/{candidate.StopPlayback.DisplayText}。", DashboardTheme.Success);
        }
        catch (Exception ex)
        {
            UnregisterHotkeys();
            TryRegisterHotkeys(previous, out _);
            ShowHotkeyError("保存快捷键设置失败：" + ex.Message);
        }
    }

    private bool TryRegisterHotkeys(DashboardSettings settings, out string? error)
    {
        error = DashboardSettings.Validate(settings);
        if (error is not null) return false;
        UnregisterHotkeys();
        var registered = new List<int>(4);
        try
        {
            WindowsGlobalHotkeys.Register(Handle, StartRecordingHotkeyId, settings.StartRecording.Modifiers, settings.StartRecording.VirtualKey);
            registered.Add(StartRecordingHotkeyId);
            WindowsGlobalHotkeys.Register(Handle, StopRecordingHotkeyId, settings.StopRecording.Modifiers, settings.StopRecording.VirtualKey);
            registered.Add(StopRecordingHotkeyId);
            WindowsGlobalHotkeys.Register(Handle, StartPlaybackHotkeyId, settings.StartPlayback.Modifiers, settings.StartPlayback.VirtualKey);
            registered.Add(StartPlaybackHotkeyId);
            WindowsGlobalHotkeys.Register(Handle, StopPlaybackHotkeyId, settings.StopPlayback.Modifiers, settings.StopPlayback.VirtualKey);
            registered.Add(StopPlaybackHotkeyId);
            _hotkeysRegistered = true;
            return true;
        }
        catch (Exception ex)
        {
            foreach (var registeredId in registered) WindowsGlobalHotkeys.Unregister(Handle, registeredId);
            error = $"全局快捷键注册失败：{ex.Message}";
            return false;
        }
    }

    private void UnregisterHotkeys()
    {
        if (!_hotkeysRegistered && !IsHandleCreated) return;
        WindowsGlobalHotkeys.Unregister(Handle, StartRecordingHotkeyId);
        WindowsGlobalHotkeys.Unregister(Handle, StopRecordingHotkeyId);
        WindowsGlobalHotkeys.Unregister(Handle, StartPlaybackHotkeyId);
        WindowsGlobalHotkeys.Unregister(Handle, StopPlaybackHotkeyId);
        _hotkeysRegistered = false;
    }

    private void RefreshHotkeyPresentation()
    {
        _toolTip.SetToolTip(_startRecording, $"全局快捷键：{_settings.StartRecording.DisplayText}");
        _toolTip.SetToolTip(_stopRecording, $"全局快捷键：{_settings.StopRecording.DisplayText}");
        _toolTip.SetToolTip(_startPlayback, $"全局快捷键：{_settings.StartPlayback.DisplayText}");
        _toolTip.SetToolTip(_stopPlayback, $"全局快捷键：{_settings.StopPlayback.DisplayText}");
        _toolTip.SetToolTip(_hotkeySettings,
            $"开始录制：{_settings.StartRecording.DisplayText}{Environment.NewLine}停止录制：{_settings.StopRecording.DisplayText}{Environment.NewLine}播放：{_settings.StartPlayback.DisplayText}{Environment.NewLine}停止播放：{_settings.StopPlayback.DisplayText}");
    }

    private void ShowHotkeyError(string message)
    {
        AppendLog(message, DashboardTheme.Danger);
        MessageBox.Show(this, message, "快捷键设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task RefreshServiceHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync("/health");
            response.EnsureSuccessStatusCode();
            var health = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            _serviceValue.Text = "运行中";
            _serviceValue.ForeColor = DashboardTheme.Success;
            _startService.Enabled = false;
            _startService.Text = "inputd 已运行";
            _ = health;
        }
        catch
        {
            _serviceValue.Text = "未运行";
            _serviceValue.ForeColor = DashboardTheme.Danger;
            _startService.Enabled = true;
            _startService.Text = "启动 inputd";
        }
    }

    private async Task StartServiceAsync()
    {
        _startService.Enabled = false;
        try
        {
            var executable = FindInputdExecutable();
            if (executable is null) throw new FileNotFoundException("未找到 inputd.exe。请将 inputd.exe 与 Dashboard 放在同一目录，或设置 INPUTD_EXE。 ");
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                try
                {
                    using var response = await _http.GetAsync("/health");
                    if (response.IsSuccessStatusCode) break;
                }
                catch { }
            }
            await RefreshServiceHealthAsync();
            AppendLog("inputd 服务启动请求已完成。", DashboardTheme.Success);
        }
        catch (Exception ex)
        {
            _startService.Enabled = true;
            AppendLog("启动服务失败：" + ex.Message, DashboardTheme.Danger);
            MessageBox.Show(this, ex.Message, "无法启动 inputd", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? FindInputdExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("INPUTD_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var adjacent = Path.Combine(AppContext.BaseDirectory, "inputd.exe");
        if (File.Exists(adjacent)) return adjacent;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "artifacts", "inputd-win-x64", "inputd.exe");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static string RecordingDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("INPUTD_DASHBOARD_RECORDING_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "inputd-recordings");
    }

    private void OpenRecordingFolder()
    {
        var directory = RecordingDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void OpenTrainingDataDialog()
    {
        var suggested = _trajectory.CurrentPath;
        using var dialog = new TrainingDataDialog(!string.IsNullOrWhiteSpace(suggested) && File.Exists(suggested) ? suggested : null);
        dialog.ShowDialog(this);
    }

    private void ClearState()
    {
        Interlocked.Exchange(ref _observedEvents, 0);
        lock (_previewSync)
        {
            _latestPreviewFrame = null;
            _previewDx = _previewDy = _previewWheelX = _previewWheelY = 0;
            _previewDirty = false;
        }
        _eventLog.Clear();
        _keyboard.SetPressed([]);
        _mouse.ClearState(Cursor.Position);
        _lastInputValue.Text = "等待输入…";
    }

    private void AppendLog(string message, Color color)
    {
        _ = color;
        _eventLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        if (_eventLog.TextLength > 16_000) _eventLog.Text = _eventLog.Text[^12_000..];
        _eventLog.SelectionStart = _eventLog.TextLength;
        _eventLog.ScrollToCaret();
    }

    private async void OnClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        UnregisterHotkeys();
        _uiTimer.Stop();
        _healthTimer.Stop();
        if (_playing) await StopPlaybackAsync("程序关闭");
        if (_recording) await StopRecordingAsync();
        if (_recorder is not null)
        {
            _recorder.FrameObserved -= RecorderOnFrameObserved;
            await _recorder.DisposeAsync();
        }
        if (_replayScheduler is not null) await _replayScheduler.DisposeAsync();
        await _trajectory.DisposeAsync();
        _http.Dispose();
        _toolTip.Dispose();
        e.Cancel = false;
        BeginInvoke(Close);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
