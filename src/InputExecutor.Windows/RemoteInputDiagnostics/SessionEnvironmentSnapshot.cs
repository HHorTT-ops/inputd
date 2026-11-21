using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

public sealed record SessionEnvironmentSnapshot
{
    [JsonPropertyName("captured_at_utc")]
    public string CapturedAtUtc { get; init; } = "";

    [JsonPropertyName("sm_remote_session")]
    public bool SmRemoteSession { get; init; }

    [JsonPropertyName("sm_remote_session_note")]
    public string SmRemoteSessionNote { get; init; } =
        "SM_REMOTESESSION=false does NOT prove absence of third-party remote control (e.g. NetEase UU).";

    [JsonPropertyName("process_id")]
    public int ProcessId { get; init; }

    [JsonPropertyName("session_id")]
    public uint? SessionId { get; init; }

    [JsonPropertyName("wts_client_protocol_type")]
    public int? WtsClientProtocolType { get; init; }

    [JsonPropertyName("wts_client_protocol_note")]
    public string? WtsClientProtocolNote { get; init; }

    [JsonPropertyName("monitor_count")]
    public int MonitorCount { get; init; }

    [JsonPropertyName("virtual_screen")]
    public required object VirtualScreen { get; init; }

    [JsonPropertyName("primary_screen")]
    public required object PrimaryScreen { get; init; }

    [JsonPropertyName("raw_input_devices")]
    public required IReadOnlyList<RawInputDeviceInfoDto> RawInputDevices { get; init; }

    public static SessionEnvironmentSnapshot Capture()
    {
        uint? sessionId = null;
        if (NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var sid))
            sessionId = sid;

        int? protocol = null;
        string? protocolNote = null;
        if (sessionId is uint s)
        {
            try
            {
                if (NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, (int)s, NativeMethods.WTS_INFO_CLASS.WTSClientProtocolType, out var buffer, out _))
                {
                    try { protocol = Marshal.ReadInt16(buffer); }
                    finally { NativeMethods.WTSFreeMemory(buffer); }
                    protocolNote = protocol switch
                    {
                        0 => "Console",
                        1 => "Legacy RDP",
                        2 => "RDP",
                        _ => "Other/unknown WTS protocol value"
                    };
                }
            }
            catch (Exception ex)
            {
                protocolNote = "WTSQuerySessionInformation unavailable: " + ex.GetType().Name;
            }
        }

        return new SessionEnvironmentSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            SmRemoteSession = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0,
            ProcessId = Environment.ProcessId,
            SessionId = sessionId,
            WtsClientProtocolType = protocol,
            WtsClientProtocolNote = protocolNote,
            MonitorCount = NativeMethods.GetSystemMetrics(NativeMethods.SM_CMONITORS),
            VirtualScreen = new
            {
                left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
                top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
                width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)
            },
            PrimaryScreen = new
            {
                width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
            },
            RawInputDevices = EnumerateRawInputDevices()
        };
    }

    private static IReadOnlyList<RawInputDeviceInfoDto> EnumerateRawInputDevices()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();
        NativeMethods.GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return Array.Empty<RawInputDeviceInfoDto>();
        var list = new NativeMethods.RAWINPUTDEVICELIST[count];
        var written = NativeMethods.GetRawInputDeviceList(list, ref count, size);
        if (written == unchecked((uint)-1)) return Array.Empty<RawInputDeviceInfoDto>();

        var result = new List<RawInputDeviceInfoDto>((int)count);
        for (var i = 0; i < written && i < list.Length; i++)
        {
            var device = list[i];
            string? name = null;
            try
            {
                uint nameSize = 0;
                NativeMethods.GetRawInputDeviceInfo(device.hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref nameSize);
                if (nameSize > 0)
                {
                    var buffer = Marshal.AllocHGlobal((int)nameSize * 2);
                    try
                    {
                        if (NativeMethods.GetRawInputDeviceInfo(device.hDevice, NativeMethods.RIDI_DEVICENAME, buffer, ref nameSize) != unchecked((uint)-1))
                            name = Marshal.PtrToStringUni(buffer);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            catch { /* non-fatal */ }

            // Redact potentially sensitive trailing instance IDs while keeping device class path.
            var redacted = RedactDeviceName(name);
            result.Add(new RawInputDeviceInfoDto
            {
                DeviceHandle = InputExecutor.Core.Protocol.InputSourceClass.FormatDeviceHandle(device.hDevice),
                DeviceType = device.dwType switch
                {
                    NativeMethods.RIM_TYPEMOUSE => "mouse",
                    NativeMethods.RIM_TYPEKEYBOARD => "keyboard",
                    2u => "hid",
                    _ => $"type_{device.dwType}"
                },
                DeviceNameRedacted = redacted,
                LooksLikeVirtualHid = redacted is not null && (
                    redacted.Contains("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                    redacted.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase) ||
                    redacted.Contains("VHID", StringComparison.OrdinalIgnoreCase) ||
                    redacted.Contains("RDP", StringComparison.OrdinalIgnoreCase))
            });
        }
        return result;
    }

    private static string? RedactDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        // Keep up to last meaningful class segment; strip long hardware IDs after '#' when present.
        var parts = name.Split('#');
        if (parts.Length <= 2) return name;
        return string.Join('#', parts.Take(2)) + "#<redacted>";
    }
}

public sealed record RawInputDeviceInfoDto
{
    [JsonPropertyName("device_handle")]
    public string DeviceHandle { get; init; } = "0x0";

    [JsonPropertyName("device_type")]
    public string DeviceType { get; init; } = "";

    [JsonPropertyName("device_name_redacted")]
    public string? DeviceNameRedacted { get; init; }

    [JsonPropertyName("looks_like_virtual_hid")]
    public bool LooksLikeVirtualHid { get; init; }
}
