using System.Text.Json;
using InputExecutor.Windows;

namespace InputExecutor.Dashboard;

internal sealed record DashboardHotkeyGesture
{
    public DashboardHotkeyGesture() { }

    internal DashboardHotkeyGesture(WindowsHotkeyModifiers modifiers, int virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public WindowsHotkeyModifiers Modifiers { get; init; }
    public int VirtualKey { get; init; }

    internal string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(WindowsHotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(WindowsHotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(WindowsHotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(WindowsHotkeyModifiers.Windows)) parts.Add("Win");
            parts.Add(KeyDisplayName(VirtualKey));
            return string.Join(" + ", parts);
        }
    }

    internal IReadOnlyList<int> KeysToRelease()
    {
        var keys = new List<int>(5) { VirtualKey };
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Control)) keys.Add(0x11);
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Alt)) keys.Add(0x12);
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Shift)) keys.Add(0x10);
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Windows)) { keys.Add(0x5B); keys.Add(0x5C); }
        return keys;
    }

    internal IReadOnlyCollection<string> PhysicalCodesToSuppress()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (PhysicalKeyMap.TryGetCodeForVirtualKey(VirtualKey, out var main)) codes.Add(main);
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Control))
        {
            codes.Add("ControlLeft");
            codes.Add("ControlRight");
        }
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Alt))
        {
            codes.Add("AltLeft");
            codes.Add("AltRight");
        }
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Shift))
        {
            codes.Add("ShiftLeft");
            codes.Add("ShiftRight");
        }
        if (Modifiers.HasFlag(WindowsHotkeyModifiers.Windows))
        {
            codes.Add("MetaLeft");
            codes.Add("MetaRight");
        }
        return codes;
    }

    internal static DashboardHotkeyGesture FromKeyEvent(KeyEventArgs e)
    {
        var modifiers = WindowsHotkeyModifiers.None;
        if (e.Control) modifiers |= WindowsHotkeyModifiers.Control;
        if (e.Alt) modifiers |= WindowsHotkeyModifiers.Alt;
        if (e.Shift) modifiers |= WindowsHotkeyModifiers.Shift;
        return new DashboardHotkeyGesture(modifiers, (int)e.KeyCode);
    }

    internal static bool IsModifierOnly(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    private static string KeyDisplayName(int virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        var key = (Keys)virtualKey;
        return key switch
        {
            Keys.Next => "PageDown",
            Keys.Prior => "PageUp",
            Keys.Return => "Enter",
            _ => Enum.GetName(key) ?? $"VK 0x{virtualKey:X2}"
        };
    }
}

internal sealed record DashboardSettings
{
    public DashboardSettings() { }

    public int Version { get; init; } = 2;
    public DashboardHotkeyGesture StartRecording { get; init; } =
        new(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt, (int)Keys.F9);
    public DashboardHotkeyGesture StopRecording { get; init; } =
        new(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt, (int)Keys.F10);
    public DashboardHotkeyGesture StartPlayback { get; init; } =
        new(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt, (int)Keys.F7);
    public DashboardHotkeyGesture StopPlayback { get; init; } =
        new(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt, (int)Keys.F8);

    internal static DashboardSettings Default => new();

    internal static string? Validate(DashboardSettings settings)
    {
        var allowed = WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt | WindowsHotkeyModifiers.Shift | WindowsHotkeyModifiers.Windows;
        var gestures = new[]
        {
            ("开始录制", settings.StartRecording),
            ("停止录制", settings.StopRecording),
            ("播放操作", settings.StartPlayback),
            ("停止播放", settings.StopPlayback)
        };
        foreach (var (name, gesture) in gestures)
        {
            if (gesture is null) return $"{name}快捷键不能为空。";
            if ((gesture.Modifiers & ~allowed) != 0) return $"{name}包含不支持的修饰键。";
            if (gesture.VirtualKey is < 1 or > 0xff || DashboardHotkeyGesture.IsModifierOnly((Keys)gesture.VirtualKey))
                return $"{name}必须包含一个非修饰主键。";
        }
        var duplicate = gestures.GroupBy(item => item.Item2).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) return $"{string.Join("、", duplicate.Select(item => item.Item1))}不能使用同一个快捷键。";
        var emergency = new DashboardHotkeyGesture(WindowsHotkeyModifiers.Control | WindowsHotkeyModifiers.Alt, (int)Keys.F12);
        if (gestures.Any(item => item.Item2 == emergency))
            return "Ctrl + Alt + F12 是 inputd 紧急停止键，不能用于录制或回放快捷键。";
        return null;
    }
}

internal static class DashboardSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inputd", "dashboard.settings.json");

    internal static DashboardSettings Load(out string? warning, string? path = null)
    {
        path ??= DefaultPath;
        warning = null;
        if (!File.Exists(path)) return DashboardSettings.Default;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var start = root.GetProperty("start_recording");
            var stop = root.GetProperty("stop_recording");
            var defaults = DashboardSettings.Default;
            var play = root.TryGetProperty("start_playback", out var storedPlay) ? storedPlay : default;
            var stopPlay = root.TryGetProperty("stop_playback", out var storedStopPlay) ? storedStopPlay : default;
            var settings = new DashboardSettings
            {
                Version = root.TryGetProperty("version", out var version) ? version.GetInt32() : 1,
                StartRecording = new DashboardHotkeyGesture(
                    (WindowsHotkeyModifiers)start.GetProperty("modifiers").GetUInt32(),
                    start.GetProperty("virtual_key").GetInt32()),
                StopRecording = new DashboardHotkeyGesture(
                    (WindowsHotkeyModifiers)stop.GetProperty("modifiers").GetUInt32(),
                    stop.GetProperty("virtual_key").GetInt32()),
                StartPlayback = play.ValueKind == JsonValueKind.Object
                    ? new DashboardHotkeyGesture(
                        (WindowsHotkeyModifiers)play.GetProperty("modifiers").GetUInt32(),
                        play.GetProperty("virtual_key").GetInt32())
                    : defaults.StartPlayback,
                StopPlayback = stopPlay.ValueKind == JsonValueKind.Object
                    ? new DashboardHotkeyGesture(
                        (WindowsHotkeyModifiers)stopPlay.GetProperty("modifiers").GetUInt32(),
                        stopPlay.GetProperty("virtual_key").GetInt32())
                    : defaults.StopPlayback
            };
            var validation = DashboardSettings.Validate(settings);
            if (validation is not null) throw new InvalidDataException(validation);
            return settings;
        }
        catch (Exception ex)
        {
            warning = $"快捷键设置无效，已恢复默认值：{ex.Message}";
            return DashboardSettings.Default;
        }
    }

    internal static void Save(DashboardSettings settings, string? path = null)
    {
        var validation = DashboardSettings.Validate(settings);
        if (validation is not null) throw new InvalidDataException(validation);
        path ??= DefaultPath;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stored = new
            {
                version = settings.Version,
                start_recording = new { modifiers = (uint)settings.StartRecording.Modifiers, virtual_key = settings.StartRecording.VirtualKey },
                stop_recording = new { modifiers = (uint)settings.StopRecording.Modifiers, virtual_key = settings.StopRecording.VirtualKey },
                start_playback = new { modifiers = (uint)settings.StartPlayback.Modifiers, virtual_key = settings.StartPlayback.VirtualKey },
                stop_playback = new { modifiers = (uint)settings.StopPlayback.Modifiers, virtual_key = settings.StopPlayback.VirtualKey }
            };
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Json));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
