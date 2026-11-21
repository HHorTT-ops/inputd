using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

/// <summary>
/// Captures DPI / display metrics and documents which APIs report which coordinate spaces.
/// Does not change process DPI awareness.
/// </summary>
public static class DisplayCoordinateDiagnostics
{
    public static DisplayCoordinateSnapshot Capture()
    {
        string? processDpi = null;
        string? processDpiError = null;
        try
        {
            var hr = NativeMethods.GetProcessDpiAwareness(NativeMethods.GetCurrentProcess(), out var awareness);
            processDpi = hr == 0 ? awareness.ToString() : $"HRESULT_0x{hr:X8}";
        }
        catch (Exception ex)
        {
            processDpiError = ex.GetType().Name + ": " + ex.Message;
        }

        string? threadDpi = null;
        string? threadDpiError = null;
        try
        {
            var ctx = NativeMethods.GetThreadDpiAwarenessContext();
            var awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(ctx);
            threadDpi = ((NativeMethods.DPI_AWARENESS)awareness).ToString();
        }
        catch (Exception ex)
        {
            threadDpiError = ex.GetType().Name + ": " + ex.Message;
        }

        uint? systemDpi = null;
        string? systemDpiError = null;
        try { systemDpi = NativeMethods.GetDpiForSystem(); }
        catch (Exception ex) { systemDpiError = ex.GetType().Name; }

        object? cursorLogical = null;
        object? cursorPhysical = null;
        string? cursorPhysicalNote = null;
        if (NativeMethods.GetCursorPos(out var logical))
        {
            cursorLogical = new
            {
                x = logical.X,
                y = logical.Y,
                coordinate_space = "GetCursorPos — process-view screen coords (DPI-virtualized when process is unaware)"
            };
        }

        try
        {
            if (NativeMethods.GetPhysicalCursorPos(out var physical))
            {
                cursorPhysical = new
                {
                    x = physical.X,
                    y = physical.Y,
                    coordinate_space = "GetPhysicalCursorPos — physical screen coords (matches typical WH_MOUSE_LL pt when process is DPI-unaware)"
                };
            }
            else
            {
                cursorPhysicalNote = "GetPhysicalCursorPos returned false";
            }
        }
        catch (Exception ex)
        {
            cursorPhysicalNote = "GetPhysicalCursorPos unavailable: " + ex.GetType().Name;
        }

        var sm = CaptureSystemMetrics("GetSystemMetrics — process-view / may be DPI-virtualized");
        object? smFor96 = null;
        object? smForSystemDpi = null;
        if (systemDpi is uint dpi)
        {
            try { smFor96 = CaptureSystemMetricsForDpi(96, "GetSystemMetricsForDpi(dpi=96)"); } catch { /* optional */ }
            try { smForSystemDpi = CaptureSystemMetricsForDpi(dpi, $"GetSystemMetricsForDpi(dpi={dpi})"); } catch { /* optional */ }
        }

        var monitors = EnumerateMonitors();
        var converter = CoordinateSpaceConverter.FromLiveSystem();

        return new DisplayCoordinateSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            ProcessDpiAwareness = processDpi,
            ProcessDpiAwarenessError = processDpiError,
            ThreadDpiAwareness = threadDpi,
            ThreadDpiAwarenessError = threadDpiError,
            DpiForSystem = systemDpi,
            DpiForSystemError = systemDpiError,
            SystemMetrics = sm,
            SystemMetricsForDpi96 = smFor96,
            SystemMetricsForSystemDpi = smForSystemDpi,
            Monitors = monitors,
            CursorPos = cursorLogical,
            PhysicalCursorPos = cursorPhysical,
            PhysicalCursorPosNote = cursorPhysicalNote,
            MsllHookPtCoordinateSpace =
                "MSLLHOOKSTRUCT.pt — screen coordinates delivered to WH_MOUSE_LL; often physical/per-monitor space and may disagree with GetSystemMetrics under DPI virtualization.",
            RawAbsoluteCoordinateSpace =
                "RAWMOUSE absolute lLastX/lLastY are 0..65535 normalized over primary or virtual desktop; mapped with (size-1) into the chosen metrics space, then optionally aligned to hook space.",
            Alignment = converter.Describe(),
            Note = "Do not change process DPI mode to 'fix' coordinates. Prefer explicit, logged conversion (LogicalToPhysical / measured scale). Never hardcode a 2x ratio."
        };
    }

    private static object CaptureSystemMetrics(string spaceNote) => new
    {
        coordinate_space = spaceNote,
        cx_screen = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
        cy_screen = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN),
        x_virtual = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        y_virtual = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        cx_virtual = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        cy_virtual = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN),
        c_monitors = NativeMethods.GetSystemMetrics(NativeMethods.SM_CMONITORS)
    };

    private static object CaptureSystemMetricsForDpi(uint dpi, string spaceNote) => new
    {
        coordinate_space = spaceNote,
        dpi,
        cx_screen = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSCREEN, dpi),
        cy_screen = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYSCREEN, dpi),
        x_virtual = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_XVIRTUALSCREEN, dpi),
        y_virtual = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_YVIRTUALSCREEN, dpi),
        cx_virtual = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXVIRTUALSCREEN, dpi),
        cy_virtual = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYVIRTUALSCREEN, dpi)
    };

    private static IReadOnlyList<object> EnumerateMonitors()
    {
        var list = new List<object>();
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, ref rect, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            uint? dpiX = null, dpiY = null;
            string? dpiError = null;
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                try
                {
                    var hr = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out var x, out var y);
                    if (hr == 0) { dpiX = x; dpiY = y; }
                    else dpiError = $"HRESULT_0x{hr:X8}";
                }
                catch (Exception ex) { dpiError = ex.GetType().Name; }

                list.Add(new
                {
                    device = info.szDevice,
                    primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    monitor_rect = new { left = info.rcMonitor.Left, top = info.rcMonitor.Top, right = info.rcMonitor.Right, bottom = info.rcMonitor.Bottom },
                    work_rect = new { left = info.rcWork.Left, top = info.rcWork.Top, right = info.rcWork.Right, bottom = info.rcWork.Bottom },
                    dpi_effective_x = dpiX,
                    dpi_effective_y = dpiY,
                    dpi_error = dpiError,
                    coordinate_space = "GetMonitorInfo rects — process-view; GetDpiForMonitor is physical effective DPI for that monitor"
                });
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }
}

public sealed record DisplayCoordinateSnapshot
{
    [JsonPropertyName("captured_at_utc")]
    public string CapturedAtUtc { get; init; } = "";

    [JsonPropertyName("process_dpi_awareness")]
    public string? ProcessDpiAwareness { get; init; }

    [JsonPropertyName("process_dpi_awareness_error")]
    public string? ProcessDpiAwarenessError { get; init; }

    [JsonPropertyName("thread_dpi_awareness")]
    public string? ThreadDpiAwareness { get; init; }

    [JsonPropertyName("thread_dpi_awareness_error")]
    public string? ThreadDpiAwarenessError { get; init; }

    [JsonPropertyName("dpi_for_system")]
    public uint? DpiForSystem { get; init; }

    [JsonPropertyName("dpi_for_system_error")]
    public string? DpiForSystemError { get; init; }

    [JsonPropertyName("system_metrics")]
    public object? SystemMetrics { get; init; }

    [JsonPropertyName("system_metrics_for_dpi_96")]
    public object? SystemMetricsForDpi96 { get; init; }

    [JsonPropertyName("system_metrics_for_system_dpi")]
    public object? SystemMetricsForSystemDpi { get; init; }

    [JsonPropertyName("monitors")]
    public IReadOnlyList<object>? Monitors { get; init; }

    [JsonPropertyName("cursor_pos")]
    public object? CursorPos { get; init; }

    [JsonPropertyName("physical_cursor_pos")]
    public object? PhysicalCursorPos { get; init; }

    [JsonPropertyName("physical_cursor_pos_note")]
    public string? PhysicalCursorPosNote { get; init; }

    [JsonPropertyName("msllhook_pt_coordinate_space")]
    public string? MsllHookPtCoordinateSpace { get; init; }

    [JsonPropertyName("raw_absolute_coordinate_space")]
    public string? RawAbsoluteCoordinateSpace { get; init; }

    [JsonPropertyName("alignment")]
    public object? Alignment { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}
