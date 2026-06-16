using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VsIdeBridgeService.SystemTools;

internal static partial class BridgeInstalledVersionTool
{
    public static Task<JsonNode> ExecuteAsync(JsonNode? _, JsonObject? args, BridgeConnection bridge)
    {
        string solutionDirectory = ServiceToolPaths.ResolveSolutionDirectory(bridge);
        string installedRoot = args?["root"]?.GetValue<string>()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VsIdeBridge");
        installedRoot = Path.GetFullPath(installedRoot);

        JsonObject source = BuildSourceSummary(solutionDirectory);
        JsonArray installedFiles = BuildInstalledFiles(installedRoot);
        JsonArray topLevelFiles = BuildTopLevelFiles(installedRoot);

        JsonObject payload = new()
        {
            ["success"] = true,
            ["solutionDirectory"] = solutionDirectory,
            ["source"] = source,
            ["installedRoot"] = installedRoot,
            ["installedRootExists"] = Directory.Exists(installedRoot),
            ["installedFiles"] = installedFiles,
            ["topLevelFiles"] = topLevelFiles,
        };

        string sourceVersion = source["version"]?.GetValue<string>() ?? "unknown";
        string successText = $"Source version {sourceVersion}; inspected {installedFiles.Count} installed bridge file(s).";
        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(payload, args, successText: successText));
    }

    private static JsonObject BuildSourceSummary(string solutionDirectory)
    {
        JsonArray files = [];
        files.Add(BuildSourceVersionFile(
            Path.Combine(solutionDirectory, "Directory.Build.props"),
            "Directory.Build.props",
            VersionTagRegex()));
        files.Add(BuildSourceVersionFile(
            Path.Combine(solutionDirectory, "src", "VsIdeBridge", "source.extension.vsixmanifest"),
            "src/VsIdeBridge/source.extension.vsixmanifest",
            VsixVersionRegex()));
        files.Add(BuildSourceVersionFile(
            Path.Combine(solutionDirectory, "installer", "inno", "vs-ide-bridge.iss"),
            "installer/inno/vs-ide-bridge.iss",
            IssVersionRegex()));

        string? version = files.OfType<JsonObject>()
            .Select(file => file["version"]?.GetValue<string>())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new JsonObject
        {
            ["version"] = version,
            ["files"] = files,
        };
    }

    private static JsonObject BuildSourceVersionFile(string path, string relativePath, Regex regex)
    {
        JsonObject result = new()
        {
            ["path"] = relativePath,
            ["exists"] = File.Exists(path),
        };

        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            Match match = regex.Match(File.ReadAllText(path));
            result["version"] = match.Success ? match.Value : null;
            result["lastWriteUtc"] = File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (IOException ex)
        {
            result["error"] = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }

    private static JsonArray BuildInstalledFiles(string installedRoot)
    {
        string[] relativePaths =
        [
            Path.Combine("service", "VsIdeBridgeService.exe"),
            Path.Combine("service", "VsIdeBridgeService.dll"),
            Path.Combine("service", "VsIdeBridge.ServiceDomain.dll"),
            Path.Combine("service", "VsIdeBridge.Tooling.dll"),
            Path.Combine("service", "VsIdeBridge.Diagnostics.dll"),
            Path.Combine("service", "VsIdeBridge.Discovery.dll"),
            Path.Combine("service", "VsIdeBridgeLauncher.exe"),
            Path.Combine("service", "net472", "VsIdeBridgeLauncher.exe"),
            Path.Combine("vsix", "VsIdeBridge.vsix"),
            "README.md",
            "unins000.exe",
        ];

        JsonArray files = [];
        foreach (string relativePath in relativePaths)
        {
            files.Add(BuildInstalledFile(installedRoot, relativePath));
        }

        return files;
    }

    private static JsonObject BuildInstalledFile(string installedRoot, string relativePath)
    {
        string path = Path.Combine(installedRoot, relativePath);
        JsonObject result = new()
        {
            ["path"] = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            ["exists"] = File.Exists(path),
        };

        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            FileInfo info = new(path);
            result["sizeBytes"] = info.Length;
            result["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion))
            {
                result["fileVersion"] = versionInfo.FileVersion;
            }

            if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
            {
                result["productVersion"] = versionInfo.ProductVersion;
            }
        }
        catch (IOException ex)
        {
            result["error"] = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }

    private static JsonArray BuildTopLevelFiles(string installedRoot)
    {
        JsonArray files = [];
        if (!Directory.Exists(installedRoot))
        {
            return files;
        }

        try
        {
            foreach (FileInfo file in new DirectoryInfo(installedRoot).EnumerateFiles().OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(new JsonObject
                {
                    ["path"] = file.Name,
                    ["sizeBytes"] = file.Length,
                    ["lastWriteUtc"] = file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files.Add(new JsonObject
            {
                ["error"] = ex.Message,
                ["errorType"] = ex.GetType().Name,
            });
        }

        return files;
    }

    [GeneratedRegex("(?<=<Version>)[^<]*(?=</Version>)")]
    private static partial Regex VersionTagRegex();

    [GeneratedRegex("""(?<=<Identity[^>]+Version=")[^"]*(?=")""")]
    private static partial Regex VsixVersionRegex();

    [GeneratedRegex("""(?<=#define MyAppVersion ")[^"]*(?=")""")]
    private static partial Regex IssVersionRegex();
}
