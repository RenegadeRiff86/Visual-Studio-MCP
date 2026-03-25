using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string MicrosoftVisualStudio = "Microsoft Visual Studio";
    private const string DevenvExe = "devenv.exe";

    private static Task<JsonNode> BridgeHealthAsync(JsonNode? id, BridgeConnection bridge)
    {
        BridgeInstance? instance = bridge.CurrentInstance;
        JsonObject health = new()
        {
            ["success"] = true,
            ["discoveryMode"] = bridge.Mode.ToString(),
            ["currentSolutionPath"] = bridge.CurrentSolutionPath,
            ["bound"] = instance is not null,
        };

        if (instance is not null)
        {
            health["instance"] = new JsonObject
            {
                ["instanceId"] = instance.InstanceId,
                ["pipeName"] = instance.PipeName,
                ["processId"] = instance.ProcessId,
                ["solutionPath"] = instance.SolutionPath ?? string.Empty,
                ["source"] = instance.Source,
            };
        }

        return Task.FromResult((JsonNode)BridgeResult(health));
    }

    private static async Task<JsonNode> VsOpenAsync(JsonNode? id, JsonObject? args)
    {
        // Ensure the Windows service is running before trying to launch and discover VS.
        EnsureServiceRunning();

        string? solution = args?["solution"]?.GetValue<string>();
        string? explicitDevenv = args?["devenv_path"]?.GetValue<string>();
        string devenvPath = string.IsNullOrWhiteSpace(explicitDevenv)
            ? ResolveDevenvPath(id)
            : explicitDevenv;

        string ps = string.IsNullOrWhiteSpace(solution)
            ? $"$p=Start-Process -FilePath '{QuotePsLiteral(devenvPath)}' -PassThru; Write-Output $p.Id"
            : $"$p=Start-Process -FilePath '{QuotePsLiteral(devenvPath)}'" +
              $" -ArgumentList @('{QuotePsLiteral(solution)}') -PassThru; Write-Output $p.Id";

        ProcessStartInfo psi = new()
        {
            FileName = GetPowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -Command \"{ps}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Ensure ProgramW6432 is present for devenv.exe. The variable is injected by WoW64
        // into 32-bit processes but absent in 64-bit service environments; devenv's
        // ProjectFileClassifier static ctor crashes with ArgumentNullException if it's missing.
        psi.Environment["ProgramW6432"] = Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        bool ok = int.TryParse(stdout.Trim(), out int pid) && pid > 0;
        if (!ok)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Visual Studio launch failed. stderr: {stderr.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(solution))
        {
            string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vs-ide-bridge");
            System.IO.Directory.CreateDirectory(flagDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(flagDir, $"bridge-nosolution-{pid}.flag"), string.Empty);
        }

        return BridgeResult(new JsonObject
        {
            ["Success"] = true,
            ["pid"] = pid,
            ["devenv_path"] = devenvPath,
            ["solution"] = solution ?? string.Empty,
        });
    }

    private static async Task<JsonNode> WaitForInstanceAsync(
        JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string? solutionHint = args?["solution"]?.GetValue<string>();
        int timeoutMs = args?["timeout_ms"]?.GetValue<int?>() ?? 60_000;

        IReadOnlyList<BridgeInstance> existing =
            await VsDiscovery.ListAsync(bridge.Mode).ConfigureAwait(false);
        HashSet<string> existingIds = existing
            .Select(static instance => instance.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using CancellationTokenSource cts = new(timeoutMs);
        while (!cts.Token.IsCancellationRequested)
        {
            IReadOnlyList<BridgeInstance> current;
            try
            {
                // Each discovery poll gets its own 5 s deadline so a hung mutex
                // or file scan cannot block us past the outer timeout.
                Task<IReadOnlyList<BridgeInstance>> listTask =
                    VsDiscovery.ListAsync(bridge.Mode);
                if (await Task.WhenAny(listTask, Task.Delay(5_000, cts.Token))
                        .ConfigureAwait(false) != listTask)
                {
                    break; // outer timeout or poll timeout — give up
                }
                current = listTask.Result;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            BridgeInstance? found = current.FirstOrDefault(instance =>
                !existingIds.Contains(instance.InstanceId) &&
                (solutionHint is null ||
                 (instance.SolutionPath?.Contains(solutionHint, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (instance.SolutionName?.Contains(solutionHint, StringComparison.OrdinalIgnoreCase) ?? false)));

            if (found is not null)
            {
                return BridgeResult(new JsonObject
                {
                    ["Success"] = true,
                    ["instanceId"] = found.InstanceId,
                    ["pipeName"] = found.PipeName,
                    ["processId"] = found.ProcessId,
                    ["solutionPath"] = found.SolutionPath ?? string.Empty,
                });
            }

            try
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            $"No new VS instance appeared within {timeoutMs} ms.");
    }

    private static Task<JsonNode> VsCloseAsync(
        JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        int pid = args?["process_id"]?.GetValue<int?>() ??
                  bridge.CurrentInstance?.ProcessId ?? 0;
        bool force = args?["force"]?.GetValue<bool?>() ?? false;
        if (pid <= 0)
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                "No process_id specified and no VS instance bound.");
        }

        try
        {
            Process process = Process.GetProcessById(pid);
            if (force)
            {
                process.Kill();
            }
            else
            {
                process.CloseMainWindow();
            }

            return Task.FromResult((JsonNode)BridgeResult(new JsonObject
            {
                ["Success"] = true,
                ["processId"] = pid,
                ["forced"] = force,
            }));
        }
        catch (Exception ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to close VS process {pid}: {ex.Message}");
        }
    }

    private static string ResolveDevenvPath(JsonNode? id)
    {
        // vswhere may live under ProgramFilesX86 or ProgramFiles depending on VS install.
        string[] vswhereCandidates =
        [
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                MicrosoftVisualStudio, "Installer", "vswhere.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                MicrosoftVisualStudio, "Installer", "vswhere.exe"),
        ];
        string? vswhereExe = Array.Find(vswhereCandidates, File.Exists);
        if (vswhereExe is not null)
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = vswhereExe,
                    Arguments = "-latest -prerelease -requires Microsoft.Component.MSBuild" +
                                " -find Common7\\IDE\\devenv.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using Process process = Process.Start(psi)!;
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                {
                    return output;
                }
            }
            catch (Exception)
            {
                // devenv path probe failed — fall through to next candidate.
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            System.IO.Path.Combine(programFiles, MicrosoftVisualStudio, "18", "Community", "Common7", "IDE", DevenvExe),
            System.IO.Path.Combine(programFiles, MicrosoftVisualStudio, "18", "Professional", "Common7", "IDE", DevenvExe),
            System.IO.Path.Combine(programFiles, MicrosoftVisualStudio, "18", "Enterprise", "Common7", "IDE", DevenvExe),
            System.IO.Path.Combine(programFiles, MicrosoftVisualStudio, "2022", "Community", "Common7", "IDE", DevenvExe),
            System.IO.Path.Combine(programFiles, MicrosoftVisualStudio, "2022", "Professional", "Common7", "IDE", DevenvExe),
            System.IO.Path.Combine(programFiles, MicrosoftVisualStudio, "2022", "Enterprise", "Common7", "IDE", DevenvExe),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "devenv.exe not found. Install Visual Studio or pass 'devenv_path' explicitly.");
    }

    private static string QuotePsLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string GetPowerShellPath()
    {
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static async Task<JsonNode> ListInstancesAsync(BridgeConnection bridge)
    {
        // Ensure the Windows service is running so discovery actually has something to find.
        EnsureServiceRunning();

        IReadOnlyList<BridgeInstance> instances = await VsDiscovery
            .ListAsync(bridge.Mode).ConfigureAwait(false);

        JsonArray resultItems = [];
        foreach (BridgeInstance instance in instances)
        {
            resultItems.Add(new JsonObject
            {
                ["instanceId"] = instance.InstanceId,
                ["pipeName"] = instance.PipeName,
                ["processId"] = instance.ProcessId,
                ["solutionPath"] = instance.SolutionPath ?? string.Empty,
                ["solutionName"] = instance.SolutionName ?? string.Empty,
                ["source"] = instance.Source,
            });
        }

        JsonObject result = new()
        {
            ["success"] = true,
            ["instances"] = resultItems,
        };
        return BridgeResult(result);
    }

    /// <summary>
    /// Ensures the VsIdeBridgeService Windows service is running.
    /// Called before vs_open and list_instances so that a stopped-but-installed
    /// service auto-resumes rather than silently returning empty results.
    /// Failures are swallowed — the caller proceeds regardless.
    /// </summary>
    private static void EnsureServiceRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using System.ServiceProcess.ServiceController sc = new("VsIdeBridgeService");
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running &&
                sc.Status != System.ServiceProcess.ServiceControllerStatus.StartPending)
            {
                sc.Start();
                sc.WaitForStatus(
                    System.ServiceProcess.ServiceControllerStatus.Running,
                    TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception)
        {
            // Service may not be installed or we may lack permission — proceed anyway.
        }
    }
}
