namespace InputExecutor.ObsBridge;

public sealed record ObsPairedPaths(string VideoPath, string TrajectoryPath, string PartialPath, string ManifestPath)
{
    public static ObsPairedPaths FromVideo(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("OBS did not provide a recording output path.", nameof(videoPath));

        var trimmed = videoPath.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            throw new ArgumentException("The OBS recording output path must be absolute.", nameof(videoPath));
        var fullVideoPath = Path.GetFullPath(trimmed);

        var directory = Path.GetDirectoryName(fullVideoPath);
        var stem = Path.GetFileNameWithoutExtension(fullVideoPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
            throw new ArgumentException("The OBS recording output path must include a file name.", nameof(videoPath));

        var trajectory = Path.Combine(directory, stem + ".input.jsonl");
        return new ObsPairedPaths(fullVideoPath, trajectory, trajectory + ".partial", trajectory + ".manifest.json");
    }
}
