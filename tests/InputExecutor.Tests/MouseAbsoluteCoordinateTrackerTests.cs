using InputExecutor.Windows;

namespace InputExecutor.Tests;

public sealed class MouseAbsoluteCoordinateTrackerTests
{
    private static MouseAbsoluteCoordinateTracker.ScreenMetrics Metrics => new(0, 0, 2000, 1000, 1920, 1080);

    [Fact]
    public void RelativePackets_PassThroughAndClearAbsoluteBaseline()
    {
        var tracker = new MouseAbsoluteCoordinateTracker();
        var firstAbs = tracker.Process(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute, 32767, 32767, Metrics);
        Assert.True(firstAbs.IsAbsolute);
        Assert.True(firstAbs.EstablishedBaselineOnly);

        var relative = tracker.Process(0, 4, -3, Metrics);
        Assert.False(relative.IsAbsolute);
        Assert.Equal(4, relative.Dx);
        Assert.Equal(-3, relative.Dy);

        var absAgain = tracker.Process(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute, 1000, 1000, Metrics);
        Assert.True(absAgain.EstablishedBaselineOnly);
        Assert.Equal(0, absAgain.Dx);
        Assert.Equal(0, absAgain.Dy);
    }

    [Fact]
    public void PrimaryDesktop_MapsZeroAnd65535ToInclusiveEdges()
    {
        var primary = new MouseAbsoluteCoordinateTracker.ScreenMetrics(0, 0, 3000, 2000, 1920, 1080);
        Assert.Equal(0, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(0, 0, primary.PrimaryWidth));
        Assert.Equal(1919, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, 0, primary.PrimaryWidth));
        Assert.Equal(0, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(0, 0, primary.PrimaryHeight));
        Assert.Equal(1079, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, 0, primary.PrimaryHeight));
    }

    [Fact]
    public void VirtualDesktop_NegativeOrigin_FourCornersAt0And65535()
    {
        // Virtual desktop: origin (-200,-100), size 2560x1440 → inclusive right/bottom = (2359, 1339)
        var left = -200;
        var top = -100;
        var width = 2560;
        var height = 1440;
        Assert.Equal(left, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(0, left, width));
        Assert.Equal(top, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(0, top, height));
        Assert.Equal(left + width - 1, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, left, width));
        Assert.Equal(top + height - 1, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, top, height));

        var flags = (ushort)(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute | MouseAbsoluteCoordinateTracker.MouseVirtualDesktop);
        var metrics = new MouseAbsoluteCoordinateTracker.ScreenMetrics(left, top, width, height, 1920, 1080);
        var tracker = new MouseAbsoluteCoordinateTracker();

        var tl = tracker.Process(flags, 0, 0, metrics);
        Assert.Equal(left, tl.ScreenX);
        Assert.Equal(top, tl.ScreenY);

        var tr = tracker.Process(flags, 65535, 0, metrics);
        Assert.Equal(left + width - 1, tr.ScreenX);
        Assert.Equal(top, tr.ScreenY);

        var br = tracker.Process(flags, 65535, 65535, metrics);
        Assert.Equal(left + width - 1, br.ScreenX);
        Assert.Equal(top + height - 1, br.ScreenY);

        var bl = tracker.Process(flags, 0, 65535, metrics);
        Assert.Equal(left, bl.ScreenX);
        Assert.Equal(top + height - 1, bl.ScreenY);
    }

    [Fact]
    public void Boundary65535_IsInclusiveEdge_NotBeyond()
    {
        Assert.Equal(99, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, 0, 100));
        Assert.Equal(0, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(0, 0, 100));
        // Over-range clamps to 65535 edge
        Assert.Equal(99, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(70000, 0, 100));
        Assert.Equal(0, MouseAbsoluteCoordinateTracker.MapNormalizedAxis(-10, 0, 100));
    }

    [Fact]
    public void AbsolutePackets_ComputeDeltaAfterBaseline_AndHonorVirtualDesktop()
    {
        var tracker = new MouseAbsoluteCoordinateTracker();
        var metrics = new MouseAbsoluteCoordinateTracker.ScreenMetrics(-100, -50, 3000, 2000, 1920, 1080);
        var flags = (ushort)(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute | MouseAbsoluteCoordinateTracker.MouseVirtualDesktop);

        var baseline = tracker.Process(flags, 0, 0, metrics);
        Assert.True(baseline.IsVirtualDesktop);
        Assert.True(baseline.EstablishedBaselineOnly);
        Assert.Equal(-100, baseline.ScreenX);
        Assert.Equal(-50, baseline.ScreenY);

        var mid = tracker.Process(flags, 32768, 0, metrics);
        Assert.False(mid.EstablishedBaselineOnly);
        Assert.True(mid.Dx > 0);
        Assert.Equal(0, mid.Dy);
    }

    [Fact]
    public void ScreenRangeChange_ResetsBaseline()
    {
        var tracker = new MouseAbsoluteCoordinateTracker();
        var a = new MouseAbsoluteCoordinateTracker.ScreenMetrics(0, 0, 1000, 1000, 800, 600);
        var b = new MouseAbsoluteCoordinateTracker.ScreenMetrics(0, 0, 2000, 1000, 800, 600);
        var flags = (ushort)(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute | MouseAbsoluteCoordinateTracker.MouseVirtualDesktop);

        Assert.True(tracker.Process(flags, 100, 100, a).EstablishedBaselineOnly);
        var afterResize = tracker.Process(flags, 100, 100, b);
        Assert.True(afterResize.EstablishedBaselineOnly);
        Assert.Equal(0, afterResize.Dx);
    }
}
