using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Core.Recording;

public sealed class JsonLinesTrajectorySink : IBatchedTrajectorySink
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public JsonLinesTrajectorySink(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        _writer = new StreamWriter(_stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 256 * 1024, leaveOpen: true);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
    }

    public ValueTask WriteAsync(TrajectoryRecord record, CancellationToken cancellationToken) =>
        WriteBatchAsync([record], cancellationToken);

    public async ValueTask WriteBatchAsync(IReadOnlyList<TrajectoryRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var record in records)
            {
                var json = JsonSerializer.Serialize(record, _jsonOptions);
                await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            }
            await FlushCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FlushCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        await _writer.FlushAsync(cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            await FlushCoreAsync(CancellationToken.None);
            _disposed = true;
            await _writer.DisposeAsync();
            await _stream.DisposeAsync();
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
