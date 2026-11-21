using InputExecutor.Windows;
using InputExecutor.Windows.RemoteInputDiagnostics;

namespace InputExecutor.Tests;

public sealed class RemoteInputDiagnosticsUnitTests
{
    [Fact]
    public void DiagnosticChannel_WaitPlusTryWrite_CountsDropsAccurately()
    {
        var channel = new DiagnosticChannel<int>(capacity: 2);
        Assert.True(channel.TryEnqueue(1));
        Assert.True(channel.TryEnqueue(2));
        Assert.False(channel.TryEnqueue(3));
        Assert.False(channel.TryEnqueue(4));
        Assert.Equal(2, channel.Enqueued);
        Assert.Equal(2, channel.Dropped);

        Assert.True(channel.Reader.TryRead(out var first));
        Assert.Equal(1, first);
        Assert.True(channel.TryEnqueue(5));
        Assert.Equal(3, channel.Enqueued);
        Assert.Equal(2, channel.Dropped);
        channel.Complete();
    }

    [Fact]
    public void Correlation_OneToOne_MarksAmbiguousOnTies()
    {
        var raw = new List<DiagnosticsCorrelation.RawEvent>
        {
            new(1_000, "0x0", false, 1, 0, 10, 10, 0, "move")
        };
        var hooks = new List<DiagnosticsCorrelation.HookEvent>
        {
            new(1_000, "WM_MOUSEMOVE", 10, 10, false),
            new(1_500, "WM_MOUSEMOVE", 10, 10, false)
        };
        var pairs = DiagnosticsCorrelation.MatchOneToOne(raw, hooks);
        Assert.Single(pairs);
        Assert.Equal("AMBIGUOUS", pairs[0].Status);
    }

    [Fact]
    public void Correlation_OneToOne_MatchesCompatibleKinds()
    {
        var raw = new List<DiagnosticsCorrelation.RawEvent>
        {
            new(1_000, "0x0", false, 0, 0, 10, 10, 0x0001, "left_button"),
            new(2_000, "0x0", false, 2, 0, 20, 10, 0, "move")
        };
        var hooks = new List<DiagnosticsCorrelation.HookEvent>
        {
            new(1_050, "WM_LBUTTONDOWN", 10, 10, true),
            new(2_100, "WM_MOUSEMOVE", 21, 10, true)
        };
        var pairs = DiagnosticsCorrelation.MatchOneToOne(raw, hooks);
        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Equal("MATCHED", p.Status));
        Assert.True(pairs[0].Hook!.Injected);
    }

    [Fact]
    public void Correlation_DoesNotCrossIncompatibleKinds()
    {
        var raw = new[] { new DiagnosticsCorrelation.RawEvent(1_000, "0x0", false, 1, 0, null, null, 0, "move") };
        var hooks = new[] { new DiagnosticsCorrelation.HookEvent(1_000, "WM_LBUTTONDOWN", 0, 0, true) };
        var pairs = DiagnosticsCorrelation.MatchOneToOne(raw, hooks);
        Assert.Equal("UNMATCHED_RAW", pairs[0].Status);
    }

    [Fact]
    public void ClassifyRawKind_AbsoluteZeroDelta_IsMove()
    {
        Assert.Equal("move", DiagnosticsCorrelation.ClassifyRawKind(0, 0, 0, absolute: true));
    }

    [Fact]
    public void ClassifyRawKind_ButtonFlags_WinOverAbsolute()
    {
        Assert.Equal("left_button", DiagnosticsCorrelation.ClassifyRawKind(0x0001, 0, 0, absolute: true));
        Assert.Equal("right_button", DiagnosticsCorrelation.ClassifyRawKind(0x0004, 0, 0, absolute: false));
        Assert.Equal("wheel", DiagnosticsCorrelation.ClassifyRawKind(0x0400, 0, 0, absolute: false));
    }

    [Fact]
    public void KindCompatible_Other_NeverMatches()
    {
        Assert.False(DiagnosticsCorrelation.KindCompatible("other", "WM_MOUSEMOVE"));
        Assert.False(DiagnosticsCorrelation.KindCompatible("other", "WM_LBUTTONDOWN"));
        Assert.False(DiagnosticsCorrelation.KindCompatible("other", "WM_MOUSEWHEEL"));
    }

    [Fact]
    public void Correlation_AbsoluteZeroDelta_MatchesMouseMove_NotButton()
    {
        var raw = new[]
        {
            new DiagnosticsCorrelation.RawEvent(1_000, "0x0", true, 0, 0, 100, 200, 0, "move")
        };
        var moveHooks = new[] { new DiagnosticsCorrelation.HookEvent(1_000, "WM_MOUSEMOVE", 100, 200, true) };
        var buttonHooks = new[] { new DiagnosticsCorrelation.HookEvent(1_000, "WM_LBUTTONDOWN", 100, 200, true) };

        var matched = DiagnosticsCorrelation.MatchOneToOne(raw, moveHooks);
        Assert.Equal("MATCHED", matched[0].Status);

        var unmatched = DiagnosticsCorrelation.MatchOneToOne(raw, buttonHooks);
        Assert.Equal("UNMATCHED_RAW", unmatched[0].Status);
    }

    [Fact]
    public void EmpiricalScale_FromAbsoluteRawVsHook_EnablesPositionMatch()
    {
        var raw = new List<DiagnosticsCorrelation.RawEvent>();
        var hooks = new List<DiagnosticsCorrelation.HookEvent>();
        for (var i = 0; i < 8; i++)
        {
            var sx = 900 + i * 20;
            var sy = 350 + i * 10;
            // Space well beyond DefaultTimeWindowUs (20ms) so pairs do not compete.
            var t = 100_000L + i * 50_000L;
            raw.Add(new DiagnosticsCorrelation.RawEvent(
                t, "0x0", true, 0, 0, sx, sy, 0, "move", sx, sy));
            hooks.Add(new DiagnosticsCorrelation.HookEvent(
                t, "WM_MOUSEMOVE", sx * 2, sy * 2, true));
        }

        var (converter, _) = DiagnosticsCorrelation.EstimateScaleFromAbsoluteRawAndHooks(raw, hooks);
        Assert.NotNull(converter);
        Assert.InRange(converter!.ScaleX, 1.9, 2.1);
        Assert.InRange(converter.ScaleY, 1.9, 2.1);

        var pairs = DiagnosticsCorrelation.MatchOneToOne(raw, hooks, aligner: converter);
        Assert.All(pairs, p => Assert.Equal("MATCHED", p.Status));
    }

    [Fact]
    public void Correlation_DpiScale_AlignsMetricsCoordsBeforePositionMatch()
    {
        // Observed UU shape: metrics ~852, hook ~1704 → measured scale 2.0 (not hardcoded in production path).
        var aligner = CoordinateSpaceConverter.FromMeasuredScale(2.0, 2.0, "unit_test_measured_scale", "simulates DPI gap");
        var unaligned = new[]
        {
            new DiagnosticsCorrelation.RawEvent(
                1_000, "0x0", true, 0, 0,
                ScreenX: 852, ScreenY: 436,
                ButtonFlags: 0, Kind: "move",
                ScreenXMetrics: 852, ScreenYMetrics: 436)
        };
        var needsAlign = new[]
        {
            new DiagnosticsCorrelation.RawEvent(
                1_000, "0x0", true, 0, 0,
                ScreenX: null, ScreenY: null,
                ButtonFlags: 0, Kind: "move",
                ScreenXMetrics: 852, ScreenYMetrics: 436)
        };
        var hooks = new[] { new DiagnosticsCorrelation.HookEvent(1_000, "WM_MOUSEMOVE", 1704, 872, true) };

        var without = DiagnosticsCorrelation.MatchOneToOne(unaligned, hooks);
        Assert.Equal("UNMATCHED_RAW", without[0].Status);

        var withAlign = DiagnosticsCorrelation.MatchOneToOne(needsAlign, hooks, aligner: aligner);
        Assert.Equal("MATCHED", withAlign[0].Status);
        Assert.Contains("position", withAlign[0].Reason);
    }

    [Fact]
    public void Correlation_DuplicatePosition_SameTimestamp_Ambiguous()
    {
        var raw = new[]
        {
            new DiagnosticsCorrelation.RawEvent(1_000, "0x0", true, 0, 0, 50, 50, 0, "move")
        };
        var hooks = new[]
        {
            new DiagnosticsCorrelation.HookEvent(1_000, "WM_MOUSEMOVE", 50, 50, true),
            new DiagnosticsCorrelation.HookEvent(1_000, "WM_MOUSEMOVE", 50, 50, true)
        };
        var pairs = DiagnosticsCorrelation.MatchOneToOne(raw, hooks);
        Assert.Equal("AMBIGUOUS", pairs[0].Status);
    }

    [Fact]
    public void Correlation_OneToOne_Conflict_SecondRawUnmatched()
    {
        var raw = new[]
        {
            new DiagnosticsCorrelation.RawEvent(1_000, "0x0", true, 0, 0, 10, 10, 0, "move"),
            new DiagnosticsCorrelation.RawEvent(1_500, "0x0", true, 0, 0, 10, 10, 0, "move")
        };
        var hooks = new[]
        {
            new DiagnosticsCorrelation.HookEvent(1_200, "WM_MOUSEMOVE", 10, 10, true)
        };
        var pairs = DiagnosticsCorrelation.MatchOneToOne(raw, hooks);
        Assert.Equal("MATCHED", pairs[0].Status);
        Assert.Equal("UNMATCHED_RAW", pairs[1].Status);
    }

    [Fact]
    public void AbsoluteMapping_WithMeasuredScale_AlignsWithoutHardcodedTwo()
    {
        var metrics = new MouseAbsoluteCoordinateTracker.ScreenMetrics(0, 0, 960, 540, 960, 540);
        var aligner = CoordinateSpaceConverter.FromMeasuredScale(2.0, 2.0, "unit_test_measured_scale");
        var tracker = new MouseAbsoluteCoordinateTracker();
        var flags = (ushort)(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute | MouseAbsoluteCoordinateTracker.MouseVirtualDesktop);

        // Mid-desktop in metrics space ~480,270 → hook space ~960,540
        var midNormX = (int)(480L * 65535 / 959);
        var midNormY = (int)(270L * 65535 / 539);
        var result = tracker.Process(flags, midNormX, midNormY, metrics, aligner);
        Assert.True(result.IsAbsolute);
        Assert.NotNull(result.ScreenXMetrics);
        Assert.NotNull(result.ScreenX);
        Assert.True(Math.Abs(result.ScreenX!.Value - result.ScreenXMetrics!.Value * 2) <= 3);
        Assert.Equal("unit_test_measured_scale", result.AlignmentMethod);
    }

    [Fact]
    public void AbsoluteMapping_VirtualDesktop_NegativeOrigin_PreservedInMetricsAndScaledHook()
    {
        var metrics = new MouseAbsoluteCoordinateTracker.ScreenMetrics(-200, -100, 1280, 720, 960, 540);
        var aligner = CoordinateSpaceConverter.FromMeasuredScale(1.5, 1.5, "unit_test_measured_scale");
        var tracker = new MouseAbsoluteCoordinateTracker();
        var flags = (ushort)(MouseAbsoluteCoordinateTracker.MouseMoveAbsolute | MouseAbsoluteCoordinateTracker.MouseVirtualDesktop);
        var tl = tracker.Process(flags, 0, 0, metrics, aligner);
        Assert.Equal(-200, tl.ScreenXMetrics);
        Assert.Equal(-100, tl.ScreenYMetrics);
        Assert.Equal(-300, tl.ScreenX);
        Assert.Equal(-150, tl.ScreenY);
    }

    [Fact]
    public void AbsoluteMapping_DifferentMonitorScale_UsesMeasuredXYIndependently()
    {
        var metrics = new MouseAbsoluteCoordinateTracker.ScreenMetrics(0, 0, 1000, 1000, 1000, 1000);
        var aligner = CoordinateSpaceConverter.FromMeasuredScale(1.25, 1.5, "unit_test_per_axis");
        var tracker = new MouseAbsoluteCoordinateTracker();
        var flags = MouseAbsoluteCoordinateTracker.MouseMoveAbsolute;
        var r = tracker.Process(flags, 0, 0, metrics, aligner);
        Assert.Equal(0, r.ScreenXMetrics);
        Assert.Equal(0, r.ScreenYMetrics);
        Assert.Equal(0, r.ScreenX);
        Assert.Equal(0, r.ScreenY);

        var br = tracker.Process(flags, 65535, 65535, metrics, aligner);
        Assert.Equal(999, br.ScreenXMetrics);
        Assert.Equal(999, br.ScreenYMetrics);
        // Hook space maps onto scaled desktop edges (size*scale), not naive metrics*scale.
        Assert.Equal(MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, 0, (int)Math.Round(1000 * 1.25)), br.ScreenX);
        Assert.Equal(MouseAbsoluteCoordinateTracker.MapNormalizedAxis(65535, 0, (int)Math.Round(1000 * 1.5)), br.ScreenY);
    }

    [Fact]
    public void StimulusVerdict_MoveOnly_FailsOnWheelContamination()
    {
        var raw = Array.Empty<DiagnosticsCorrelation.RawEvent>();
        var hooks = new[]
        {
            new DiagnosticsCorrelation.HookEvent(1, "WM_MOUSEMOVE", 0, 0, true),
            new DiagnosticsCorrelation.HookEvent(2, "WM_MOUSEWHEEL", 0, 0, true)
        };
        var v = StimulusVerdict.Evaluate("test-a-uu-move", raw, hooks);
        Assert.Equal("FAIL", v.Status);
        Assert.Contains("CONTAMINATED", v.Reason);
    }

    [Fact]
    public void StimulusVerdict_Drag_RequiresMoveBetweenDownUp()
    {
        var hooksClick = new[]
        {
            new DiagnosticsCorrelation.HookEvent(1, "WM_LBUTTONDOWN", 100, 100, true),
            new DiagnosticsCorrelation.HookEvent(2, "WM_LBUTTONUP", 100, 100, true)
        };
        Assert.Equal("FAIL", StimulusVerdict.Evaluate("test-d-uu-drag-select", Array.Empty<DiagnosticsCorrelation.RawEvent>(), hooksClick).Status);

        var hooksDrag = new[]
        {
            new DiagnosticsCorrelation.HookEvent(1, "WM_LBUTTONDOWN", 100, 100, true),
            new DiagnosticsCorrelation.HookEvent(2, "WM_MOUSEMOVE", 250, 100, true),
            new DiagnosticsCorrelation.HookEvent(3, "WM_MOUSEMOVE", 400, 100, true),
            new DiagnosticsCorrelation.HookEvent(4, "WM_LBUTTONUP", 400, 100, true)
        };
        var pass = StimulusVerdict.Evaluate("test-d-uu-drag-select", Array.Empty<DiagnosticsCorrelation.RawEvent>(), hooksDrag);
        Assert.Equal("PASS", pass.Status);
    }

    [Fact]
    public void DeviceSnapshotCompare_ReportsAddedRemovedWithoutClaimingUu()
    {
        var before = new SessionEnvironmentSnapshot
        {
            CapturedAtUtc = "before",
            VirtualScreen = new { },
            PrimaryScreen = new { },
            RawInputDevices =
            [
                new RawInputDeviceInfoDto { DeviceHandle = "0x1", DeviceType = "mouse", DeviceNameRedacted = "A", CandidateClass = DeviceCandidateClass.Unknown }
            ]
        };
        var after = new SessionEnvironmentSnapshot
        {
            CapturedAtUtc = "after",
            VirtualScreen = new { },
            PrimaryScreen = new { },
            RawInputDevices =
            [
                new RawInputDeviceInfoDto { DeviceHandle = "0x2", DeviceType = "mouse", DeviceNameRedacted = "HID_DEVICE_SYSTEM_VHF", CandidateClass = DeviceCandidateClass.VhfDeviceCandidate }
            ]
        };
        var json = System.Text.Json.JsonSerializer.Serialize(DeviceSnapshotCompare.Compare(before, after));
        Assert.Contains("0x2", json);
        Assert.Contains("0x1", json);
        Assert.Contains("must NOT be asserted", json);
    }

    [Theory]
    [InlineData("\\\\?\\HID#HID_DEVICE_SYSTEM_VHF#1", DeviceCandidateClass.VhfDeviceCandidate)]
    [InlineData("\\\\?\\VIRTUAL_DIGITIZER", DeviceCandidateClass.VirtualHidCandidate)]
    [InlineData("\\\\?\\HID#VID_046D&PID_C52B", DeviceCandidateClass.Unknown)]
    [InlineData(null, DeviceCandidateClass.Unknown)]
    public void DeviceCandidate_ClassifiesWithoutAssertingUu(string? name, string expected)
    {
        Assert.Equal(expected, SessionEnvironmentSnapshot.ClassifyDeviceCandidate(name));
    }
}
