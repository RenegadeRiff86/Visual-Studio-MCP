using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string MicrosoftVisualStudio = "Microsoft Visual Studio";
    private const string DevenvExe = "devenv.exe";
    private const string InstanceIdKey = "instanceId";
    private const string SolutionKey = "solution";
    private const int VsCloseWaitTimeoutMilliseconds = 10_000;
    private const int VsOpenRegistrationTimeoutMilliseconds = 30_000;
    private const int LauncherResultTimeoutMilliseconds = 15_000;
    private const string LauncherExe = "VsIdeBridgeLauncher.exe";
    private const string LaunchVisualStudioCommand = "launch-visual-studio";
    private const string VsOpenLaunchOptInEnvironmentVariable = "VS_IDE_BRIDGE_ENABLE_VS_OPEN_LAUNCH";
    private static readonly SemaphoreSlim VsOpenGate = new(1, 1);
    private static readonly object PendingVsLaunchGate = new();
    private static int PendingVsLaunchPid;
    private static string? PendingVsLaunchSolution;
    private static DateTimeOffset PendingVsLaunchStartedAtUtc;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwShowNormal = 1;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const int LogonWithProfile = 0x00000001;

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
                [InstanceIdKey] = instance.InstanceId,
                ["label"] = instance.Label,
                ["pipeName"] = instance.PipeName,
                ["processId"] = instance.ProcessId,
                ["solutionPath"] = instance.SolutionPath ?? string.Empty,
                ["source"] = instance.Source,
            };
        }

        return Task.FromResult((JsonNode)BridgeResult(health));
    }

    private static async Task<JsonNode> VsOpenAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        // Ensure the Windows service is running before trying to launch and discover VS.
        EnsureServiceRunning();

        string? solution = args?[SolutionKey]?.GetValue<string>();
        string? explicitDevenv = args?["devenv_path"]?.GetValue<string>();
        string devenvPath = string.IsNullOrWhiteSpace(explicitDevenv)
            ? ResolveDevenvPath(id)
            : explicitDevenv;
        await VsOpenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            int existingPid = await TryReuseExistingVsProcessAsync(solution).ConfigureAwait(false);
            if (existingPid > 0)
            {
                return BridgeResult(new JsonObject
                {
                    ["Success"] = true,
                    ["pid"] = existingPid,
                    ["devenv_path"] = devenvPath,
                    [SolutionKey] = solution ?? string.Empty,
                    ["reused"] = true,
                });
            }

            if (TryGetPendingVsLaunch(solution, out int pendingPid))
            {
                BridgeInstance? pendingInstance = await WaitForRegisteredInstanceAsync(
                    pendingPid,
                    solution,
                    VsOpenRegistrationTimeoutMilliseconds).ConfigureAwait(false);

                if (pendingInstance is not null)
                {
                    ClearPendingVsLaunch(pendingInstance.ProcessId);
                    return BridgeResult(new JsonObject
                    {
                        ["Success"] = true,
                        ["pid"] = pendingInstance.ProcessId,
                        [InstanceIdKey] = pendingInstance.InstanceId,
                        ["label"] = pendingInstance.Label,
                        ["devenv_path"] = devenvPath,
                        [SolutionKey] = pendingInstance.SolutionPath ?? solution ?? string.Empty,
                        ["reused"] = true,
                    });
                }

                CleanupFailedVsLaunch(pendingPid);
            }

            int launchedByBridgePid = await TryLaunchViaBoundInstanceAsync(id, bridge, devenvPath, solution).ConfigureAwait(false);
            if (launchedByBridgePid > 0)
            {
                RecordPendingVsLaunch(launchedByBridgePid, solution);

                BridgeInstance? bridgedLaunchInstance = await WaitForRegisteredInstanceAsync(
                    launchedByBridgePid,
                    solution,
                    VsOpenRegistrationTimeoutMilliseconds).ConfigureAwait(false);

                if (bridgedLaunchInstance is null)
                {
                    CleanupFailedVsLaunch(launchedByBridgePid);
                    throw new McpRequestException(
                        id,
                        McpErrorCodes.BridgeError,
                        $"Interactive VS launch created PID {launchedByBridgePid} but it never registered a live VS IDE Bridge instance within {VsOpenRegistrationTimeoutMilliseconds} ms.");
                }

                ClearPendingVsLaunch(bridgedLaunchInstance.ProcessId);

                return BridgeResult(new JsonObject
                {
                    ["Success"] = true,
                    ["pid"] = bridgedLaunchInstance.ProcessId,
                    [InstanceIdKey] = bridgedLaunchInstance.InstanceId,
                    ["label"] = bridgedLaunchInstance.Label,
                    ["devenv_path"] = devenvPath,
                    [SolutionKey] = bridgedLaunchInstance.SolutionPath ?? solution ?? string.Empty,
                    ["reused"] = false,
                    ["launchMode"] = "interactive-bridge",
                });
            }

            if (!IsVsOpenLaunchEnabled())
            {
                throw new McpRequestException(
                    id,
                    McpErrorCodes.BridgeError,
                    $"'vs_open' launch is disabled because it is not yet production-ready and can destabilize Visual Studio startup. Start Visual Studio manually and bind to it, or set {VsOpenLaunchOptInEnvironmentVariable}=1 only when deliberately testing the launch path.");
            }

            bool deferSolutionOpen = ShouldLaunchInInteractiveSession() && !string.IsNullOrWhiteSpace(solution);
            int pid = LaunchVisualStudio(devenvPath, deferSolutionOpen ? null : solution);
            RecordPendingVsLaunch(pid, solution);

            if (deferSolutionOpen)
            {
                WritePendingSolutionOpenFlag(pid, solution!);
            }

            if (string.IsNullOrWhiteSpace(solution))
            {
                string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vs-ide-bridge");
                System.IO.Directory.CreateDirectory(flagDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(flagDir, $"bridge-nosolution-{pid}.flag"), string.Empty);
            }

            BridgeInstance? launchedInstance = await WaitForRegisteredInstanceAsync(
                pid,
                solution,
                VsOpenRegistrationTimeoutMilliseconds).ConfigureAwait(false);

            if (launchedInstance is null)
            {
                CleanupFailedVsLaunch(pid);
                throw new McpRequestException(
                    id,
                    McpErrorCodes.BridgeError,
                    $"Visual Studio launched as PID {pid} but never registered a live VS IDE Bridge instance within {VsOpenRegistrationTimeoutMilliseconds} ms.");
            }

            ClearPendingVsLaunch(launchedInstance.ProcessId);

            return BridgeResult(new JsonObject
            {
                ["Success"] = true,
                ["pid"] = launchedInstance.ProcessId,
                [InstanceIdKey] = launchedInstance.InstanceId,
                ["label"] = launchedInstance.Label,
                ["devenv_path"] = devenvPath,
                [SolutionKey] = launchedInstance.SolutionPath ?? solution ?? string.Empty,
                ["reused"] = false,
            });
        }
        finally
        {
            VsOpenGate.Release();
        }
    }

    private static async Task<int> TryLaunchViaBoundInstanceAsync(JsonNode? id, BridgeConnection bridge, string devenvPath, string? solution)
    {
        if (bridge.CurrentInstance is null)
        {
            return 0;
        }

        JsonObject payload = new()
        {
            ["devenv_path"] = devenvPath,
            [SolutionKey] = solution ?? string.Empty,
        };

        JsonObject response = await bridge.SendIgnoringSolutionHintAsync(id, LaunchVisualStudioCommand, payload.ToJsonString()).ConfigureAwait(false);
        JsonNode? launchedPidNode = response["Data"]?["pid"];
        if (launchedPidNode is null)
        {
            return 0;
        }

        return launchedPidNode.GetValue<int>();
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
                    ["label"] = found.Label,
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
            using Process process = Process.GetProcessById(pid);
            if (force)
            {
                process.Kill(entireProcessTree: true);
            }
            else
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(VsCloseWaitTimeoutMilliseconds) && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            ClearPendingVsLaunch(pid);

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

    private static async Task<int> TryReuseExistingVsProcessAsync(string? solution)
    {
        IReadOnlyList<BridgeInstance> instances = await VsDiscovery.ListAsync().ConfigureAwait(false);
        BridgeInstance? existing = instances.FirstOrDefault(instance =>
            string.IsNullOrWhiteSpace(solution) ||
            string.Equals(instance.SolutionPath, solution, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.ProcessId > 0)
        {
            ClearPendingVsLaunch(existing.ProcessId);
            return existing.ProcessId;
        }

        return 0;
    }

    private static bool TryGetPendingVsLaunch(string? solution, out int pendingPid)
    {
        lock (PendingVsLaunchGate)
        {
            if (PendingVsLaunchPid <= 0)
            {
                pendingPid = 0;
                return false;
            }

            bool solutionMatches = string.IsNullOrWhiteSpace(solution) ||
                string.Equals(PendingVsLaunchSolution, solution, StringComparison.OrdinalIgnoreCase);
            bool launchIsFresh = DateTimeOffset.UtcNow - PendingVsLaunchStartedAtUtc < TimeSpan.FromMinutes(2);
            if (!solutionMatches || !launchIsFresh)
            {
                pendingPid = 0;
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(PendingVsLaunchPid);
                if (!process.HasExited)
                {
                    pendingPid = PendingVsLaunchPid;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogIgnoredException("Failed to probe pending Visual Studio launch state.", ex);
            }

            PendingVsLaunchPid = 0;
            PendingVsLaunchSolution = null;
            PendingVsLaunchStartedAtUtc = default;
            pendingPid = 0;
            return false;
        }
    }

    private static void RecordPendingVsLaunch(int pid, string? solution)
    {
        lock (PendingVsLaunchGate)
        {
            PendingVsLaunchPid = pid;
            PendingVsLaunchSolution = solution;
            PendingVsLaunchStartedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static void ClearPendingVsLaunch(int pid)
    {
        lock (PendingVsLaunchGate)
        {
            if (PendingVsLaunchPid != pid)
            {
                return;
            }

            PendingVsLaunchPid = 0;
            PendingVsLaunchSolution = null;
            PendingVsLaunchStartedAtUtc = default;
        }
    }

    private static async Task<BridgeInstance?> WaitForRegisteredInstanceAsync(int pid, string? solution, int timeoutMs)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<BridgeInstance> instances = await VsDiscovery.ListAsync().ConfigureAwait(false);
                BridgeInstance? instance = instances.FirstOrDefault(instance =>
                    instance.ProcessId == pid &&
                    (string.IsNullOrWhiteSpace(solution) ||
                     string.Equals(instance.SolutionPath, solution, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(instance.SolutionName, System.IO.Path.GetFileName(solution), StringComparison.OrdinalIgnoreCase)));
                if (instance is not null)
                {
                    return instance;
                }
            }
            catch (Exception ex)
            {
                LogIgnoredException($"Failed to query discovery state while waiting for Visual Studio pid {pid}.", ex);
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    break;
                }
            }
            catch
            {
                break;
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

        return null;
    }

    private static void CleanupFailedVsLaunch(int pid)
    {
        ClearPendingVsLaunch(pid);
        DeletePendingLaunchFlags(pid);
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            LogIgnoredException($"Failed to terminate launched Visual Studio pid {pid}.", ex);
        }
    }

    private static int LaunchVisualStudio(string devenvPath, string? solution)
    {
        if (ShouldLaunchInInteractiveSession())
        {
            return LaunchVisualStudioInInteractiveSession(devenvPath, solution);
        }

        ProcessStartInfo psi = new()
        {
            FileName = devenvPath,
            UseShellExecute = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(devenvPath),
        };

        if (!string.IsNullOrWhiteSpace(solution))
        {
            psi.ArgumentList.Add(solution);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Visual Studio launch failed: Process.Start returned null.");
        }

        return process.Id;
    }

    private static bool ShouldLaunchInInteractiveSession()
    {
        if (Environment.UserInteractive)
        {
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    private static bool IsVsOpenLaunchEnabled()
    {
        if (VsOpenLaunchController.IsEnabled)
        {
            return true;
        }

        string? configuredValue = Environment.GetEnvironmentVariable(VsOpenLaunchOptInEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return false;
        }

        return string.Equals(configuredValue, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(configuredValue, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(configuredValue, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int LaunchVisualStudioInInteractiveSession(string devenvPath, string? solution)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            throw new InvalidOperationException("No active interactive Windows session was found.");
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr launchEnvironmentBlock = IntPtr.Zero;
        string? resultPath = null;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");
            }

            if (!DuplicateTokenEx(
                    userToken,
                    TokenAccessLevels.MaximumAllowed,
                    IntPtr.Zero,
                    TokenImpersonationLevel.Identification,
                    TokenType.TokenPrimary,
                    out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            if (!CreateEnvironmentBlock(out environmentBlock, primaryToken, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
            }

            Dictionary<string, string> launchEnvironment = NormalizeLaunchEnvironment(ReadEnvironmentBlock(environmentBlock));
            launchEnvironmentBlock = CreateUnicodeEnvironmentBlock(launchEnvironment);
            string launcherPath = ServiceToolPaths.ResolveInstalledCompanionPath(LauncherExe, solution);
            resultPath = CreateLauncherResultPath(launchEnvironment);

            STARTUPINFO startupInfo = new()
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwShowNormal,
            };

            string commandLine = BuildLauncherCommandLine(launcherPath, devenvPath, resultPath);

            PROCESS_INFORMATION processInformation;
            if (!CreateProcessWithTokenW(
                    primaryToken,
                    LogonWithProfile,
                    null,
                    commandLine,
                    (int)CreateUnicodeEnvironment,
                    launchEnvironmentBlock,
                    System.IO.Path.GetDirectoryName(launcherPath),
                    ref startupInfo,
                    out processInformation))
            {
                int launchError = Marshal.GetLastWin32Error();
                if (launchError != ErrorPrivilegeNotHeld ||
                    !CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment,
                        launchEnvironmentBlock,
                        System.IO.Path.GetDirectoryName(launcherPath),
                        ref startupInfo,
                        out processInformation))
                {
                    throw new Win32Exception(launchError, "Failed to launch the bridge helper in the interactive user session.");
                }
            }

            try
            {
                return WaitForLauncherResult(resultPath, LauncherResultTimeoutMilliseconds);
            }
            finally
            {
                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
            }
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environmentBlock);
            }

            if (launchEnvironmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(launchEnvironmentBlock);
            }

            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            if (userToken != IntPtr.Zero)
            {
                CloseHandle(userToken);
            }

            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                TryDeleteFile(resultPath);
            }
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

    private static void WritePendingSolutionOpenFlag(int pid, string solutionPath)
    {
        string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vs-ide-bridge");
        System.IO.Directory.CreateDirectory(flagDir);
        string flagPath = System.IO.Path.Combine(flagDir, $"bridge-opensolution-{pid}.flag");
        System.IO.File.WriteAllText(flagPath, solutionPath);
    }

    private static void DeletePendingLaunchFlags(int pid)
    {
        string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vs-ide-bridge");
        TryDeleteFile(System.IO.Path.Combine(flagDir, $"bridge-opensolution-{pid}.flag"));
        TryDeleteFile(System.IO.Path.Combine(flagDir, $"bridge-nosolution-{pid}.flag"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogIgnoredException($"Failed to delete pending launch flag '{path}'.", ex);
        }
    }

    private static void LogIgnoredException(string context, Exception ex)
    {
        McpServerLog.Write($"{context} {ex.GetType().Name}: {ex.Message}");
    }

    private static string CreateLauncherResultPath(IReadOnlyDictionary<string, string> environment)
    {
        string? tempDirectory = TryGetEnvironmentValue(environment, "TEMP")
            ?? TryGetEnvironmentValue(environment, "TMP");
        if (string.IsNullOrWhiteSpace(tempDirectory))
        {
            string? localAppData = TryGetEnvironmentValue(environment, "LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                tempDirectory = System.IO.Path.Combine(localAppData, "Temp");
            }
        }

        if (string.IsNullOrWhiteSpace(tempDirectory))
        {
            tempDirectory = System.IO.Path.GetTempPath();
        }

        string resultDirectory = System.IO.Path.Combine(tempDirectory, "vs-ide-bridge", "launcher-results");
        Directory.CreateDirectory(resultDirectory);
        return System.IO.Path.Combine(resultDirectory, $"vs-launcher-{Guid.NewGuid():N}.json");
    }

    private static string? TryGetEnvironmentValue(IReadOnlyDictionary<string, string> environment, string variableName)
    {
        return environment.TryGetValue(variableName, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string BuildLauncherCommandLine(string launcherPath, string devenvPath, string resultPath)
    {
        StringBuilder commandLine = new();
        commandLine.Append(QuoteCommandLineArg(launcherPath));
        commandLine.Append(' ');
        commandLine.Append("--devenv-path ");
        commandLine.Append(QuoteCommandLineArg(devenvPath));
        commandLine.Append(' ');
        commandLine.Append("--result-file ");
        commandLine.Append(QuoteCommandLineArg(resultPath));

        return commandLine.ToString();
    }

    private static int WaitForLauncherResult(string resultPath, int timeoutMs)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (File.Exists(resultPath))
                {
                    return ParseLauncherResult(File.ReadAllText(resultPath));
                }
            }
            catch (IOException)
            {
                // Helper may still be flushing the result file.
            }
            catch (UnauthorizedAccessException)
            {
                // Helper may still be writing or antivirus may be probing.
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"Launcher helper did not report a devenv process within {timeoutMs} ms.");
    }

    private static int ParseLauncherResult(string payload)
    {
        JsonObject? result = JsonNode.Parse(payload) as JsonObject;
        if (result is null)
        {
            throw new InvalidOperationException("Launcher helper returned invalid JSON.");
        }

        bool success = result["success"]?.GetValue<bool>() ?? false;
        if (!success)
        {
            string error = result["error"]?.GetValue<string>() ?? "Launcher helper failed without an error message.";
            throw new InvalidOperationException(error);
        }

        int? pid = result["pid"]?.GetValue<int?>();
        if (!pid.HasValue || pid.Value <= 0)
        {
            throw new InvalidOperationException("Launcher helper did not return a valid devenv process id.");
        }

        return pid.Value;
    }

    private static Dictionary<string, string> ReadEnvironmentBlock(IntPtr environmentBlock)
    {
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        if (environmentBlock == IntPtr.Zero)
        {
            return environment;
        }

        IntPtr cursor = environmentBlock;
        while (true)
        {
            string? entry = Marshal.PtrToStringUni(cursor);
            if (string.IsNullOrEmpty(entry))
            {
                return environment;
            }

            int separatorIndex = entry.IndexOf('=');
            if (separatorIndex > 0)
            {
                environment[entry[..separatorIndex]] = entry[(separatorIndex + 1)..];
            }

            cursor = IntPtr.Add(cursor, (entry.Length + 1) * sizeof(char));
        }
    }

    private static Dictionary<string, string> NormalizeLaunchEnvironment(Dictionary<string, string> environment)
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        ApplyEnvironmentValue(environment, "SystemRoot", windowsDirectory);
        ApplyEnvironmentValue(environment, "windir", windowsDirectory);

        string? userProfile = TryGetEnvironmentValue(environment, "USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        ApplyEnvironmentValue(environment, "USERPROFILE", userProfile);

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string? homeDrive = System.IO.Path.GetPathRoot(userProfile)?.TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(homeDrive))
            {
                ApplyEnvironmentValue(environment, "HOMEDRIVE", homeDrive);
                string homePath = userProfile.StartsWith(homeDrive, StringComparison.OrdinalIgnoreCase)
                    ? userProfile[homeDrive.Length..]
                    : userProfile;
                if (string.IsNullOrWhiteSpace(homePath))
                {
                    homePath = "\\";
                }

                ApplyEnvironmentValue(environment, "HOMEPATH", homePath);
            }
        }

        string? localAppData = TryGetEnvironmentValue(environment, "LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        ApplyEnvironmentValue(environment, "LOCALAPPDATA", localAppData);

        string? appData = TryGetEnvironmentValue(environment, "APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        ApplyEnvironmentValue(environment, "APPDATA", appData);

        string? tempDirectory = TryGetEnvironmentValue(environment, "TEMP")
            ?? TryGetEnvironmentValue(environment, "TMP");
        if (string.IsNullOrWhiteSpace(tempDirectory) && !string.IsNullOrWhiteSpace(localAppData))
        {
            tempDirectory = System.IO.Path.Combine(localAppData, "Temp");
        }

        ApplyEnvironmentValue(environment, "TEMP", tempDirectory);
        ApplyEnvironmentValue(environment, "TMP", tempDirectory);
        return environment;
    }

    private static void ApplyEnvironmentValue(IDictionary<string, string> environment, string variableName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment[variableName] = value;
        }
    }

    private static IntPtr CreateUnicodeEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        StringBuilder payload = new();
        foreach (KeyValuePair<string, string> entry in environment.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            payload.Append(entry.Key);
            payload.Append('=');
            payload.Append(entry.Value ?? string.Empty);
            payload.Append('\0');
        }

        payload.Append('\0');

        IntPtr block = Marshal.AllocHGlobal(payload.Length * sizeof(char));
        Marshal.Copy(payload.ToString().ToCharArray(), 0, block, payload.Length);
        return block;
    }

    private static string QuoteCommandLineArg(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Contains('\"') && !value.Contains(' ') && !value.Contains('\t'))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        TokenAccessLevels desiredAccess,
        IntPtr tokenAttributes,
        TokenImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        int logonFlags,
        string? applicationName,
        string commandLine,
        int creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum TokenType
    {
        TokenPrimary = 1,
        TokenImpersonation = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
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
                ["label"] = instance.Label,
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
