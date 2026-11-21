using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

public static class DeviceCandidateClass
{
    public const string VhfDeviceCandidate = "VHF_DEVICE_CANDIDATE";
    public const string VirtualHidCandidate = "VIRTUAL_HID_CANDIDATE";
    public const string Unknown = "UNKNOWN";
}

public sealed record SessionEnvironmentSnapshot
{
    [JsonPropertyName("captured_at_utc")]
    public string CapturedAtUtc { get; init; } = "";

    [JsonPropertyName("sm_remote_session")]
    public bool SmRemoteSession { get; init; }

    [JsonPropertyName("sm_remote_control")]
    public bool SmRemoteControl { get; init; }

    [JsonPropertyName("wts_is_remote_session")]
    public bool? WtsIsRemoteSession { get; init; }

    [JsonPropertyName("wts_is_remote_session_available")]
    public bool WtsIsRemoteSessionAvailable { get; init; }

    [JsonPropertyName("remote_indicators_note")]
    public string RemoteIndicatorsNote { get; init; } =
        "SM_REMOTESESSION / SM_REMOTECONTROL / WTSIsRemoteSession false or unavailable does NOT prove absence of third-party remote control (e.g. NetEase UU).";

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

    [JsonPropertyName("device_list_compare_note")]
    public string DeviceListCompareNote { get; init; } =
        "Capture once before UU connect and once after; compare device_handle sets. Name matches are candidates only — never assert UU ownership from names alone.";

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

        var (wtsRemote, wtsAvailable) = ProbeWtsIsRemoteSession();

        return new SessionEnvironmentSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            SmRemoteSession = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0,
            SmRemoteControl = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTECONTROL) != 0,
            WtsIsRemoteSession = wtsRemote,
            WtsIsRemoteSessionAvailable = wtsAvailable,
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

    public static string ClassifyDeviceCandidate(string? redactedName)
    {
        if (string.IsNullOrWhiteSpace(redactedName))
            return DeviceCandidateClass.Unknown;
        if (redactedName.Contains("HID_DEVICE_SYSTEM_VHF", StringComparison.OrdinalIgnoreCase) ||
            redactedName.Contains("SYSTEM_VHF", StringComparison.OrdinalIgnoreCase))
            return DeviceCandidateClass.VhfDeviceCandidate;
        if (redactedName.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase) ||
            redactedName.Contains("VHID", StringComparison.OrdinalIgnoreCase) ||
            redactedName.Contains("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
            redactedName.Contains("RDP", StringComparison.OrdinalIgnoreCase))
            return DeviceCandidateClass.VirtualHidCandidate;
        return DeviceCandidateClass.Unknown;
    }

    private static (bool? Value, bool Available) ProbeWtsIsRemoteSession()
    {
        foreach (var dll in new[] { "wtsapi32.dll", "kernel32.dll" })
        {
            var module = NativeMethods.LoadLibrary(dll);
            if (module == IntPtr.Zero) continue;
            var proc = NativeMethods.GetProcAddress(module, "WTSIsRemoteSession");
            if (proc == IntPtr.Zero) continue;
            try
            {
                var fn = Marshal.GetDelegateForFunctionPointer<NativeMethods.WtsIsRemoteSessionDelegate>(proc);
                return (fn(), true);
            }
            catch
            {
                return (null, true);
            }
        }
        return (null, false);
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

            var redacted = RedactDeviceName(name);
            var info = TryReadDeviceInfo(device.hDevice, device.dwType);
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
                CandidateClass = ClassifyDeviceCandidate(redacted),
                LooksLikeVirtualHid = ClassifyDeviceCandidate(redacted) is not DeviceCandidateClass.Unknown,
                HidUsagePage = info?.UsagePage,
                HidUsage = info?.Usage,
                VendorId = info?.VendorId,
                ProductId = info?.ProductId,
                DeviceInfoNote = "CandidateClass is name-based only; never assert UU ownership from device names."
            });
        }
        return result;
    }

    private static (ushort? UsagePage, ushort? Usage, uint? VendorId, uint? ProductId)? TryReadDeviceInfo(IntPtr device, uint dwType)
    {
        try
        {
            var info = new NativeMethods.RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.RID_DEVICE_INFO>() };
            var size = info.cbSize;
            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICEINFO, ptr, ref size) == unchecked((uint)-1))
                    return null;
                var parsed = Marshal.PtrToStructure<NativeMethods.RID_DEVICE_INFO>(ptr);
                if (parsed.dwType == 2 || dwType == 2)
                {
                    return (parsed.hid.usUsagePage, parsed.hid.usUsage, parsed.hid.dwVendorId, parsed.hid.dwProductId);
                }
                if (parsed.dwType == NativeMethods.RIM_TYPEMOUSE)
                {
                    return (null, null, null, null);
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { /* non-fatal */ }
        return null;
    }

    private static string? RedactDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
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

    [JsonPropertyName("candidate_class")]
    public string CandidateClass { get; init; } = DeviceCandidateClass.Unknown;

    [JsonPropertyName("looks_like_virtual_hid")]
    public bool LooksLikeVirtualHid { get; init; }

    [JsonPropertyName("hid_usage_page")]
    public ushort? HidUsagePage { get; init; }

    [JsonPropertyName("hid_usage")]
    public ushort? HidUsage { get; init; }

    [JsonPropertyName("vendor_id")]
    public uint? VendorId { get; init; }

    [JsonPropertyName("product_id")]
    public uint? ProductId { get; init; }

    [JsonPropertyName("device_info_note")]
    public string? DeviceInfoNote { get; init; }
}
