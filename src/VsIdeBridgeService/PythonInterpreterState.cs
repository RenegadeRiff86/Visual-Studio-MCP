using System.Text.Json;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static class PythonInterpreterState
{
    private const string ActiveInterpreterPathPropertyName = "activeInterpreterPath";
    private const string StateFileName = "python-state.json";

    internal static string? LoadActiveInterpreterPath()
    {
        JsonObject state = LoadState();
        JsonNode? pathNode = state[ActiveInterpreterPathPropertyName];
        return NormalizeFilePath(pathNode?.GetValue<string>());
    }

    private static JsonObject LoadState()
    {
        string statePath = GetStateFilePath();
        if (!File.Exists(statePath))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GetStateFilePath()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_PYTHON_STATE_DIR");
        string baseDirectory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsIdeBridge");
        return Path.Combine(baseDirectory, StateFileName);
    }

    private static string? NormalizeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(path.Trim());
    }
}
