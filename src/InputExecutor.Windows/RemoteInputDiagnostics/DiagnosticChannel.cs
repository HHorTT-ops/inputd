using System.Threading.Channels;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

/// <summary>
/// Bounded diagnostic queue using FullMode.Wait so TryWrite returns false when full
/// (accurate drop counting) without blocking the producer thread.
/// </summary>
public sealed class DiagnosticChannel<T>
{
    private readonly Channel<T> _channel;
    private long _enqueued;
    private long _dropped;

    public DiagnosticChannel(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<T> Reader => _channel.Reader;
    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(T item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _enqueued);
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
