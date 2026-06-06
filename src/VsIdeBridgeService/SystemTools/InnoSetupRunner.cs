using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

/// <summary>
/// Runs the Inno Setup compiler (ISCC.exe) as a subprocess and returns structured MCP results.
/// All installer bridge tools share this runner for consistent timeout, env, and response shaping.
/// </summary>
internal static class InnoSetupRunner
{
    /// <summary>Default timeout — installer compilation is fast but packaging can take a moment.</summary>
    private const int DefaultTimeoutMs = 120_000; // 2 minutes

    /// <summary>
    /// Compile an Inno Setup script (.iss) file using ISCC.exe.
    /// </summary>
    public static async Task<JsonNode> RunAsync(
        JsonNode? id,
        string issFilePath,
        string? configuration,
        int timeoutMs = DefaultTimeoutMs)
    {
        string isccExe = ResolveIsccExecutable(id);
        ProcessStartInfo psi = CreateStartInfo(isccExe, Path.GetDirectoryName(issFilePath)!);

        if (!string.IsNullOrWhiteSpace(configuration))
            psi.ArgumentList.Add($"/DConfiguration={configuration}");

        psi.ArgumentList.Add(issFilePath);

        string displayArgs = string.IsNullOrWhiteSpace(configuration)
            ? issFilePath
            : $"/DConfiguration={configuration} {issFilePath}";

        return await RunProcessAsync(id, psi, displayArgs, timeoutMs).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string isccExe, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = isccExe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // closed immediately; prevents ISCC from waiting on stdin
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
                $"Failed to start ISCC process at '{psi.FileName}'.");

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
                $"ISCC {displayArgs} timed out after {timeoutMs / 1000} seconds.");
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
            ? "ISCC compiled the installer successfully (exit 0)."
            : $"ISCC exited with code {process.ExitCode} — check stdout for details.";

        return ToolResultFormatter.StructuredToolResult(payload, isError: !success, successText: successText);
    }

    // ── ISCC executable resolution ────────────────────────────────────────────────

    private static volatile string? _resolvedIsccExe;

    /// <summary>
    /// Find ISCC.exe: PATH first, then standard Inno Setup install locations (v6 before v5),
    /// checked in both Program Files and Program Files (x86).
    /// Result is cached for the process lifetime.
    /// </summary>
    public static string ResolveIsccExecutable(JsonNode? id)
    {
        if (_resolvedIsccExe is not null)
            return _resolvedIsccExe;

        // 1. Prefer ISCC.exe on PATH.
        if (TryFindOnPath("ISCC.exe", out string? fromPath))
            return _resolvedIsccExe = fromPath!;

        // 2. Standard Inno Setup install locations (v6 before v5; x86 before x64).
        string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] roots    = [pf86, pf];
        string[] versions = ["Inno Setup 6", "Inno Setup 5"];

        foreach (string root in roots)
        foreach (string ver  in versions)
        {
            string candidate = Path.Combine(root, ver, "ISCC.exe");
            if (File.Exists(candidate))
                return _resolvedIsccExe = candidate;
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "ISCC.exe not found. Install Inno Setup from https://jrsoftware.org/isinfo.php and " +
            "ensure ISCC.exe is on PATH or installed at the default Program Files location.");
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
            McpServerLog.WriteException("failed to terminate ISCC child process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate ISCC child process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate ISCC child process", ex);
        }
    }
}
