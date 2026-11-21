namespace InputExecutor.Windows;

/// <summary>
/// Converts RAWMOUSE absolute coordinates (0..65535) into screen pixels and relative deltas.
/// Relative packets pass through unchanged; absolute/relative mode switches reset the baseline.
/// Mapping uses (width-1)/(height-1) so 0 and 65535 land on the inclusive desktop edges,
/// including negative virtual-desktop origins.
/// </summary>
public sealed class MouseAbsoluteCoordinateTracker
{
    public const ushort MouseMoveAbsolute = 0x0001;
    public const ushort MouseVirtualDesktop = 0x0002;

    private int? _lastScreenX;
    private int? _lastScreenY;
    private bool _lastWasAbsolute;
    private int _originLeft;
    private int _originTop;
    private int _originWidth;
    private int _originHeight;
    private bool _haveOrigin;

    public readonly record struct ScreenMetrics(
        int VirtualLeft,
        int VirtualTop,
        int VirtualWidth,
        int VirtualHeight,
        int PrimaryWidth,
        int PrimaryHeight);

    public readonly record struct Result(
        int Dx,
        int Dy,
        bool IsAbsolute,
        bool IsVirtualDesktop,
        bool EstablishedBaselineOnly,
        int? ScreenX,
        int? ScreenY,
        int OriginLeft,
        int OriginTop,
        int OriginWidth,
        int OriginHeight);

    public Result Process(ushort usFlags, int lastX, int lastY, ScreenMetrics metrics)
    {
        var absolute = (usFlags & MouseMoveAbsolute) != 0;
        if (!absolute)
        {
            _lastWasAbsolute = false;
            _lastScreenX = null;
            _lastScreenY = null;
            _haveOrigin = false;
            return new Result(lastX, lastY, false, false, false, null, null, 0, 0, 0, 0);
        }

        var virtualDesktop = (usFlags & MouseVirtualDesktop) != 0;
        var left = virtualDesktop ? metrics.VirtualLeft : 0;
        var top = virtualDesktop ? metrics.VirtualTop : 0;
        var width = virtualDesktop ? metrics.VirtualWidth : metrics.PrimaryWidth;
        var height = virtualDesktop ? metrics.VirtualHeight : metrics.PrimaryHeight;
        if (width <= 0) width = 1;
        if (height <= 0) height = 1;

        var screenX = MapNormalizedAxis(lastX, left, width);
        var screenY = MapNormalizedAxis(lastY, top, height);

        var originChanged = !_haveOrigin
            || left != _originLeft
            || top != _originTop
            || width != _originWidth
            || height != _originHeight;
        var modeSwitch = !_lastWasAbsolute;
        _lastWasAbsolute = true;
        _originLeft = left;
        _originTop = top;
        _originWidth = width;
        _originHeight = height;
        _haveOrigin = true;

        if (originChanged || modeSwitch || _lastScreenX is null || _lastScreenY is null)
        {
            _lastScreenX = screenX;
            _lastScreenY = screenY;
            return new Result(0, 0, true, virtualDesktop, true, screenX, screenY, left, top, width, height);
        }

        var dx = screenX - _lastScreenX.Value;
        var dy = screenY - _lastScreenY.Value;
        _lastScreenX = screenX;
        _lastScreenY = screenY;
        return new Result(dx, dy, true, virtualDesktop, false, screenX, screenY, left, top, width, height);
    }

    /// <summary>
    /// Maps a 0..65535 axis into [origin, origin+size-1] inclusive.
    /// </summary>
    public static int MapNormalizedAxis(int normalized, int origin, int size)
    {
        if (size <= 0) size = 1;
        var span = size - 1;
        if (span <= 0)
            return origin;
        var clamped = normalized < 0 ? 0 : normalized > 65535 ? 65535 : normalized;
        return origin + (int)((long)clamped * span / 65535L);
    }

    public static ScreenMetrics CaptureFromSystem() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN));
}
