namespace InputExecutor.Dashboard;

internal sealed class HotkeySettingsDialog : Form
{
    private readonly TextBox _start = CaptureBox();
    private readonly TextBox _stop = CaptureBox();
    private readonly TextBox _play = CaptureBox();
    private readonly TextBox _stopPlay = CaptureBox();
    private readonly Label _error = new() { Dock = DockStyle.Fill, ForeColor = DashboardTheme.Danger, TextAlign = ContentAlignment.MiddleLeft };
    private DashboardHotkeyGesture _startGesture;
    private DashboardHotkeyGesture _stopGesture;
    private DashboardHotkeyGesture _playGesture;
    private DashboardHotkeyGesture _stopPlayGesture;

    internal HotkeySettingsDialog(DashboardSettings settings)
    {
        _startGesture = settings.StartRecording;
        _stopGesture = settings.StopRecording;
        _playGesture = settings.StartPlayback;
        _stopPlayGesture = settings.StopPlayback;
        _start.Name = "StartRecordingHotkeyBox";
        _stop.Name = "StopRecordingHotkeyBox";
        _play.Name = "StartPlaybackHotkeyBox";
        _stopPlay.Name = "StopPlaybackHotkeyBox";
        Settings = settings;
        Text = "录制与回放快捷键设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(600, 420);
        BackColor = DashboardTheme.Window;
        Font = new Font("Segoe UI", 9.5f);
        BuildLayout();
        UpdateBoxes();
    }

    internal DashboardSettings Settings { get; private set; }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 18),
            ColumnCount = 2,
            RowCount = 8,
            BackColor = DashboardTheme.Window
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var instruction = new Label
        {
            Text = "点击输入框后直接按下新的组合键。快捷键在游戏获得焦点时仍然有效。",
            Dock = DockStyle.Fill,
            ForeColor = DashboardTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(instruction, 0, 0);
        root.SetColumnSpan(instruction, 2);
        root.Controls.Add(LabelFor("开始录制"), 0, 1);
        root.Controls.Add(_start, 1, 1);
        root.Controls.Add(LabelFor("停止录制"), 0, 2);
        root.Controls.Add(_stop, 1, 2);
        root.Controls.Add(LabelFor("播放操作"), 0, 3);
        root.Controls.Add(_play, 1, 3);
        root.Controls.Add(LabelFor("停止播放"), 0, 4);
        root.Controls.Add(_stopPlay, 1, 4);

        var note = new Label
        {
            Text = "提示：Ctrl + Alt + F12 保留为 inputd 紧急停止键。录制快捷键会从轨迹中剔除；停止播放会释放回放按住的全部键。",
            Dock = DockStyle.Fill,
            ForeColor = DashboardTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(note, 0, 5);
        root.SetColumnSpan(note, 2);
        root.Controls.Add(_error, 0, 6);
        root.SetColumnSpan(_error, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = DashboardTheme.Button("保存", DashboardTheme.Accent);
        var cancel = DashboardTheme.Button("取消", Color.FromArgb(100, 116, 139));
        var defaults = DashboardTheme.Button("恢复默认", DashboardTheme.Ink);
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        defaults.Click += (_, _) =>
        {
            _startGesture = DashboardSettings.Default.StartRecording;
            _stopGesture = DashboardSettings.Default.StopRecording;
            _playGesture = DashboardSettings.Default.StartPlayback;
            _stopPlayGesture = DashboardSettings.Default.StopPlayback;
            _error.Text = string.Empty;
            UpdateBoxes();
        };
        buttons.Controls.AddRange([save, cancel, defaults]);
        root.Controls.Add(buttons, 0, 7);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        _start.KeyDown += (_, e) => CaptureGesture(e, isStart: true);
        _stop.KeyDown += (_, e) => CaptureGesture(e, isStart: false);
        _play.KeyDown += (_, e) => CaptureGesture(e, HotkeyField.Play);
        _stopPlay.KeyDown += (_, e) => CaptureGesture(e, HotkeyField.StopPlay);
        _start.Enter += (_, _) => _start.SelectAll();
        _stop.Enter += (_, _) => _stop.SelectAll();
        _play.Enter += (_, _) => _play.SelectAll();
        _stopPlay.Enter += (_, _) => _stopPlay.SelectAll();
    }

    private void CaptureGesture(KeyEventArgs e, bool isStart)
        => CaptureGesture(e, isStart ? HotkeyField.Start : HotkeyField.Stop);

    private void CaptureGesture(KeyEventArgs e, HotkeyField field)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (DashboardHotkeyGesture.IsModifierOnly(e.KeyCode))
        {
            Box(field).Text = "继续按下一个主键…";
            return;
        }
        var gesture = DashboardHotkeyGesture.FromKeyEvent(e);
        switch (field)
        {
            case HotkeyField.Start: _startGesture = gesture; break;
            case HotkeyField.Stop: _stopGesture = gesture; break;
            case HotkeyField.Play: _playGesture = gesture; break;
            case HotkeyField.StopPlay: _stopPlayGesture = gesture; break;
        }
        _error.Text = string.Empty;
        UpdateBoxes();
    }

    private void SaveAndClose()
    {
        var candidate = new DashboardSettings
        {
            StartRecording = _startGesture,
            StopRecording = _stopGesture,
            StartPlayback = _playGesture,
            StopPlayback = _stopPlayGesture
        };
        var validation = DashboardSettings.Validate(candidate);
        if (validation is not null)
        {
            _error.Text = validation;
            return;
        }
        Settings = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateBoxes()
    {
        _start.Text = _startGesture.DisplayText;
        _stop.Text = _stopGesture.DisplayText;
        _play.Text = _playGesture.DisplayText;
        _stopPlay.Text = _stopPlayGesture.DisplayText;
    }

    private TextBox Box(HotkeyField field) => field switch
    {
        HotkeyField.Start => _start,
        HotkeyField.Stop => _stop,
        HotkeyField.Play => _play,
        _ => _stopPlay
    };

    private enum HotkeyField { Start, Stop, Play, StopPlay }

    private static TextBox CaptureBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        ShortcutsEnabled = false,
        BackColor = Color.White,
        ForeColor = DashboardTheme.Ink,
        Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        TextAlign = HorizontalAlignment.Center,
        Margin = new Padding(0, 7, 0, 7),
        Cursor = Cursors.Hand,
        TabStop = true
    };

    private static Label LabelFor(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = DashboardTheme.Ink,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft
    };
}
