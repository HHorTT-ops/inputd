namespace InputExecutor.Core.Protocol;

/// <summary>
/// Best-effort attribution for mouse Raw Input frames exposed on the physical-input SSE stream.
/// Windows does not provide a single API that reliably proves "human hand on a physical mouse".
/// </summary>
public static class InputSourceClass
{
    /// <summary>
    /// Raw Input reported a non-null device handle. Strong signal of a registered HID/mouse device,
    /// not a proof of human physical operation (virtual HID drivers can also produce handles).
    /// </summary>
    public const string HumanPhysical = "HUMAN_PHYSICAL";

    /// <summary>
    /// inputd's own SendInput path tagged with <c>ExecutorEventTag</c>.
    /// These frames are excluded from the SSE mouse stream and counted only as diagnostics.
    /// </summary>
    public const string ExecutorInjected = "EXECUTOR_INJECTED";

    /// <summary>
    /// Mouse Raw Input without a reliable device handle (commonly includes untagged
    /// <c>SendInput</c> / <c>mouse_event</c> from this or other processes). Must not be treated
    /// as authoritative human-physical evidence.
    /// </summary>
    public const string Unattributed = "UNATTRIBUTED";

    public static string Classify(HumanInputFrame frame)
    {
        if (frame.InjectedTagged)
            return ExecutorInjected;

        if (TryParseDeviceHandle(frame.RawDeviceHandle, out var handle) && handle != 0)
            return HumanPhysical;

        return Unattributed;
    }

    public static string FormatDeviceHandle(nint device) =>
        $"0x{unchecked((nuint)device):X}";

    public static bool TryParseDeviceHandle(string? raw, out nuint handle)
    {
        handle = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return nuint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out handle);
    }
}
