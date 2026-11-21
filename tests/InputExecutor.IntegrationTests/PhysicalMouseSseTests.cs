using System.Net;
using System.Text;
using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    public void Hub_BroadcastsMouseAndKeyActivity_IgnoresTaggedInjection_AndNeverLeaksKeyIdentity()
    {
        using var hub = new PhysicalMouseEventHub();
        using var subscription = hub.Subscribe(capacity: 8);
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 11,
            RawDeviceHandle = "0xABC",
            Mouse = new MouseFrame { Dx = 2, Dy = -3, HeldButtons = new[] { "left" }, WheelY = 120 }
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = 12,
            RawDeviceHandle = "0x10",
            ChangedKey = "KeyA",
            KeyDown = true,
            Keyboard = new KeyboardFrame { Held = new[] { "KeyA", "KeyB" } }
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
            Device = "keyboard",
            TimestampUs = 14,
            InjectedTagged = true,
            ChangedKey = "KeyZ",
            KeyDown = true
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse_hook_injected",
            TimestampUs = 15,
            InjectedTagged = true,
            Mouse = new MouseFrame { Dx = 1, Dy = 1 }
        });

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal("mouse", first.EventName);
        Assert.Equal(2, first.Mouse!.Dx);
        Assert.Equal(-3, first.Mouse.Dy);
        Assert.Equal(new[] { "left" }, first.Mouse.HeldButtons);
        Assert.Equal(120, first.Mouse.WheelY);
        Assert.Equal(InputSourceClass.DeviceAttributed, first.Mouse.SourceClass);
        Assert.Equal("0xABC", first.Mouse.RawDeviceHandle);

        Assert.True(subscription.Reader.TryRead(out var keyEvt));
        Assert.Equal("key_activity", keyEvt.EventName);
        Assert.NotNull(keyEvt.KeyActivity);
        Assert.Equal("down", keyEvt.KeyActivity!.Edge);
        Assert.Equal(InputSourceClass.DeviceAttributed, keyEvt.KeyActivity.SourceClass);
        Assert.Equal("0x10", keyEvt.KeyActivity.RawDeviceHandle);
        Assert.Equal(12, keyEvt.KeyActivity.TimestampUs);
        // Privacy: DTO must not carry ChangedKey / held keys (only Edge + attribution fields).
        Assert.Null(keyEvt.Mouse);

        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public void Hub_KeyActivity_ClassifiesZeroHandleAsUnattributed_AndOmitsEdgeWithoutKeyDown()
    {
        using var hub = new PhysicalMouseEventHub();
        using var subscription = hub.Subscribe(capacity: 4);
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = 1,
            RawDeviceHandle = "0x0",
            ChangedKey = "Secret",
            KeyDown = false
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = 2,
            ChangedKey = "Secret2"
            // KeyDown null ? ignored
        });

        Assert.True(subscription.Reader.TryRead(out var up));
        Assert.Equal("key_activity", up.EventName);
        Assert.Equal("up", up.KeyActivity!.Edge);
        Assert.Equal(InputSourceClass.Unattributed, up.KeyActivity.SourceClass);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public void Hub_ClassifiesZeroDeviceHandleAsUnattributed_AndNonZeroAsDeviceAttributed()
    {
        using var hub = new PhysicalMouseEventHub();
        using var subscription = hub.Subscribe(capacity: 4);

        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 1,
            RawDeviceHandle = "0x0",
            Mouse = new MouseFrame { Dx = 1 }
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 2,
            RawDeviceHandle = null,
            Mouse = new MouseFrame { Dx = 2 }
        });
        hub.PublishFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 3,
            RawDeviceHandle = "0x1A2B",
            Mouse = new MouseFrame { Dx = 3 }
        });

        Assert.True(subscription.Reader.TryRead(out var unattributedZero));
        Assert.Equal(InputSourceClass.Unattributed, unattributedZero.Mouse!.SourceClass);
        Assert.True(subscription.Reader.TryRead(out var unattributedNull));
        Assert.Equal(InputSourceClass.Unattributed, unattributedNull.Mouse!.SourceClass);
        Assert.True(subscription.Reader.TryRead(out var physical));
        Assert.Equal(InputSourceClass.DeviceAttributed, physical.Mouse!.SourceClass);
        Assert.Equal("0x1A2B", physical.Mouse.RawDeviceHandle);
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
    public void InputSourceClass_DoesNotTreatExtraInfoZeroAsDeviceAttributed()
    {
        // ExtraInformation == 0 alone is not classification input; device handle decides.
        var untaggedInjected = new HumanInputFrame
        {
            Device = "mouse",
            InjectedTagged = false,
            RawDeviceHandle = "0x0",
            Mouse = new MouseFrame { Dx = 1 }
        };
        Assert.Equal(InputSourceClass.Unattributed, InputSourceClass.Classify(untaggedInjected));

        var tagged = new HumanInputFrame
        {
            Device = "tagged_raw_mouse",
            InjectedTagged = true,
            RawDeviceHandle = "0x0",
            Mouse = new MouseFrame { Dx = 1 }
        };
        Assert.Equal(InputSourceClass.ExecutorInjected, InputSourceClass.Classify(tagged));
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

        factory.Source.RaiseFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 42,
            RawDeviceHandle = "0x55",
            Mouse = new MouseFrame { Dx = 5, Dy = -1, WheelX = 0, WheelY = 0 }
        });

        var evt = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        if (evt.EventName == "heartbeat")
            evt = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("mouse", evt.EventName);
        Assert.Equal(5, evt.Data.GetProperty("dx").GetInt32());
        Assert.Equal(-1, evt.Data.GetProperty("dy").GetInt32());
        Assert.Equal(42, evt.Data.GetProperty("timestamp_us").GetInt64());
        Assert.Equal(InputSourceClass.DeviceAttributed, evt.Data.GetProperty("source_class").GetString());
        Assert.Equal("0x55", evt.Data.GetProperty("raw_device_handle").GetString());
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

    [Fact]
    public async Task PhysicalMouseSse_EventsPublishedDuringConnectAreNotLost()
    {
        // Subscribe happens before response flush. Once headers are readable, the hub already
        // has this subscriber, so frames published before the client reads the body must arrive.
        using var factory = new PhysicalMouseSseWebApplicationFactory();
        var hub = factory.Services.GetRequiredService<PhysicalMouseEventHub>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/physical-input/events");
        request.Headers.Add("X-Inputd-Access-Token", PhysicalMouseSseWebApplicationFactory.AccessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitUntilAsync(() => hub.SubscriberCount >= 1, TimeSpan.FromSeconds(3));

        const int published = 32;
        for (var i = 0; i < published; i++)
        {
            factory.Source.RaiseFrame(new HumanInputFrame
            {
                Device = "mouse",
                TimestampUs = 1000 + i,
                RawDeviceHandle = "0x10",
                Mouse = new MouseFrame { Dx = i + 1, Dy = 0 }
            });
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var seen = new List<int>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (seen.Count < published && DateTime.UtcNow < deadline)
        {
            var evt = await ReadNextEventAsync(reader, deadline - DateTime.UtcNow);
            if (evt.EventName == "heartbeat")
                continue;
            Assert.Equal("mouse", evt.EventName);
            seen.Add(evt.Data.GetProperty("dx").GetInt32());
        }

        Assert.Equal(published, seen.Count);
        Assert.Equal(Enumerable.Range(1, published).ToArray(), seen.ToArray());
    }

    [Fact]
    public async Task PhysicalMouseSseHandler_OverflowWritesFault_ViaRealChannelBackpressure()
    {
        // Real Handler test (not TestServer TCP backpressure):
        // 1) wait for initial heartbeat fully written
        // 2) block subsequent response writes
        // 3) publish until Broadcast hits real Channel TryWrite overflow
        // 4) release writes and assert both event:mouse and event:fault/OVERFLOW
        const string token = PhysicalMouseSseWebApplicationFactory.AccessToken;
        var previous = Environment.GetEnvironmentVariable("INPUTD_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("INPUTD_ACCESS_TOKEN", token);
        try
        {
            var accessToken = new ServiceAccessToken();
            using var hub = new PhysicalMouseEventHub(defaultCapacity: 1);
            var clock = new HandlerTestClock { NowMicroseconds = 9_001 };
            await using var body = new GatedResponseBodyStream();
            using var requestAborted = new CancellationTokenSource();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Inputd-Access-Token"] = token;
            context.Response.Body = body;
            context.RequestAborted = requestAborted.Token;

            var handlerTask = PhysicalMouseSseEndpoint.HandleAsync(context, hub, accessToken, clock);

            await WaitUntilAsync(
                () =>
                {
                    var text = body.Utf8Text;
                    return text.Contains("event: heartbeat", StringComparison.Ordinal)
                           && text.Contains("tagged_frames_seen", StringComparison.Ordinal)
                           && text.Contains("\n\n", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));
            Assert.Equal(1, hub.SubscriberCount);
            Assert.DoesNotContain("event: mouse", body.Utf8Text, StringComparison.Ordinal);
            Assert.DoesNotContain("event: fault", body.Utf8Text, StringComparison.Ordinal);

            body.BlockSubsequentWrites();

            hub.PublishFrame(new HumanInputFrame
            {
                Device = "mouse",
                TimestampUs = 1,
                RawDeviceHandle = "0x0",
                Mouse = new MouseFrame { Dx = 11 }
            });
            await WaitUntilAsync(() => body.PendingWriteCount >= 1, TimeSpan.FromSeconds(3));

            hub.PublishFrame(new HumanInputFrame
            {
                Device = "mouse",
                TimestampUs = 2,
                RawDeviceHandle = "0x0",
                Mouse = new MouseFrame { Dx = 22 }
            });
            // Capacity 1 + reader blocked in WriteSseAsync => third publish fails TryWrite.
            hub.PublishFrame(new HumanInputFrame
            {
                Device = "mouse",
                TimestampUs = 3,
                RawDeviceHandle = "0x0",
                Mouse = new MouseFrame { Dx = 33 }
            });

            await WaitUntilAsync(() => hub.SubscriberCount == 0, TimeSpan.FromSeconds(3));
            body.ReleaseWrites();

            await handlerTask.WaitAsync(TimeSpan.FromSeconds(5));
            var sse = body.Utf8Text;
            Assert.Contains("event: heartbeat", sse, StringComparison.Ordinal);
            Assert.Contains("event: mouse", sse, StringComparison.Ordinal);
            Assert.Contains("\"dx\":11", sse, StringComparison.Ordinal);
            Assert.Contains("event: fault", sse, StringComparison.Ordinal);
            Assert.Contains("OVERFLOW", sse, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INPUTD_ACCESS_TOKEN", previous);
        }
    }
    [Fact]
    public async Task PhysicalMouseSse_KeyActivityPublishedWithoutKeyIdentityFields()
    {
        using var factory = new PhysicalMouseSseWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/physical-input/events");
        request.Headers.Add("X-Inputd-Access-Token", PhysicalMouseSseWebApplicationFactory.AccessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var first = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("heartbeat", first.EventName);

        factory.Source.RaiseFrame(new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = 77,
            RawDeviceHandle = "0x0",
            ChangedKey = "KeyQ",
            KeyDown = true,
            Keyboard = new KeyboardFrame { Held = new[] { "KeyQ" } }
        });

        var evt = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("key_activity", evt.EventName);
        Assert.Equal(77, evt.Data.GetProperty("timestamp_us").GetInt64());
        Assert.Equal("down", evt.Data.GetProperty("edge").GetString());
        Assert.Equal(InputSourceClass.Unattributed, evt.Data.GetProperty("source_class").GetString());
        Assert.Equal("0x0", evt.Data.GetProperty("raw_device_handle").GetString());
        Assert.False(evt.Data.TryGetProperty("changed_key", out _));
        Assert.False(evt.Data.TryGetProperty("held", out _));
        Assert.False(evt.Data.TryGetProperty("keyboard", out _));
        Assert.DoesNotContain("KeyQ", evt.Data.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhysicalMouseSse_TaggedFramesAreExcludedFromMouseStream_AndCountedInHeartbeat()
    {
        using var factory = new PhysicalMouseSseWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/physical-input/events");
        request.Headers.Add("X-Inputd-Access-Token", PhysicalMouseSseWebApplicationFactory.AccessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Drain initial heartbeat.
        var first = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("heartbeat", first.EventName);

        factory.Source.RaiseTagged(new HumanInputFrame
        {
            Device = "tagged_raw_mouse",
            TimestampUs = 7,
            InjectedTagged = true,
            Mouse = new MouseFrame { Dx = 99, Dy = 99 }
        });
        factory.Source.RaiseTagged(new HumanInputFrame
        {
            Device = "tagged_raw_mouse",
            TimestampUs = 8,
            InjectedTagged = true,
            Mouse = new MouseFrame { Dx = 100 }
        });
        factory.Source.RaiseTagged(new HumanInputFrame
        {
            Device = "tagged_raw_keyboard",
            TimestampUs = 8,
            InjectedTagged = true,
            ChangedKey = "KeyT",
            KeyDown = true
        });
        // Also publish via FrameObserved with InjectedTagged ? must still be excluded.
        factory.Source.RaiseFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 9,
            InjectedTagged = true,
            Mouse = new MouseFrame { Dx = 101 }
        });
        factory.Source.RaiseFrame(new HumanInputFrame
        {
            Device = "keyboard",
            TimestampUs = 9,
            InjectedTagged = true,
            ChangedKey = "KeyU",
            KeyDown = true
        });

        factory.Source.RaiseFrame(new HumanInputFrame
        {
            Device = "mouse",
            TimestampUs = 10,
            RawDeviceHandle = "0x1",
            Mouse = new MouseFrame { Dx = 3 }
        });

        var evt = await ReadNextEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("mouse", evt.EventName);
        Assert.Equal(3, evt.Data.GetProperty("dx").GetInt32());

        var hub = factory.Services.GetRequiredService<PhysicalMouseEventHub>();
        Assert.Equal(3, hub.TaggedFramesSeen);
    }

    private static async Task<(string EventName, JsonElement Data)> ReadNextEventAsync(StreamReader reader, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new TimeoutException("Timed out waiting for SSE event.");
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

internal sealed class HandlerTestClock : IMonotonicClock
{
    public long NowMicroseconds { get; set; } = 1_000_000;
}

/// <summary>
/// Response body that can block WriteAsync after an arming point, simulating client slow-drain
/// without depending on TestServer TCP window behavior.
/// </summary>
internal sealed class GatedResponseBodyStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _writeGate = new(initialState: true);
    private int _pendingWrites;
    private bool _blockSubsequent;
    private bool _disposed;

    public string Utf8Text
    {
        get
        {
            lock (_sync) return Encoding.UTF8.GetString(_buffer.ToArray());
        }
    }

    public int PendingWriteCount => Volatile.Read(ref _pendingWrites);

    public void BlockSubsequentWrites()
    {
        _blockSubsequent = true;
        _writeGate.Reset();
    }

    public void ReleaseWrites() => _writeGate.Set();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length { get { lock (_sync) return _buffer.Length; } }
    public override long Position
    {
        get { lock (_sync) return _buffer.Position; }
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        lock (_sync) _buffer.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flush();
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blockSubsequent)
        {
            Interlocked.Increment(ref _pendingWrites);
            try
            {
                while (!_writeGate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingWrites);
            }
        }

        lock (_sync)
        {
            _buffer.Write(buffer.Span);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _writeGate.Set();
            _writeGate.Dispose();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
