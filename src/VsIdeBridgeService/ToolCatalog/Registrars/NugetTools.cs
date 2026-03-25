using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> NugetTools()
    {
        yield return new("nuget_add_package",
            "Add a NuGet package to a project using 'dotnet add package'. " +
            "The project must be in the open solution.",
            ObjectSchema(
                Req("project", "Project name or relative path (e.g. \"MyLib\" or \"src/MyLib/MyLib.csproj\")."),
                Req("package", "NuGet package id, e.g. \"Newtonsoft.Json\"."),
                Opt("version", "Optional version constraint, e.g. \"13.0.3\".")),
            "project",
            async (id, args, bridge) =>
            {
                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string project = RequireNugetArg(id, args, "project");
                string package = RequireNugetArg(id, args, "package");
                string? version = args?["version"]?.GetValue<string>();
                string versionArg = string.IsNullOrWhiteSpace(version) ? string.Empty
                    : $" --version {version}";
                string dotnetArgs = $"add \"{project}\" package {package}{versionArg}";
                return await RunDotnetAsync(id, dotnetArgs, solutionDir, timeoutMs: 120_000)
                    .ConfigureAwait(false);
            });

        yield return new("nuget_remove_package",
            "Remove a NuGet package from a project using 'dotnet remove package'.",
            ObjectSchema(
                Req("project", "Project name or relative path."),
                Req("package", "NuGet package id to remove.")),
            "project",
            async (id, args, bridge) =>
            {
                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string project = RequireNugetArg(id, args, "project");
                string package = RequireNugetArg(id, args, "package");
                return await RunDotnetAsync(id, $"remove \"{project}\" package {package}",
                    solutionDir, timeoutMs: 30_000)
                    .ConfigureAwait(false);
            });
    }

    // ── NuGet helpers ─────────────────────────────────────────────────────────

    private static async Task<JsonNode> RunDotnetAsync(
        JsonNode? id, string dotnetArgs, string workingDirectory, int timeoutMs)
    {
        string dotnet = ResolveDotnet();

        ProcessStartInfo psi = new()
        {
            FileName = dotnet,
            Arguments = dotnetArgs,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start dotnet process at '{dotnet}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        if (!ReferenceEquals(
                await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false),
                waitTask))
        {
            TryKillProcess(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"'dotnet {dotnetArgs}' timed out after {timeoutMs} ms.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        bool success = process.ExitCode == 0;

        JsonObject payload = new()
        {
            ["success"] = success,
            ["exitCode"] = process.ExitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() },
            },
            ["isError"] = !success,
            ["structuredContent"] = payload,
        };
    }

    private static string? _resolvedDotnet;

    private static string ResolveDotnet()
    {
        if (_resolvedDotnet is not null) return _resolvedDotnet;

        // Prefer dotnet on PATH (the normal case).
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(System.IO.Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (string name in new[] { "dotnet.exe", "dotnet" })
                {
                    string candidate = System.IO.Path.Combine(dir, name);
                    if (File.Exists(candidate)) return _resolvedDotnet = candidate;
                }
            }
        }

        // Fallback: well-known install path on Windows.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string wellKnown = System.IO.Path.Combine(programFiles, "dotnet", "dotnet.exe");
        if (File.Exists(wellKnown)) return _resolvedDotnet = wellKnown;

        // Last resort: rely on PATH lookup by name only.
        return _resolvedDotnet = "dotnet";
    }

    private static string RequireNugetArg(JsonNode? id, JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Missing required argument '{name}'.");
        return value;
    }

    private static void TryKillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
