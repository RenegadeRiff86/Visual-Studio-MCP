using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
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
            primaryToken = CreatePrimaryUserToken(sessionId, out userToken);
            environmentBlock = CreateEnvironmentBlockForToken(primaryToken);

            Dictionary<string, string> launchEnvironment = NormalizeLaunchEnvironment(ReadEnvironmentBlock(environmentBlock));
            launchEnvironmentBlock = CreateUnicodeEnvironmentBlock(launchEnvironment);
            string launcherPath = VsIdeBridgeService.SystemTools.ServiceToolPaths.ResolveInstalledCompanionPath(LauncherExe, solution);
            resultPath = CreateLauncherResultPath(launchEnvironment);
            string commandLine = BuildLauncherCommandLine(launcherPath, devenvPath, resultPath);

            PROCESS_INFORMATION processInformation = CreateInteractiveLauncherProcess(
                primaryToken,
                launchEnvironmentBlock,
                launcherPath,
                commandLine);

            try
            {
                return WaitForLauncherResult(resultPath, LauncherResultTimeoutMilliseconds);
            }
            finally
            {
                CloseProcessHandles(processInformation);
            }
        }
        finally
        {
            CleanupInteractiveLaunchResources(userToken, primaryToken, environmentBlock, launchEnvironmentBlock, resultPath);
        }
    }

    private static IntPtr CreatePrimaryUserToken(uint sessionId, out IntPtr userToken)
    {
        userToken = IntPtr.Zero;
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
                out IntPtr primaryToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
        }

        return primaryToken;
    }

    private static IntPtr CreateEnvironmentBlockForToken(IntPtr primaryToken)
    {
        if (!CreateEnvironmentBlock(out IntPtr environmentBlock, primaryToken, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
        }

        return environmentBlock;
    }

    private static PROCESS_INFORMATION CreateInteractiveLauncherProcess(
        IntPtr primaryToken,
        IntPtr launchEnvironmentBlock,
        string launcherPath,
        string commandLine)
    {
        STARTUPINFO startupInfo = CreateLaunchStartupInfo();
        PROCESS_INFORMATION processInformation;
        string? workingDirectory = System.IO.Path.GetDirectoryName(launcherPath);

        if (CreateProcessWithTokenW(
                primaryToken,
                LogonWithProfile,
                null,
                commandLine,
                (int)CreateUnicodeEnvironment,
                launchEnvironmentBlock,
                workingDirectory,
                ref startupInfo,
                out processInformation))
        {
            return processInformation;
        }

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
                workingDirectory,
                ref startupInfo,
                out processInformation))
        {
            throw new Win32Exception(launchError, "Failed to launch the bridge helper in the interactive user session.");
        }

        return processInformation;
    }

    private static STARTUPINFO CreateLaunchStartupInfo()
    {
        return new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = @"winsta0\default",
            dwFlags = StartfUseShowWindow,
            wShowWindow = SwShowNormal,
        };
    }

    private static void CloseProcessHandles(PROCESS_INFORMATION processInformation)
    {
        CloseHandle(processInformation.hThread);
        CloseHandle(processInformation.hProcess);
    }

    private static void CleanupInteractiveLaunchResources(
        IntPtr userToken,
        IntPtr primaryToken,
        IntPtr environmentBlock,
        IntPtr launchEnvironmentBlock,
        string? resultPath)
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

    private static string ResolveDevenvPath(JsonNode? id)
    {
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
            catch (Win32Exception ex)
            {
                LogIgnoredException("Failed to probe devenv path with vswhere.", ex);
            }
            catch (InvalidOperationException ex)
            {
                LogIgnoredException("Failed to probe devenv path with vswhere.", ex);
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
        string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), PendingLaunchFlagDirectoryName);
        Directory.CreateDirectory(flagDir);
        string flagPath = System.IO.Path.Combine(flagDir, $"bridge-opensolution-{pid}.flag");
        File.WriteAllText(flagPath, solutionPath);
    }

    private static void WriteNoSolutionFlag(int pid)
    {
        string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), PendingLaunchFlagDirectoryName);
        Directory.CreateDirectory(flagDir);
        File.WriteAllText(System.IO.Path.Combine(flagDir, $"bridge-nosolution-{pid}.flag"), string.Empty);
    }

    private static void DeletePendingLaunchFlags(int pid)
    {
        string flagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), PendingLaunchFlagDirectoryName);
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
        catch (IOException ex)
        {
            LogIgnoredException($"Failed to delete pending launch flag '{path}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
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
            tempDirectory = System.IO.Path.GetTempPath();
        }

        string vsIdeBridgeTempDirectory = System.IO.Path.Combine(tempDirectory, PendingLaunchFlagDirectoryName);
        Directory.CreateDirectory(vsIdeBridgeTempDirectory);
        string path = System.IO.Path.Combine(vsIdeBridgeTempDirectory, $"launcher-{Guid.NewGuid():N}.json");

        const int maxPathLength = 240;
        if (path.Length <= maxPathLength)
        {
            return path;
        }

        string fallbackBase = Environment.GetEnvironmentVariable("ProgramData")
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VsIdeBridge");
        string fallbackTempDirectory = System.IO.Path.Combine(fallbackBase, "Temp");
        Directory.CreateDirectory(fallbackTempDirectory);
        return System.IO.Path.Combine(fallbackTempDirectory, $"launcher-{Guid.NewGuid():N}.json");
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
        commandLine.Append(QuoteCommandLineArg(devenvPath));
        commandLine.Append(' ');
        commandLine.Append(QuoteCommandLineArg(resultPath));
        return commandLine.ToString();
    }

    private static int WaitForLauncherResult(string resultPath, int timeoutMilliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (File.Exists(resultPath))
            {
                string json = File.ReadAllText(resultPath);
                return ParseLauncherResult(resultPath, json);
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException(
            $"Timed out waiting for '{LauncherExe}' to report the launched Visual Studio process.");
    }

    private static int ParseLauncherResult(string resultPath, string json)
    {
        JsonNode? payload = JsonNode.Parse(json);
        if (payload is null)
        {
            throw new InvalidOperationException($"Launcher result '{resultPath}' was empty.");
        }

        string? error = payload["error"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException(error);
        }

        JsonNode? pidNode = payload["pid"];
        int? pid = pidNode?.GetValue<int?>();
        if (!pid.HasValue || pid.Value <= 0)
        {
            throw new InvalidOperationException(
                $"Launcher result '{resultPath}' did not contain a valid Visual Studio process id.");
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
}
