using System.Threading.Channels;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Service;

/// <summary>
/// Fan-out hub for mouse Raw Input frames with best-effort source attribution.
/// Callbacks only TryWrite; overflow faults the subscriber instead of silently dropping events.
/// </summary>
public sealed class PhysicalMouseEventHub : IDisposable
{
    public const int DefaultCapacity = 256;

    private readonly object _sync = new();
    private readonly List<Subscriber> _subscribers = new();
    private readonly int _defaultCapacity;
    private long _sequence;
    private long _taggedFramesSeen;
    private bool _disposed;

    public PhysicalMouseEventHub(int defaultCapacity = DefaultCapacity)
    {
        if (defaultCapacity < 1) throw new ArgumentOutOfRangeException(nameof(defaultCapacity));
        _defaultCapacity = defaultCapacity;
    }

    public int SubscriberCount
    {
        get { lock (_sync) return _subscribers.Count; }
    }

    public long TaggedFramesSeen => Interlocked.Read(ref _taggedFramesSeen);

    public void ObserveTaggedFrame() => Interlocked.Increment(ref _taggedFramesSeen);

    public PhysicalMouseSubscription Subscribe(int? capacity = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resolved = capacity ?? _defaultCapacity;
        if (resolved < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        var channel = Channel.CreateBounded<PhysicalMouseSseMessage>(new BoundedChannelOptions(resolved)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var subscriber = new Subscriber(channel);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(subscriber);
        }
        return new PhysicalMouseSubscription(this, subscriber);
    }

    public void PublishFrame(HumanInputFrame frame)
    {
        if (_disposed) return;
        if (!string.Equals(frame.Device, "mouse", StringComparison.Ordinal))
            return;
        if (frame.InjectedTagged)
            return;

        var sourceClass = InputSourceClass.Classify(frame);
        // EXECUTOR_INJECTED must never appear here (InjectedTagged early-return above).
        // DEVICE_ATTRIBUTED and UNATTRIBUTED are both published; consumers must filter by source_class.
        // DEVICE_ATTRIBUTED is device-handle attribution only — not proof of human hand input.
        var sequence = Interlocked.Increment(ref _sequence);
        var message = PhysicalMouseSseMessage.ForMouse(new PhysicalMouseEventDto
        {
            Sequence = sequence,
            TimestampUs = frame.TimestampUs,
            Dx = frame.Mouse.Dx,
            Dy = frame.Mouse.Dy,
            HeldButtons = frame.Mouse.HeldButtons.ToArray(),
            WheelX = frame.Mouse.WheelX,
            WheelY = frame.Mouse.WheelY,
            SourceClass = sourceClass,
            RawDeviceHandle = frame.RawDeviceHandle ?? InputSourceClass.FormatDeviceHandle(0)
        });
        Broadcast(message);
    }

    public void PublishHeartbeat(long timestampUs)
    {
        if (_disposed) return;
        Broadcast(PhysicalMouseSseMessage.Heartbeat(timestampUs, TaggedFramesSeen));
    }

    internal void Unsubscribe(Subscriber subscriber)
    {
        lock (_sync)
        {
            _subscribers.Remove(subscriber);
        }
        subscriber.Channel.Writer.TryComplete();
    }

    private void Broadcast(PhysicalMouseSseMessage message)
    {
        Subscriber[] snapshot;
        lock (_sync)
        {
            if (_subscribers.Count == 0) return;
            snapshot = _subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            if (subscriber.Channel.Writer.TryWrite(message))
                continue;

            // Channel is full. Do not drop silently: mark overflow, complete the writer,
            // and let the SSE endpoint emit an explicit fault after draining.
            subscriber.MarkOverflow();
            subscriber.Channel.Writer.TryComplete();
            Unsubscribe(subscriber);
        }
    }

    public void Dispose()
    {
        Subscriber[] snapshot;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = _subscribers.ToArray();
            _subscribers.Clear();
        }
        foreach (var subscriber in snapshot)
            subscriber.Channel.Writer.TryComplete();
    }

    internal sealed class Subscriber(Channel<PhysicalMouseSseMessage> channel)
    {
        private int _overflow;

        public Channel<PhysicalMouseSseMessage> Channel { get; } = channel;
        public bool Overflowed => Volatile.Read(ref _overflow) != 0;
        public void MarkOverflow() => Interlocked.Exchange(ref _overflow, 1);
    }
}

public sealed class PhysicalMouseSubscription : IAsyncDisposable, IDisposable
{
    private readonly PhysicalMouseEventHub _hub;
    private readonly PhysicalMouseEventHub.Subscriber _subscriber;
    private int _released;

    internal PhysicalMouseSubscription(PhysicalMouseEventHub hub, PhysicalMouseEventHub.Subscriber subscriber)
    {
        _hub = hub;
        _subscriber = subscriber;
    }

    public ChannelReader<PhysicalMouseSseMessage> Reader => _subscriber.Channel.Reader;
    public bool Overflowed => _subscriber.Overflowed;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return ValueTask.CompletedTask;
        _hub.Unsubscribe(_subscriber);
        return ValueTask.CompletedTask;
    }
}

public sealed record PhysicalMouseEventDto
{
    public long Sequence { get; init; }
    public long TimestampUs { get; init; }
    public int Dx { get; init; }
    public int Dy { get; init; }
    public IReadOnlyList<string> HeldButtons { get; init; } = Array.Empty<string>();
    public int WheelX { get; init; }
    public int WheelY { get; init; }
    public string SourceClass { get; init; } = InputSourceClass.Unattributed;
    public string RawDeviceHandle { get; init; } = "0x0";
}

public sealed class PhysicalMouseSseMessage
{
    private PhysicalMouseSseMessage(string eventName, PhysicalMouseEventDto? mouse, long? heartbeatTimestampUs, long? taggedFramesSeen, string? errorCode, string? errorMessage)
    {
        EventName = eventName;
        Mouse = mouse;
        HeartbeatTimestampUs = heartbeatTimestampUs;
        TaggedFramesSeen = taggedFramesSeen;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public string EventName { get; }
    public PhysicalMouseEventDto? Mouse { get; }
    public long? HeartbeatTimestampUs { get; }
    public long? TaggedFramesSeen { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public static PhysicalMouseSseMessage ForMouse(PhysicalMouseEventDto dto) =>
        new("mouse", dto, null, null, null, null);

    public static PhysicalMouseSseMessage Heartbeat(long timestampUs, long taggedFramesSeen) =>
        new("heartbeat", null, timestampUs, taggedFramesSeen, null, null);

    public static PhysicalMouseSseMessage Fault(string code, string message) =>
        new("fault", null, null, null, code, message);
}
