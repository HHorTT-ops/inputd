namespace InputExecutor.Windows;

public readonly record struct ScanCode(ushort Value, bool Extended = false);

public static class PhysicalKeyMap
{
    private const uint MapVirtualKeyToScanCodeEx = 4;
    private static readonly IReadOnlyDictionary<string, ScanCode> Codes = Build();

    public static bool TryGet(string code, out ScanCode scanCode) => Codes.TryGetValue(code, out scanCode);
    public static bool TryGetCode(ushort value, bool extended, out string code)
    {
        foreach (var pair in Codes)
        {
            if (pair.Value.Value == value && pair.Value.Extended == extended)
            {
                code = pair.Key;
                return true;
            }
        }
        code = string.Empty;
        return false;
    }

    public static bool TryGetCodeForVirtualKey(int virtualKey, out string code)
    {
        code = string.Empty;
        if (!OperatingSystem.IsWindows() || virtualKey is < 1 or > 0xff) return false;
        var mapped = NativeMethods.MapVirtualKey((uint)virtualKey, MapVirtualKeyToScanCodeEx);
        if (mapped == 0) return false;
        var scanCode = (ushort)(mapped & 0xff);
        var prefix = (mapped >> 8) & 0xff;
        return TryGetCode(scanCode, prefix is 0xE0 or 0xE1, out code);
    }
    public static IReadOnlyCollection<string> SupportedCodes => Codes.Keys.ToArray();

    private static IReadOnlyDictionary<string, ScanCode> Build()
    {
        var d = new Dictionary<string, ScanCode>(StringComparer.OrdinalIgnoreCase)
        {
            ["Escape"] = new(0x01),
            ["Digit1"] = new(0x02),
            ["Digit2"] = new(0x03),
            ["Digit3"] = new(0x04),
            ["Digit4"] = new(0x05),
            ["Digit5"] = new(0x06),
            ["Digit6"] = new(0x07),
            ["Digit7"] = new(0x08),
            ["Digit8"] = new(0x09),
            ["Digit9"] = new(0x0A),
            ["Digit0"] = new(0x0B),
            ["Minus"] = new(0x0C),
            ["Equal"] = new(0x0D),
            ["Backspace"] = new(0x0E),
            ["Tab"] = new(0x0F),
            ["KeyQ"] = new(0x10),
            ["KeyW"] = new(0x11),
            ["KeyE"] = new(0x12),
            ["KeyR"] = new(0x13),
            ["KeyT"] = new(0x14),
            ["KeyY"] = new(0x15),
            ["KeyU"] = new(0x16),
            ["KeyI"] = new(0x17),
            ["KeyO"] = new(0x18),
            ["KeyP"] = new(0x19),
            ["BracketLeft"] = new(0x1A),
            ["BracketRight"] = new(0x1B),
            ["Enter"] = new(0x1C),
            ["ControlLeft"] = new(0x1D),
            ["KeyA"] = new(0x1E),
            ["KeyS"] = new(0x1F),
            ["KeyD"] = new(0x20),
            ["KeyF"] = new(0x21),
            ["KeyG"] = new(0x22),
            ["KeyH"] = new(0x23),
            ["KeyJ"] = new(0x24),
            ["KeyK"] = new(0x25),
            ["KeyL"] = new(0x26),
            ["Semicolon"] = new(0x27),
            ["Quote"] = new(0x28),
            ["Backquote"] = new(0x29),
            ["ShiftLeft"] = new(0x2A),
            ["Backslash"] = new(0x2B),
            ["KeyZ"] = new(0x2C),
            ["KeyX"] = new(0x2D),
            ["KeyC"] = new(0x2E),
            ["KeyV"] = new(0x2F),
            ["KeyB"] = new(0x30),
            ["KeyN"] = new(0x31),
            ["KeyM"] = new(0x32),
            ["Comma"] = new(0x33),
            ["Period"] = new(0x34),
            ["Slash"] = new(0x35),
            ["ShiftRight"] = new(0x36),
            ["NumpadMultiply"] = new(0x37),
            ["AltLeft"] = new(0x38),
            ["Space"] = new(0x39),
            ["CapsLock"] = new(0x3A),
            ["F1"] = new(0x3B),
            ["F2"] = new(0x3C),
            ["F3"] = new(0x3D),
            ["F4"] = new(0x3E),
            ["F5"] = new(0x3F),
            ["F6"] = new(0x40),
            ["F7"] = new(0x41),
            ["F8"] = new(0x42),
            ["F9"] = new(0x43),
            ["F10"] = new(0x44),
            ["NumLock"] = new(0x45),
            ["ScrollLock"] = new(0x46),
            ["Numpad7"] = new(0x47),
            ["Numpad8"] = new(0x48),
            ["Numpad9"] = new(0x49),
            ["NumpadSubtract"] = new(0x4A),
            ["Numpad4"] = new(0x4B),
            ["Numpad5"] = new(0x4C),
            ["Numpad6"] = new(0x4D),
            ["NumpadAdd"] = new(0x4E),
            ["Numpad1"] = new(0x4F),
            ["Numpad2"] = new(0x50),
            ["Numpad3"] = new(0x51),
            ["Numpad0"] = new(0x52),
            ["NumpadDecimal"] = new(0x53),
            ["F11"] = new(0x57),
            ["F12"] = new(0x58),
            ["NumpadEnter"] = new(0x1C, true),
            ["ControlRight"] = new(0x1D, true),
            ["NumpadDivide"] = new(0x35, true),
            ["PrintScreen"] = new(0x37, true),
            ["AltRight"] = new(0x38, true),
            ["Home"] = new(0x47, true),
            ["ArrowUp"] = new(0x48, true),
            ["PageUp"] = new(0x49, true),
            ["ArrowLeft"] = new(0x4B, true),
            ["ArrowRight"] = new(0x4D, true),
            ["End"] = new(0x4F, true),
            ["ArrowDown"] = new(0x50, true),
            ["PageDown"] = new(0x51, true),
            ["Insert"] = new(0x52, true),
            ["Delete"] = new(0x53, true),
            ["MetaLeft"] = new(0x5B, true),
            ["MetaRight"] = new(0x5C, true),
            ["ContextMenu"] = new(0x5D, true)
        };
        return d;
    }
}
