using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VsIdeBridgeService.SystemTools;

internal static class BuildErrorsTool
{
    private const int DefaultMax = 20;
    private const int BuildTimeoutMs = 120_000;

    // path(line,col): error CODE: message [project.csproj]
    private static readonly Regex ErrorLinePattern = new(
        @"^(.*?)\((\d+),\d+\): error (\w+): (.+?) \[(.+?)\]$",
        RegexOptions.Compiled);

    public static async Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string configuration = args?["configuration"]?.GetValue<string>() ?? "Release";
        string? projectArg = args?["project"]?.GetValue<string>();
        int max = args?["max"]?.GetValue<int?>() ?? DefaultMax;

        string msBuildPath = FindMsBuild();
        if (string.IsNullOrEmpty(msBuildPath))
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                "MSBuild.exe not found. Ensure Visual Studio is installed.");
        }

        string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
        string target = string.IsNullOrWhiteSpace(projectArg)
            ? FindSolutionFile(solutionDir)
            : ResolveProjectPath(solutionDir, projectArg);

        string buildArgs = $"\"{target}\" /p:Configuration={configuration} /nologo /m /v:q";
        ProcessStartInfo psi = new()
        {
            FileName = msBuildPath,
            Arguments = buildArgs,
            WorkingDirectory = solutionDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Stopwatch sw = Stopwatch.StartNew();
        using Process process = Process.Start(psi)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError, "Failed to start MSBuild.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(waitTask, Task.Delay(BuildTimeoutMs)).ConfigureAwait(false);

        if (!ReferenceEquals(completed, waitTask))
        {
            TryKill(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"MSBuild timed out after {BuildTimeoutMs / 1000}s.");
        }

        sw.Stop();
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        List<JsonObject> allErrors = ParseErrors(stdout + "\n" + stderr);
        int totalErrors = allErrors.Count;
        bool truncated = totalErrors > max;
        List<JsonObject> displayErrors = truncated ? allErrors.GetRange(0, max) : allErrors;

        JsonArray errorArray = new();
        foreach (JsonObject err in displayErrors)
            errorArray.Add(err);

        JsonObject payload = new()
        {
            ["success"] = totalErrors == 0,
            ["errorCount"] = totalErrors,
            ["truncated"] = truncated,
            ["errors"] = errorArray,
            ["buildDuration"] = $"{sw.Elapsed.TotalSeconds:F1}s",
            ["configuration"] = configuration,
            ["target"] = Path.GetFileName(target),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(),
                },
            },
            ["isError"] = totalErrors > 0,
            ["structuredContent"] = payload,
        };
    }

    private static List<JsonObject> ParseErrors(string output)
    {
        List<JsonObject> result = new();
        string[] lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string line in lines)
        {
            Match m = ErrorLinePattern.Match(line.Trim());
            if (!m.Success)
                continue;

            result.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(m.Groups[1].Value.Trim()),
                ["line"] = int.TryParse(m.Groups[2].Value, out int lineNum) ? lineNum : 0,
                ["code"] = m.Groups[3].Value,
                ["message"] = m.Groups[4].Value.Trim(),
                ["project"] = Path.GetFileNameWithoutExtension(m.Groups[5].Value.Trim()),
            });
        }
        return result;
    }

    private static string FindMsBuild()
    {
        const string vsWhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        if (File.Exists(vsWhere))
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = vsWhere,
                    Arguments = "-latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process p = Process.Start(psi)!;
                string vsWhereOutput = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                string first = vsWhereOutput.Split('\n')[0].Trim();
                if (File.Exists(first))
                    return first;
            }
            catch { /* intentional: vswhere not available or failed — fall through to directory scan */ }
        }

        // Fallback: scan common VS install roots
        string[] searchRoots = new[]
        {
            @"C:\Program Files\Microsoft Visual Studio",
            @"C:\Program Files (x86)\Microsoft Visual Studio",
        };
        foreach (string root in searchRoots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (string candidate in Directory.EnumerateFiles(root, "MSBuild.exe", SearchOption.AllDirectories))
            {
                if (candidate.Contains(@"\amd64\", StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(@"\Bin\MSBuild.exe", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        return string.Empty;
    }

    private static string FindSolutionFile(string directory)
    {
        string[] slnFiles = Directory.GetFiles(directory, "*.sln");
        return slnFiles.Length > 0 ? slnFiles[0] : directory;
    }

    private static string ResolveProjectPath(string solutionDir, string projectArg)
    {
        if (Path.IsPathRooted(projectArg))
            return projectArg;

        string relative = Path.Combine(solutionDir, projectArg);
        if (File.Exists(relative))
            return relative;

        // Search by project name under solution dir
        foreach (string proj in Directory.EnumerateFiles(solutionDir, "*.csproj", SearchOption.AllDirectories))
        {
            if (Path.GetFileNameWithoutExtension(proj).Equals(projectArg, StringComparison.OrdinalIgnoreCase))
                return proj;
        }

        return relative;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* intentional: process may have already exited between HasExited check and Kill */ }
    }
}
