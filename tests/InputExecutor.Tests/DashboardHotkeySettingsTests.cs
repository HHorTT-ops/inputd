using InputExecutor.Dashboard;
using InputExecutor.Windows;
using System.Windows.Forms;

namespace InputExecutor.Tests;

public sealed class DashboardHotkeySettingsTests
{
    [Fact]
    public void Defaults_AreDistinctValidAndHumanReadable()
    {
        var settings = DashboardSettings.Default;
        Assert.Null(DashboardSettings.Validate(settings));
        Assert.NotEqual(settings.StartRecording, settings.StopRecording);
        Assert.Equal(4, new[] { settings.StartRecording, settings.StopRecording, settings.StartPlayback, settings.StopPlayback }.Distinct().Count());
        Assert.Equal("Ctrl + Alt + F9", settings.StartRecording.DisplayText);
        Assert.Equal("Ctrl + Alt + F10", settings.StopRecording.DisplayText);
        Assert.Equal("Ctrl + Alt + F7", settings.StartPlayback.DisplayText);
        Assert.Equal("Ctrl + Alt + F8", settings.StopPlayback.DisplayText);
    }

    [Fact]
    public void Validation_RejectsDuplicateModifierOnlyAndEmergencyHotkeys()
    {
        var duplicate = new DashboardSettings
        {
            StartRecording = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control, (int)Keys.F8),
            StopRecording = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control, (int)Keys.F8)
        };
        Assert.Contains("不能使用同一个", DashboardSettings.Validate(duplicate));

        var modifierOnly = duplicate with
        {
            StopRecording = new DashboardHotkeyGesture(WindowsHotkeyModifiers.None, (int)Keys.ControlKey)
        };
        Assert.Contains("非修饰主键", DashboardSettings.Validate(modifierOnly));

        var emergency = duplicate with
        {
            StopRecording = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt, (int)Keys.F12)
        };
        Assert.Contains("紧急停止键", DashboardSettings.Validate(emergency));
    }

    [Fact]
    public void Store_RoundTripsAndInvalidFileFallsBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"inputd-hotkey-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var expected = new DashboardSettings
            {
                StartRecording = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Shift, (int)Keys.F7),
                StopRecording = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Alt, (int)Keys.F8),
                StartPlayback = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control, (int)Keys.F5),
                StopPlayback = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control, (int)Keys.F6)
            };
            DashboardSettingsStore.Save(expected, path);
            var loaded = DashboardSettingsStore.Load(out var warning, path);
            Assert.Null(warning);
            Assert.Equal(expected, loaded);

            File.WriteAllText(path, "{not-json");
            var fallback = DashboardSettingsStore.Load(out warning, path);
            Assert.NotNull(warning);
            Assert.Equal(DashboardSettings.Default, fallback);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void KeysToRelease_IncludesMainAndConfiguredModifiers()
    {
        var gesture = new DashboardHotkeyGesture(
            WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt | WindowsHotkeyModifiers.Shift,
            (int)Keys.F9);
        Assert.Equal(new[] { (int)Keys.F9, 0x11, 0x12, 0x10 }, gesture.KeysToRelease());
        Assert.Contains("F9", gesture.PhysicalCodesToSuppress());
        Assert.Contains("ControlLeft", gesture.PhysicalCodesToSuppress());
        Assert.Contains("ControlRight", gesture.PhysicalCodesToSuppress());
    }

    [Fact]
    public void KeyEventCapture_AndSettingsDialogExposeEditableStartAndStopFields()
    {
        var captured = DashboardHotkeyGesture.FromKeyEvent(new KeyEventArgs(Keys.Control | Keys.Shift | Keys.F7));
        Assert.Equal(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Shift, captured.Modifiers);
        Assert.Equal((int)Keys.F7, captured.VirtualKey);

        using var dialog = new HotkeySettingsDialog(DashboardSettings.Default);
        var start = Assert.Single(dialog.Controls.Find("StartRecordingHotkeyBox", searchAllChildren: true));
        var stop = Assert.Single(dialog.Controls.Find("StopRecordingHotkeyBox", searchAllChildren: true));
        var play = Assert.Single(dialog.Controls.Find("StartPlaybackHotkeyBox", searchAllChildren: true));
        var stopPlay = Assert.Single(dialog.Controls.Find("StopPlaybackHotkeyBox", searchAllChildren: true));
        Assert.Equal("Ctrl + Alt + F9", start.Text);
        Assert.Equal("Ctrl + Alt + F10", stop.Text);
        Assert.Equal("Ctrl + Alt + F7", play.Text);
        Assert.Equal("Ctrl + Alt + F8", stopPlay.Text);
    }
}
