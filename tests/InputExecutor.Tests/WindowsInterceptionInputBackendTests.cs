using InputExecutor.Core.Execution;
using InputExecutor.Windows;

namespace InputExecutor.Tests;

public sealed class WindowsInterceptionInputBackendTests
{
    [Fact]
    public async Task RelativeMouseButtonsAndWheelUseRawDriverWhileKeysUseFallback()
    {
        var fallback = new RecordingBackend();
        var api = new FakeInterceptionApi { AvailableDevice = 13 };
        using var backend = new WindowsInterceptionInputBackend(fallback, api, new FakeClock());

        await backend.MoveMouseRelativeAsync(17, -9, true, default);
        await backend.SetMouseButtonAsync("right", true, default);
        await backend.SetMouseButtonAsync("right", false, default);
        await backend.ScrollMouseAsync(-120, 240, default);
        await backend.SetKeyAsync("KeyW", true, default);
        await backend.SetKeyAsync("KeyW", false, default);

        Assert.Equal("windows-interception-mouse:13+sendinput", backend.Name);
        Assert.Equal(5, api.Strokes.Count);
        Assert.Equal((17, -9), (api.Strokes[0].X, api.Strokes[0].Y));
        Assert.Equal((ushort)0x0008, api.Strokes[0].Flags);
        Assert.Equal((ushort)0x0004, api.Strokes[1].State);
        Assert.Equal((ushort)0x0008, api.Strokes[2].State);
        Assert.Contains(api.Strokes, stroke => stroke.State == 0x0400 && stroke.Rolling == 240);
        Assert.Contains(api.Strokes, stroke => stroke.State == 0x0800 && stroke.Rolling == -120);
        Assert.Equal(new[] { "KeyW:down", "KeyW:up" }, fallback.Calls.Select(call => call.Detail));
        Assert.All(api.Devices, device => Assert.Equal(13, device));
    }

    [Fact]
    public void MissingDriverContextFailsExplicitlyInsteadOfFallingBackToSendInput()
    {
        var error = Assert.Throws<InputExecutorException>(() =>
            new WindowsInterceptionInputBackend(new RecordingBackend(), new FakeInterceptionApi(), new FakeClock()));

        Assert.Equal("RAW_MOUSE_DRIVER_UNAVAILABLE", error.Code);
    }

    private sealed class FakeInterceptionApi : IInterceptionApi
    {
        internal int AvailableDevice { get; init; }
        internal List<InterceptionMouseStroke> Strokes { get; } = [];
        internal List<int> Devices { get; } = [];

        public IntPtr CreateContext() => AvailableDevice == 0 ? IntPtr.Zero : new IntPtr(123);
        public void DestroyContext(IntPtr context) { }
        public uint GetHardwareId(IntPtr context, int device, IntPtr buffer, uint bufferSize) =>
            device == AvailableDevice ? 16u : 0u;
        public int Send(IntPtr context, int device, ref InterceptionMouseStroke stroke, uint strokeCount)
        {
            Devices.Add(device);
            Strokes.Add(stroke);
            return 1;
        }
    }
}
