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

        // Half of 65535 ≈ mid desktop
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
