using System.Diagnostics;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Scheduler;

namespace InputExecutor.Tests;

public sealed class CoordinatorSafetyTests
{
    [Fact]
    public async Task ExactProcessBinding_UsesResolvedMainWindowIdentity()
    {
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard();
        var clock = new FakeClock();
        await using var scheduler = new InputScheduler(backend, clock);
        await using var coordinator = new InputCoordinator(scheduler, guard, clock, new NullTrajectorySink());
        var created = await coordinator.CreateSessionAsync(new CreateSessionRequest
        {
            BindToForeground = false,
            TargetProcessId = 777
        }, default);

        Assert.Equal(777, created.Status.Target.ProcessId);
        Assert.Contains("test-game", created.Status.AllowedProcesses);
    }

    [Fact]
    public async Task ExplicitFocus_ActivatesOnlyBoundTargetAndVerifiesForeground()
    {
        var backend = new RecordingBackend();
        var guard = new FakeTargetGuard { Foreground = false };
        var clock = new FakeClock();
        await using var scheduler = new InputScheduler(backend, clock);
        await using var coordinator = new InputCoordinator(scheduler, guard, clock, new NullTrajectorySink());
        var created = await coordinator.CreateSessionAsync(new CreateSessionRequest
        {
            Target = guard.Target,
            BindToForeground = false
        }, default);

        var status = await coordinator.FocusTargetAsync(created.SessionId, created.CapabilityToken, default);

        Assert.True(guard.Foreground);
        Assert.Equal(guard.Target.WindowHandle, status.Target.WindowHandle);
    }

    [Fact]
    public async Task RealtimeDisconnect_DisarmsAndReleasesAll()
    {
        var fixture = await Fixture.CreateAsync();
        await using var cleanup = fixture;
        var connection = fixture.Coordinator.OpenRealtimeConnection(fixture.SessionId, fixture.Token);
        await fixture.Coordinator.SubmitRealtimeFrameAsync(fixture.SessionId, fixture.Token, connection, Frame(1, "KeyW"), default);

        await fixture.Coordinator.CloseRealtimeConnectionAsync(fixture.SessionId, fixture.Token, connection, "test_disconnect");

        var status = await fixture.Coordinator.GetStatusAsync(fixture.SessionId, fixture.Token, default);
        Assert.False(status.Armed);
        Assert.True(status.Paused);
        Assert.Contains(fixture.Backend.Calls, x => x.Detail == "KeyW:up");
    }

    [Fact]
    public async Task TargetFocusLoss_WatchdogReleasesHeldInputs()
    {
        var fixture = await Fixture.CreateAsync();
        await using var cleanup = fixture;
        var connection = fixture.Coordinator.OpenRealtimeConnection(fixture.SessionId, fixture.Token);
        await fixture.Coordinator.SubmitRealtimeFrameAsync(fixture.SessionId, fixture.Token, connection, Frame(1, "KeyW"), default);
        fixture.Guard.Foreground = false;

        await fixture.Coordinator.InspectWatchdogsAsync(default);

        var status = await fixture.Coordinator.GetStatusAsync(fixture.SessionId, fixture.Token, default);
        Assert.False(status.Armed);
        Assert.Equal("target_not_foreground", status.PauseReason);
        Assert.Contains(fixture.Backend.Calls, x => x.Detail == "KeyW:up");
    }

    [Fact]
    public async Task BestEffortAbnormalShutdown_ReleasesHeldInputs()
    {
        var fixture = await Fixture.CreateAsync();
        await using var cleanup = fixture;
        var connection = fixture.Coordinator.OpenRealtimeConnection(fixture.SessionId, fixture.Token);
        await fixture.Coordinator.SubmitRealtimeFrameAsync(fixture.SessionId, fixture.Token, connection, Frame(1, "KeyW"), default);

        var released = fixture.Coordinator.BestEffortEmergencyStop("unhandled_exception", TimeSpan.FromSeconds(2));

        Assert.True(released);
        Assert.Contains(fixture.Backend.Calls, x => x.Detail == "KeyW:up");
    }

    [Fact]
    public async Task BoundClientExit_WatchdogDisarmsAndReleases()
    {
        using var client = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;
        try
        {
            var fixture = await Fixture.CreateAsync(client.Id);
            await using var cleanup = fixture;
            var connection = fixture.Coordinator.OpenRealtimeConnection(fixture.SessionId, fixture.Token);
            await fixture.Coordinator.SubmitRealtimeFrameAsync(fixture.SessionId, fixture.Token, connection, Frame(1, "KeyW"), default);
            client.Kill(entireProcessTree: true);
            await client.WaitForExitAsync();

            await fixture.Coordinator.InspectWatchdogsAsync(default);

            var status = await fixture.Coordinator.GetStatusAsync(fixture.SessionId, fixture.Token, default);
            Assert.False(status.Armed);
            Assert.Equal("client_process_exited", status.PauseReason);
            Assert.Contains(fixture.Backend.Calls, x => x.Detail == "KeyW:up");
        }
        finally
        {
            if (!client.HasExited) client.Kill(entireProcessTree: true);
        }
    }

    private static RealtimeFrame Frame(long sequence, params string[] keys) => new()
    {
        Sequence = sequence,
        TimestampUs = sequence * 1_000,
        DeadlineUs = long.MaxValue,
        Keyboard = new KeyboardFrame { Held = keys },
        Mouse = new MouseFrame()
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public required RecordingBackend Backend { get; init; }
        public required FakeTargetGuard Guard { get; init; }
        public required InputScheduler Scheduler { get; init; }
        public required InputCoordinator Coordinator { get; init; }
        public required string SessionId { get; init; }
        public required string Token { get; init; }

        public static async Task<Fixture> CreateAsync(int? clientProcessId = null)
        {
            var backend = new RecordingBackend();
            var guard = new FakeTargetGuard();
            var clock = new FakeClock();
            var scheduler = new InputScheduler(backend, clock);
            var coordinator = new InputCoordinator(scheduler, guard, clock, new NullTrajectorySink());
            var created = await coordinator.CreateSessionAsync(new CreateSessionRequest
            {
                Target = guard.Target,
                BindToForeground = false,
                ClientProcessId = clientProcessId
            }, default);
            await coordinator.ArmAsync(created.SessionId, created.CapabilityToken, new ArmRequest { DurationSeconds = 60 }, default);
            return new Fixture
            {
                Backend = backend,
                Guard = guard,
                Scheduler = scheduler,
                Coordinator = coordinator,
                SessionId = created.SessionId,
                Token = created.CapabilityToken
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Scheduler.DisposeAsync();
        }
    }
}
