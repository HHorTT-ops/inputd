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
