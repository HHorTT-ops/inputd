using System.Text.Json;
using System.Text.Json.Nodes;

namespace InputExecutor.ObsBridge;

public sealed record ObsScriptRegistrationResult(string SceneCollectionPath, string BackupPath, string ScriptPath, string BridgePath);

public static class ObsScriptRegistration
{
    public static async Task<ObsScriptRegistrationResult> RegisterAsync(
        string sceneCollectionPath, string scriptPath, string bridgePath, CancellationToken cancellationToken = default)
    {
        var scene = RequireExistingAbsoluteFile(sceneCollectionPath, "OBS scene collection");
        var script = RequireExistingAbsoluteFile(scriptPath, "OBS Lua script");
        var bridge = RequireExistingAbsoluteFile(bridgePath, "OBS bridge executable");
        var text = await File.ReadAllTextAsync(scene, cancellationToken);
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("The OBS scene collection is not a JSON object.");
        var modules = root["modules"] as JsonObject;
        if (modules is null)
        {
            modules = new JsonObject();
            root["modules"] = modules;
        }
        var scripts = modules["scripts-tool"] as JsonArray;
        if (scripts is null)
        {
            scripts = new JsonArray();
            modules["scripts-tool"] = scripts;
        }

        for (var index = scripts.Count - 1; index >= 0; index--)
        {
            var existing = scripts[index]?["path"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(existing) &&
                string.Equals(Path.GetFullPath(existing), script, StringComparison.OrdinalIgnoreCase))
                scripts.RemoveAt(index);
        }
        scripts.Add(new JsonObject
        {
            ["path"] = script,
            ["settings"] = new JsonObject
            {
                ["enabled"] = true,
                ["bridge_path"] = bridge
            }
        });

        var backup = scene + $".inputd-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Copy(scene, backup, overwrite: false);
        var temporary = scene + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                new System.Text.UTF8Encoding(false), cancellationToken);
            File.Move(temporary, scene, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
        return new ObsScriptRegistrationResult(scene, backup, script, bridge);
    }

    private static string RequireExistingAbsoluteFile(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException($"{name} path must be absolute.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"{name} was not found.", fullPath);
        return fullPath;
    }
}
