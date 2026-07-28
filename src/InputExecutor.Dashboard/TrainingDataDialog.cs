using System.Diagnostics;
using InputExecutor.Core.Training;

namespace InputExecutor.Dashboard;

internal sealed class TrainingDataDialog : Form
{
    private readonly TextBox _source = new() { Dock = DockStyle.Fill };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _rate = new() { Minimum = 1, Maximum = 1000, Value = 60, Width = 90, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _allowGaps = new() { AutoSize = true, Text = "允许源 sequence 断号（不建议用于正式训练）", ForeColor = DashboardTheme.Danger };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous };
    private readonly Label _status = new() { Dock = DockStyle.Fill, Text = "选择录制 JSONL 和训练帧率。", ForeColor = DashboardTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _generate = DashboardTheme.Button("生成训练数据", DashboardTheme.Accent);
    private readonly Button _cancel = DashboardTheme.Button("关闭", Color.FromArgb(71, 85, 105));
    private readonly Button _openFolder = DashboardTheme.Button("打开输出目录", DashboardTheme.Success);
    private CancellationTokenSource? _generationCancellation;
    private bool _updatingOutput;

    internal TrainingDataDialog(string? suggestedSource)
    {
        Text = "生成固定帧率训练数据";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(820, 410);
        BackColor = DashboardTheme.Window;
        Font = new Font("Segoe UI", 9.5f);
        BuildLayout();
        WireEvents();
        _openFolder.Enabled = false;
        if (!string.IsNullOrWhiteSpace(suggestedSource) && File.Exists(suggestedSource))
        {
            _source.Text = Path.GetFullPath(suggestedSource);
            SuggestOutput();
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = DashboardTheme.Window
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        root.Controls.Add(new Label
        {
            Text = "将原始键鼠事件重采样为 20 / 40 / 60Hz 或自定义频率的训练 ActionFrame。\n界面降采样不会丢弃鼠标增量或按键边沿。",
            Dock = DockStyle.Fill,
            ForeColor = DashboardTheme.Ink,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        }, 0, 0);
        root.Controls.Add(PathRow("源录制 JSONL", _source, BrowseSource), 0, 1);
        root.Controls.Add(PathRow("输出训练 JSONL", _output, BrowseOutput), 0, 2);

        var rateRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        rateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        rateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rateRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rateRow.Controls.Add(new Label
        {
            Text = "训练帧率 (Hz)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            ForeColor = DashboardTheme.Muted
        }, 0, 0);
        var rateActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        rateActions.Controls.Add(_rate);
        foreach (var preset in new[] { 20, 40, 60 })
        {
            var button = new Button { Text = $"{preset} Hz", Size = new Size(76, 30), FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0) };
            button.Click += (_, _) => _rate.Value = preset;
            rateActions.Controls.Add(button);
        }
        rateActions.Controls.Add(new Label { Text = "支持 1–1000Hz 自定义整数帧率", AutoSize = true, ForeColor = DashboardTheme.Muted, Margin = new Padding(16, 6, 0, 0) });
        rateRow.Controls.Add(rateActions, 1, 0);
        root.Controls.Add(rateRow, 0, 3);
        root.Controls.Add(_allowGaps, 0, 4);

        var progressArea = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent };
        progressArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        progressArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progressArea.Controls.Add(_progress, 0, 0);
        progressArea.Controls.Add(_status, 0, 1);
        root.Controls.Add(progressArea, 0, 5);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_generate);
        buttons.Controls.Add(_openFolder);
        root.Controls.Add(buttons, 0, 6);
        Controls.Add(root);
    }

    private static Control PathRow(string title, TextBox textBox, EventHandler browse)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = DashboardTheme.Muted }, 0, 0);
        row.Controls.Add(textBox, 1, 0);
        var button = new Button { Text = "浏览…", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 5, 0, 5) };
        button.Click += browse;
        row.Controls.Add(button, 2, 0);
        return row;
    }

    private void WireEvents()
    {
        _source.TextChanged += (_, _) => SuggestOutput();
        _rate.ValueChanged += (_, _) => SuggestOutput();
        _generate.Click += GenerateAsync;
        _cancel.Click += (_, _) => { if (_generationCancellation is null) Close(); else _generationCancellation.Cancel(); };
        _openFolder.Click += (_, _) =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_output.Text));
            if (directory is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        };
        FormClosing += (_, eventArgs) =>
        {
            if (_generationCancellation is null) return;
            _generationCancellation.Cancel();
            eventArgs.Cancel = true;
        };
    }

    private void BrowseSource(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "inputd 录制 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Title = "选择源录制 JSONL"
        };
        if (File.Exists(_source.Text)) dialog.FileName = _source.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _source.Text = dialog.FileName;
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "训练帧 JSONL (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = "jsonl",
            OverwritePrompt = true,
            Title = "选择训练数据输出文件"
        };
        if (!string.IsNullOrWhiteSpace(_output.Text)) dialog.FileName = _output.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _updatingOutput = true;
            _output.Text = dialog.FileName;
            _updatingOutput = false;
        }
    }

    private void SuggestOutput()
    {
        if (_updatingOutput || string.IsNullOrWhiteSpace(_source.Text)) return;
        try
        {
            var source = Path.GetFullPath(_source.Text);
            _updatingOutput = true;
            _output.Text = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}.{(int)_rate.Value}hz.training.jsonl");
        }
        catch { }
        finally { _updatingOutput = false; }
    }

    private async void GenerateAsync(object? sender, EventArgs e)
    {
        if (_generationCancellation is not null) return;
        try
        {
            var source = Path.GetFullPath(_source.Text);
            var output = Path.GetFullPath(_output.Text);
            if (!File.Exists(source)) throw new FileNotFoundException("源录制 JSONL 不存在。", source);
            var overwrite = false;
            if (File.Exists(output) || File.Exists(output + ".manifest.json"))
            {
                overwrite = MessageBox.Show(this, "输出文件已经存在，是否覆盖？", "确认覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                if (!overwrite) return;
            }

            SetRunning(true);
            _generationCancellation = new CancellationTokenSource();
            var totalBytes = new FileInfo(source).Length;
            var generationProgress = new Progress<TrainingGenerationProgress>(value =>
            {
                _progress.Value = totalBytes == 0 ? 0 : (int)Math.Clamp(value.SourceBytesRead * 1000 / totalBytes, 0, 1000);
                _status.Text = $"已读取 {value.SourceEventsRead:N0} 个源事件，已生成 {value.FramesWritten:N0} 帧…";
            });
            var options = new TrainingGenerationOptions
            {
                InputPath = source,
                OutputPath = output,
                FramesPerSecond = (int)_rate.Value,
                AllowSequenceGaps = _allowGaps.Checked,
                Overwrite = overwrite
            };
            var result = await Task.Run(() => new TrajectoryTrainingDataGenerator().GenerateAsync(options, generationProgress, _generationCancellation.Token));
            _progress.Value = 1000;
            _status.Text = $"完成：{result.SourceEvents:N0} 个事件 → {result.FramesWritten:N0} 帧 @ {result.FramesPerSecond}Hz；sequence 缺口 {result.SequenceGaps:N0}。";
            _status.ForeColor = result.SequenceGaps == 0 ? DashboardTheme.Success : DashboardTheme.Danger;
            _openFolder.Enabled = true;
            MessageBox.Show(this, $"训练数据生成完成。\n\n帧数：{result.FramesWritten:N0}\nManifest：{result.ManifestPath}", "生成完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "生成已取消；临时文件已清理。";
            _status.ForeColor = DashboardTheme.Muted;
        }
        catch (Exception ex)
        {
            _status.Text = "生成失败：" + ex.Message;
            _status.ForeColor = DashboardTheme.Danger;
            MessageBox.Show(this, ex.Message, "无法生成训练数据", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _source.Enabled = _output.Enabled = _rate.Enabled = _allowGaps.Enabled = !running;
        _generate.Enabled = !running;
        _cancel.Text = running ? "取消" : "关闭";
        if (running)
        {
            _progress.Value = 0;
            _status.ForeColor = DashboardTheme.Muted;
            _status.Text = "正在验证源序列并生成训练帧…";
        }
    }
}
