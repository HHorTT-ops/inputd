using System.Security.Cryptography;
using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Recording;

namespace InputExecutor.ObsBridge;

public sealed class ObsPairedTrajectorySink : IBatchedTrajectorySink
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonLinesTrajectorySink? _inner;
    private ObsPairedPaths? _paths;
    private long _persisted;
    private bool _disposed;

    public ObsPairedPaths? Paths => Volatile.Read(ref _paths);
    public long PersistedCount => Interlocked.Read(ref _persisted);

    public async Task<ObsPairedPaths> StartAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var paths = ObsPairedPaths.FromVideo(videoPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inner is not null) throw new InvalidOperationException("An OBS input recording is already active.");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.TrajectoryPath)!);
            if (File.Exists(paths.TrajectoryPath))
                throw new IOException($"The paired input file already exists: {paths.TrajectoryPath}");
            if (File.Exists(paths.PartialPath))
            {
                var preserved = paths.TrajectoryPath + $".incomplete-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl";
                File.Move(paths.PartialPath, preserved);
            }
            _inner = new JsonLinesTrajectorySink(paths.PartialPath);
            _paths = paths;
            Interlocked.Exchange(ref _persisted, 0);
            return paths;
        }
        finally { _gate.Release(); }
    }

    public ValueTask WriteAsync(TrajectoryRecord record, CancellationToken cancellationToken) =>
        WriteBatchAsync([record], cancellationToken);

    public async ValueTask WriteBatchAsync(IReadOnlyList<TrajectoryRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is null) return;
            await _inner.WriteBatchAsync(records, cancellationToken);
            Interlocked.Add(ref _persisted, records.Count);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is not null) await _inner.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<ObsPairedPaths> CompleteAsync(string finalVideoPath, ObsPairingManifest manifest, CancellationToken cancellationToken = default)
    {
        var finalPaths = ObsPairedPaths.FromVideo(finalVideoPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is null || _paths is null) throw new InvalidOperationException("No OBS input recording is active.");
            await _inner.DisposeAsync();
            _inner = null;
            Directory.CreateDirectory(Path.GetDirectoryName(finalPaths.TrajectoryPath)!);
            if (File.Exists(finalPaths.TrajectoryPath))
                throw new IOException($"The paired input file already exists: {finalPaths.TrajectoryPath}");
            File.Move(_paths.PartialPath, finalPaths.TrajectoryPath);

            await using var trajectoryStream = new FileStream(finalPaths.TrajectoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var trajectoryHash = Convert.ToHexString(await SHA256.HashDataAsync(trajectoryStream, cancellationToken)).ToLowerInvariant();
            var completedManifest = manifest with
            {
                Complete = true,
                VideoPath = finalPaths.VideoPath,
                VideoFileName = Path.GetFileName(finalPaths.VideoPath),
                TrajectoryPath = finalPaths.TrajectoryPath,
                TrajectorySha256 = trajectoryHash
            };
            await WriteManifestAtomicallyAsync(finalPaths.ManifestPath, completedManifest, cancellationToken);
            _paths = finalPaths;
            return finalPaths;
        }
        finally { _gate.Release(); }
    }

    public async Task PreserveIncompleteAsync(ObsPairingManifest manifest, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_inner is not null)
            {
                await _inner.DisposeAsync();
                _inner = null;
            }
            if (_paths is not null)
            {
                var incompleteManifest = _paths.PartialPath + ".manifest.json";
                await WriteManifestAtomicallyAsync(incompleteManifest, manifest with
                {
                    Complete = false,
                    VideoPath = _paths.VideoPath,
                    VideoFileName = Path.GetFileName(_paths.VideoPath),
                    TrajectoryPath = _paths.PartialPath
                }, cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task WriteManifestAtomicallyAsync(string path, ObsPairingManifest manifest, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, json, new System.Text.UTF8Encoding(false), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            if (_inner is not null) await _inner.DisposeAsync();
            _inner = null;
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
