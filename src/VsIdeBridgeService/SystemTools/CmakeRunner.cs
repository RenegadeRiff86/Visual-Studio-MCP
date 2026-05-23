using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

/// <summary>
/// Runs CMake as a subprocess and returns structured MCP results.
/// All cmake bridge tools share this runner for consistent timeout, env, and response shaping.
/// </summary>
internal static class CmakeRunner
{
    /// <summary>Default timeout for cmake operations — configure can take several minutes on large projects.</summary>
    private const int DefaultTimeoutMs = 180_000; // 3 minutes

    /// <summary>
    /// Execute cmake with the given arguments in <paramref name="workingDirectory"/>.
    /// </summary>
    public static async Task<JsonNode> RunAsync(
        JsonNode? id,
        string workingDirectory,
        IReadOnlyList<string> args,
        int timeoutMs = DefaultTimeoutMs)
    {
        string cmakeExe = ResolveCmakeExecutable(id);
        ProcessStartInfo psi = CreateStartInfo(cmakeExe, workingDirectory);
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        string displayArgs = string.Join(" ", args);
        return await RunProcessAsync(id, psi, displayArgs, timeoutMs).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string cmakeExe, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = cmakeExe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // closed immediately; prevents cmake from waiting on stdin
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
                $"Failed to start cmake process at '{psi.FileName}'.");

        process.StandardInput.Close();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        if (!ReferenceEquals(
                await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false),
                waitTask))
        {
            TryKill(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"cmake {displayArgs} timed out after {timeoutMs / 1000} seconds.");
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

        string successText = success
            ? $"cmake completed successfully (exit 0)."
            : $"cmake exited with code {process.ExitCode} — check stderr for details.";

        return ToolResultFormatter.StructuredToolResult(payload, isError: !success, successText: successText);
    }

    // ── cmake executable resolution ──────────────────────────────────────────────

    private static volatile string? _resolvedCmakeExe;

    /// <summary>
    /// Find cmake.exe: PATH first, then VS-bundled CMake, then standalone Program Files install.
    /// Result is cached for the process lifetime.
    /// </summary>
    public static string ResolveCmakeExecutable(JsonNode? id)
    {
        if (_resolvedCmakeExe is not null)
            return _resolvedCmakeExe;

        // 1. Prefer cmake on PATH.
        if (TryFindOnPath("cmake.exe", out string? fromPath))
            return _resolvedCmakeExe = fromPath!;

        // 2. CMake bundled with Visual Studio (checked newest-first; all common editions).
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] vsYears   = ["2026", "2022", "2019"];
        string[] vsEditions = ["Enterprise", "Professional", "Community", "BuildTools"];
        const string VsBundledCmake =
            @"Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe";

        foreach (string year in vsYears)
        foreach (string edition in vsEditions)
        {
            string candidate = Path.Combine(
                programFiles, "Microsoft Visual Studio", year, edition, VsBundledCmake);
            if (File.Exists(candidate))
                return _resolvedCmakeExe = candidate;
        }

        // 3. Standalone CMake install.
        string standaloneCandidate = Path.Combine(programFiles, "CMake", "bin", "cmake.exe");
        if (File.Exists(standaloneCandidate))
            return _resolvedCmakeExe = standaloneCandidate;

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "cmake not found. Install CMake from https://cmake.org/download/ and ensure " +
            "cmake.exe is on PATH, or install the 'CMake tools' component via the Visual Studio Installer.");
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
            McpServerLog.WriteException("failed to terminate cmake child process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate cmake child process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate cmake child process", ex);
        }
    }
}
