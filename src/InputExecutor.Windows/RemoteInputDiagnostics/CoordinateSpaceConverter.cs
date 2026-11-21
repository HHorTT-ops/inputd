namespace InputExecutor.Windows.RemoteInputDiagnostics;

/// <summary>
/// Converts process-view (GetSystemMetrics / mapped Raw) coordinates into WH_MOUSE_LL / physical space.
/// Never hardcodes a 2× ratio; scale is measured from DPI APIs, LogicalToPhysical, or cursor samples.
/// </summary>
public sealed class CoordinateSpaceConverter
{
    public string Method { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }
    public bool IsIdentity => Math.Abs(ScaleX - 1.0) < 1e-6 && Math.Abs(ScaleY - 1.0) < 1e-6;
    public string? Detail { get; }

    private readonly bool _preferLogicalToPhysicalApi;

    private CoordinateSpaceConverter(string method, double scaleX, double scaleY, string? detail, bool preferLogicalToPhysicalApi)
    {
        Method = method;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Detail = detail;
        _preferLogicalToPhysicalApi = preferLogicalToPhysicalApi;
    }

    public static CoordinateSpaceConverter Identity { get; } =
        new("identity", 1.0, 1.0, "metrics space already matches hook space", false);

    /// <summary>Test seam: build an explicit measured scale (must not be a magic hardcoded production path).</summary>
    public static CoordinateSpaceConverter FromMeasuredScale(double scaleX, double scaleY, string method, string? detail = null) =>
        new(method, scaleX, scaleY, detail, preferLogicalToPhysicalApi: false);

    public static CoordinateSpaceConverter FromLiveSystem()
    {
        // 1) Prefer per-point LogicalToPhysical when API works and actually changes a sample point.
        try
        {
            var sample = new NativeMethods.POINT { X = 100, Y = 100 };
            var desktop = NativeMethods.GetDesktopWindow();
            if (NativeMethods.LogicalToPhysicalPointForPerMonitorDPI(desktop, ref sample))
            {
                if (sample.X != 100 || sample.Y != 100)
                {
                    var sx = sample.X / 100.0;
                    var sy = sample.Y / 100.0;
                    return new CoordinateSpaceConverter(
                        "LogicalToPhysicalPointForPerMonitorDPI",
                        sx,
                        sy,
                        $"sample(100,100)->({sample.X},{sample.Y})",
                        preferLogicalToPhysicalApi: true);
                }
            }
        }
        catch
        {
            // fall through
        }

        // 2) Cursor logical vs physical ratio (measured, not hardcoded).
        try
        {
            if (NativeMethods.GetCursorPos(out var logical) &&
                NativeMethods.GetPhysicalCursorPos(out var physical) &&
                logical.X != 0 && logical.Y != 0 &&
                (logical.X != physical.X || logical.Y != physical.Y))
            {
                // Avoid unstable ratio near origin / multi-monitor negative origins: use DPI when possible.
                // If both positive and on same side of origin, ratio is usable.
                if (logical.X > 10 && logical.Y > 10 && physical.X > 10 && physical.Y > 10)
                {
                    var sx = physical.X / (double)logical.X;
                    var sy = physical.Y / (double)logical.Y;
                    if (sx is > 0.5 and < 8 && sy is > 0.5 and < 8)
                    {
                        return new CoordinateSpaceConverter(
                            "GetCursorPos_vs_GetPhysicalCursorPos",
                            sx,
                            sy,
                            $"logical=({logical.X},{logical.Y}) physical=({physical.X},{physical.Y})",
                            preferLogicalToPhysicalApi: false);
                    }
                }
            }
        }
        catch
        {
            // fall through
        }

        // 3) System DPI / 96 — measured from GetDpiForSystem, not a fixed 2×.
        try
        {
            var dpi = NativeMethods.GetDpiForSystem();
            if (dpi != 0 && dpi != 96)
            {
                var scale = dpi / 96.0;
                return new CoordinateSpaceConverter(
                    "GetDpiForSystem_over_96",
                    scale,
                    scale,
                    $"dpi_for_system={dpi}",
                    preferLogicalToPhysicalApi: false);
            }
        }
        catch
        {
            // fall through
        }

        // 4) Compare GetSystemMetrics vs GetSystemMetricsForDpi(systemDpi) when available.
        try
        {
            var dpi = NativeMethods.GetDpiForSystem();
            if (dpi != 0 && dpi != 96)
            {
                var logicalW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                var physicalW = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSCREEN, dpi);
                if (logicalW > 0 && physicalW > 0 && logicalW != physicalW)
                {
                    var scale = physicalW / (double)logicalW;
                    return new CoordinateSpaceConverter(
                        "GetSystemMetrics_vs_GetSystemMetricsForDpi",
                        scale,
                        scale,
                        $"cx_screen logical={logicalW} forDpi({dpi})={physicalW}",
                        preferLogicalToPhysicalApi: false);
                }
            }
        }
        catch
        {
            // fall through
        }

        return Identity;
    }

    public (int X, int Y) MetricsToHookSpace(int metricsX, int metricsY)
    {
        if (_preferLogicalToPhysicalApi)
        {
            try
            {
                var pt = new NativeMethods.POINT { X = metricsX, Y = metricsY };
                if (NativeMethods.LogicalToPhysicalPointForPerMonitorDPI(NativeMethods.GetDesktopWindow(), ref pt))
                    return (pt.X, pt.Y);
            }
            catch
            {
                // fall through to scale
            }
        }

        if (IsIdentity)
            return (metricsX, metricsY);

        return (
            (int)Math.Round(metricsX * ScaleX, MidpointRounding.AwayFromZero),
            (int)Math.Round(metricsY * ScaleY, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Map 0..65535 absolute axes into hook-aligned pixels using measured physical desktop size when scale ≠ 1.
    /// Preserves negative virtual origins. Does not hardcode 2×.
    /// </summary>
    public (int MetricsX, int MetricsY, int HookX, int HookY) MapAbsoluteAxes(
        int lastX,
        int lastY,
        bool virtualDesktop,
        MouseAbsoluteCoordinateTracker.ScreenMetrics metrics)
    {
        var left = virtualDesktop ? metrics.VirtualLeft : 0;
        var top = virtualDesktop ? metrics.VirtualTop : 0;
        var width = virtualDesktop ? metrics.VirtualWidth : metrics.PrimaryWidth;
        var height = virtualDesktop ? metrics.VirtualHeight : metrics.PrimaryHeight;
        if (width <= 0) width = 1;
        if (height <= 0) height = 1;

        var metricsX = MouseAbsoluteCoordinateTracker.MapNormalizedAxis(lastX, left, width);
        var metricsY = MouseAbsoluteCoordinateTracker.MapNormalizedAxis(lastY, top, height);

        if (IsIdentity)
            return (metricsX, metricsY, metricsX, metricsY);

        // Prefer mapping onto scaled desktop so 0/65535 land on physical edges, then verify with converter.
        var hookLeft = (int)Math.Round(left * ScaleX, MidpointRounding.AwayFromZero);
        var hookTop = (int)Math.Round(top * ScaleY, MidpointRounding.AwayFromZero);
        var hookWidth = Math.Max(1, (int)Math.Round(width * ScaleX, MidpointRounding.AwayFromZero));
        var hookHeight = Math.Max(1, (int)Math.Round(height * ScaleY, MidpointRounding.AwayFromZero));
        var hookX = MouseAbsoluteCoordinateTracker.MapNormalizedAxis(lastX, hookLeft, hookWidth);
        var hookY = MouseAbsoluteCoordinateTracker.MapNormalizedAxis(lastY, hookTop, hookHeight);

        if (_preferLogicalToPhysicalApi)
        {
            var api = MetricsToHookSpace(metricsX, metricsY);
            return (metricsX, metricsY, api.X, api.Y);
        }

        return (metricsX, metricsY, hookX, hookY);
    }

    public object Describe() => new
    {
        method = Method,
        scale_x = ScaleX,
        scale_y = ScaleY,
        is_identity = IsIdentity,
        detail = Detail,
        note = "Scale is measured at runtime; never assume 2.0."
    };
}
