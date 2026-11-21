using System.Net;
using System.Text;
using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace InputExecutor.IntegrationTests;

public sealed class PhysicalMouseSseTests
{
    [Fact]
    public async Task PhysicalMouseSse_RequiresAccessToken()
    {
        using var factory = new PhysicalMouseSseWebApplicationFactory();
        using var client = factory.CreateClient();
        var denied = await client.GetAsync("/v1/physical-input/events");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public void Hub_BroadcastsMouse_FiltersKeyboard_AndIgnoresTaggedInjectionOnFramePath()
    {
        using var hub = new PhysicalMouseEventHub();
        using var subscription = hub.Subscribe(capacity: 8);
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 11,
            Mouse = new MouseFrame { Dx = 2, Dy = -3, HeldButtons = new[] { "left" }, WheelY = 120 }
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = 12,
            ChangedKey = "KeyA",
            KeyDown = true
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 13,
            InjectedTagged = true,
            Mouse = new MouseFrame { Dx = 9, Dy = 9 }
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse_hook_injected",
            TimestampUs = 14,
            InjectedTagged = true,
            Mouse = new MouseFrame { Dx = 1, Dy = 1 }
        });

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal("mouse", first.EventName);
        Assert.Equal(2, first.Mouse!.Dx);
        Assert.Equal(-3, first.Mouse.Dy);
        Assert.Equal(new[] { "left" }, first.Mouse.HeldButtons);
        Assert.Equal(120, first.Mouse.WheelY);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public void Hub_TaggedObservationIncrementsDiagnosticCounter_WithoutMouseEvent()
    {
        using var hub = new PhysicalMouseEventHub();
        using var subscription = hub.Subscribe();
        hub.ObserveTaggedFrame();
        hub.ObserveTaggedFrame();
        Assert.Equal(2, hub.TaggedFramesSeen);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public void Hub_OverflowFaultsSubscriber_WithoutSilentDrop()
    {
        using var hub = new PhysicalMouseEventHub();
        using var subscription = hub.Subscribe(capacity: 1);
        hub.PublishFrame(new HumanInputFrame { Device = "mouse", TimestampUs = 1, Mouse = new MouseFrame { Dx = 1 } });
        hub.PublishFrame(new HumanInputFrame { Device = "mouse", TimestampUs = 2, Mouse = new MouseFrame { Dx = 2 } });

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal("mouse", first.EventName);
        Assert.True(subscription.Overflowed);
        Assert.Equal(0, hub.SubscriberCount);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task PhysicalMouseSse_ReceivesPublishedMouseWithoutRecordingStart()
    {
        using var factory = new PhysicalMouseSseWebApplicationFactory();
        Assert.Equal(0, factory.Source.StartRecordingCalls);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/physical-input/events");
        request.Headers.Add("X-Inputd-Access-Token", PhysicalMouseSseWebApplicationFactory.AccessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Drain the initial heartbeat if present, then raise one physical mouse frame.
        factory.Source.RaiseFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 42,
            Mouse = new MouseFrame { Dx = 5, Dy = -1, WheelX = 0, WheelY = 0 }
        });

        var evt = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        if (evt.EventName == "heartbeat")
            evt = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("mouse", evt.EventName);
        Assert.Equal(5, evt.Data.GetProperty("dx").GetInt32());
        Assert.Equal(-1, evt.Data.GetProperty("dy").GetInt32());
        Assert.Equal(42, evt.Data.GetProperty("timestamp_us").GetInt64());
        Assert.Equal(0, factory.Source.StartRecordingCalls);
    }

    [Fact]
    public async Task PhysicalMouseSse_ClientDisconnect_ReleasesSubscription()
    {
        using var factory = new PhysicalMouseSseWebApplicationFactory();
        var hub = factory.Services.GetRequiredService<PhysicalMouseEventHub>();
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/physical-input/events");
        request.Headers.Add("X-Inputd-Access-Token", PhysicalMouseSseWebApplicationFactory.AccessToken);
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitUntilAsync(() => hub.SubscriberCount >= 1, TimeSpan.FromSeconds(3));
        cts.Cancel();
        response.Dispose();
        await WaitUntilAsync(() => hub.SubscriberCount == 0, TimeSpan.FromSeconds(3));
    }

    private static async Task<(string EventName, JsonElement Data)> ReadNextEventAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string? eventName = null;
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) throw new InvalidOperationException("SSE stream ended.");
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var json = line["data:".Length..].Trim();
                using var doc = JsonDocument.Parse(json);
                return (eventName ?? "message", doc.RootElement.Clone());
            }
        }
        throw new TimeoutException("Timed out waiting for SSE event.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition not met.");
    }
}

internal sealed class PhysicalMouseSseWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AccessToken = "test-access-token-0123456789-abcdefghijk";
    public FakePhysicalInputObservationSource Source { get; } = new();

    public PhysicalMouseSseWebApplicationFactory() => Environment.SetEnvironmentVariable("INPUTD_ACCESS_TOKEN", AccessToken);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInputBackend>();
            services.RemoveAll<ITargetGuard>();
            services.RemoveAll<ITrajectorySink>();
            services.RemoveAll<IPhysicalInputObservationSource>();
            var emergency = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(EmergencyStopWorker));
            if (emergency is not null) services.Remove(emergency);
            services.AddSingleton<IInputBackend>(new TestBackend());
            services.AddSingleton<ITargetGuard>(new TestTargetGuard());
            services.AddSingleton<ITrajectorySink, NullTrajectorySink>();
            services.AddSingleton<IPhysicalInputObservationSource>(Source);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("INPUTD_ACCESS_TOKEN", null);
    }
}

internal sealed class FakePhysicalInputObservationSource : IPhysicalInputObservationSource
{
    public int StartRecordingCalls => 0;

    public event EventHandler<HumanInputFrame>? FrameObserved;
    public event EventHandler<HumanInputFrame>? TaggedRawFrameObserved;

    public void RaiseFrame(HumanInputFrame frame) => FrameObserved?.Invoke(this, frame);
    public void RaiseTagged(HumanInputFrame frame) => TaggedRawFrameObserved?.Invoke(this, frame);
}
