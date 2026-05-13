using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

/// <summary>
/// Runs GitHub CLI (gh) commands as a subprocess and returns structured MCP results.
/// All GitHub tools share this runner for consistent timeout, env, and response shaping.
/// </summary>
internal static class GhRunner
{
    private const int DefaultTimeoutMs = 30_000;

    /// <summary>
    /// Execute a gh command in the given working directory and return an MCP-formatted result.
    /// </summary>
    public static async Task<JsonNode> RunAsync(
        JsonNode? id,
        string workingDirectory,
        IReadOnlyList<string> args,
        int timeoutMs = DefaultTimeoutMs)
    {
        string ghExe = ResolveGhExecutable(id);
        ProcessStartInfo psi = CreateStartInfo(ghExe, workingDirectory);
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        string displayArgs = string.Join(" ", args);
        return await RunProcessAsync(id, psi, displayArgs, timeoutMs).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string ghExe, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = ghExe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static async Task<JsonNode> RunProcessAsync(
        JsonNode? id,
        ProcessStartInfo psi,
        string displayArgs,
        int timeoutMs)
    {
        using Process process = Process.Start(psi)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start gh process at '{psi.FileName}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        if (!ReferenceEquals(
                await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false),
                waitTask))
        {
            TryKill(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"gh {displayArgs} timed out after {timeoutMs} ms.");
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

        string successText = $"gh command completed with exit code {process.ExitCode}.";
        return ToolResultFormatter.StructuredToolResult(payload, isError: !success, successText: successText);
    }

    // ── gh executable resolution ──────────────────────────────────────────────

    private static volatile string? _resolvedGhExe;

    public static string ResolveGhExecutable(JsonNode? id)
    {
        if (_resolvedGhExe is not null)
            return _resolvedGhExe;

        // 1. Prefer gh on PATH.
        if (TryFindOnPath("gh.exe", out string? fromPath))
            return _resolvedGhExe = fromPath!;
        if (TryFindOnPath("gh", out string? fromPathUnix))
            return _resolvedGhExe = fromPathUnix!;

        // 2. Common Windows install locations.
        string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            localAppData is not null
                ? Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe")
                : string.Empty,
            Path.Combine(programFiles, "GitHub CLI", "gh.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                return _resolvedGhExe = candidate;
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "GitHub CLI (gh) not found. Install it from https://cli.github.com/ " +
            "and ensure gh.exe is on PATH, or run 'gh auth login' if already installed.");
    }

    private static bool TryFindOnPath(string name, out string? fullPath)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            fullPath = null;
            return false;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        fullPath = null;
        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException("failed to terminate gh child process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate gh child process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate gh child process", ex);
        }
    }
}
