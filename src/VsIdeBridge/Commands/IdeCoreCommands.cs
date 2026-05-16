using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Commands;

internal static partial class IdeCoreCommands
{
    private const string WarningsCommandName = "warnings";
    private const string WarningsPropertyName = "warnings";
    private const string MessagesCommandName = "messages";
    private const string MessagesPropertyName = "messages";
    private const string ExampleCppPath = @"C:\repo\src\foo.cpp";
    private static readonly string WarningsCommandExample =
        CreateExampleCommand(WarningsCommandName, ("group_by", new JValue("code")));
    private static readonly string MessagesCommandExample =
        CreateExampleCommand(MessagesCommandName, ("group_by", new JValue("code")));

    private static string CreateExampleCommand(string commandName, params (string Name, JToken Value)[] args)
    {
        if (args.Length == 0)
            return commandName;

        JObject payload = [];
        foreach ((string name, JToken value) in args)
            payload[name] = value.DeepClone();

        return commandName + " " + SerializeCompactJson(payload);
    }

    private static string SerializeCompactJson(JToken token)
    {
        return JsonConvert.SerializeObject(token, Formatting.None);
    }

    private static string CreateExampleFileCommand(string commandName)
    {
        return CreateExampleCommand(commandName, ("file", new JValue(ExampleCppPath)));
    }

    private static (string Name, JToken Value)[] CreateExampleLocationArguments()
    {
        return
        [
            ("file", new JValue(ExampleCppPath)),
            ("line", new JValue(42)),
            ("column", new JValue(13)),
        ];
    }

    private static JArray CreateExampleSlicesRanges()
    {
        return
        [
            new JObject
            {
                ["file"] = ExampleCppPath,
                ["line"] = 42,
                ["context_before"] = 8,
                ["context_after"] = 20,
            }
        ];
    }

    private static string CreateExampleLocationCommand(string commandName)
    {
        return CreateExampleCommand(commandName, CreateExampleLocationArguments());
    }


    private static JObject GetUiSettingsData(IdeCommandContext context)
    {
        return new JObject
        {
            ["allowBridgeShellExec"] = context.Runtime.UiSettings.AllowBridgeShellExec,
            ["allowBridgePythonExecution"] = context.Runtime.UiSettings.AllowBridgePythonExecution,
            ["allowBridgePythonUnrestrictedExecution"] = context.Runtime.UiSettings.AllowBridgePythonUnrestrictedExecution,
            ["allowBridgePythonEnvironmentMutation"] = context.Runtime.UiSettings.AllowBridgePythonEnvironmentMutation,
            ["bestPracticeDiagnostics"] = context.Runtime.UiSettings.BestPracticeDiagnosticsEnabled,
            ["goToEditedParts"] = context.Runtime.UiSettings.GoToEditedParts,
            ["allowBridgeBuild"] = context.Runtime.UiSettings.AllowBridgeBuild,
            ["httpServerEnabled"] = HttpServerStateManager.IsEnabled,
            ["streamableHttpServerEnabled"] = StreamableHttpServerStateManager.IsEnabled,
        };
    }

    private static Task<CommandExecutionResult> GetUiSettingsAsync(IdeCommandContext context)
    {
        return Task.FromResult(new CommandExecutionResult(
            "IDE Bridge UI settings captured.",
            GetUiSettingsData(context)));
    }

    private static async Task<CommandExecutionResult> ToggleHttpServerAsync(IdeCommandContext context)
    {
        bool enabled = !HttpServerStateManager.IsEnabled;

        try
        {
            if (enabled)
            {
                await HttpServerStateManager.EnableAndReconcileAsync().ConfigureAwait(true);
            }
            else
            {
                await HttpServerStateManager.DisableAndReconcileAsync().ConfigureAwait(true);
            }

            context.Runtime.UiSettings.HttpServerEnabled = enabled;

            string statusMessage = enabled 
                ? $"HTTP MCP server enabled on {HttpServerStateManager.Url}"
                : "HTTP MCP server disabled.";

            return new CommandExecutionResult(
                statusMessage,
                new JObject
                {
                    ["enabled"] = enabled,
                    ["port"] = HttpServerStateManager.DefaultPort,
                    ["url"] = HttpServerStateManager.Url
                });
        }
        catch (Exception ex) when (ex is not null) // rethrow as CommandErrorException so callers get a clean error code
        {
            throw new CommandErrorException("http_toggle_failed", $"Failed to toggle the bridge HTTP server: {ex.Message}. This is a bridge-internal error — wait a moment and retry, or restart Visual Studio if it persists.");
        }
    }

    internal sealed class IdeToggleHttpServerMenuCommand : IdeCommandBase
    {
        public IdeToggleHttpServerMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x010B, acceptsParameters: false)
        {
            MenuCommand.BeforeQueryStatus += (_, _) => MenuCommand.Checked = HttpServerStateManager.IsEnabled;
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeToggleHttpServer";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ToggleHttpServerAsync(context);
        }
    }

    private static async Task<CommandExecutionResult> ToggleStreamableHttpServerAsync(IdeCommandContext _)
    {
        bool enabled = !StreamableHttpServerStateManager.IsEnabled;

        try
        {
            if (enabled)
                await StreamableHttpServerStateManager.EnableAndReconcileAsync().ConfigureAwait(true);
            else
                await StreamableHttpServerStateManager.DisableAndReconcileAsync().ConfigureAwait(true);

            string statusMessage = enabled
                ? $"Streamable HTTP MCP server enabled on {StreamableHttpServerStateManager.Url}"
                : "Streamable HTTP MCP server disabled.";

            return new CommandExecutionResult(
                statusMessage,
                new JObject
                {
                    ["enabled"] = enabled,
                    ["port"] = StreamableHttpServerStateManager.DefaultPort,
                    ["url"] = StreamableHttpServerStateManager.Url
                });
        }
        catch (Exception ex) when (ex is not null)
        {
            throw new CommandErrorException("streamable_http_toggle_failed", $"Failed to toggle the Streamable HTTP server: {ex.Message}. Wait a moment and retry, or restart Visual Studio if it persists.");
        }
    }

    internal sealed class IdeToggleStreamableHttpServerMenuCommand : IdeCommandBase
    {
        public IdeToggleStreamableHttpServerMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0269, acceptsParameters: false)
        {
            MenuCommand.BeforeQueryStatus += (_, _) => MenuCommand.Checked = StreamableHttpServerStateManager.IsEnabled;
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeToggleStreamableHttpServer";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ToggleStreamableHttpServerAsync(context);
        }
    }

    private static async Task<CommandExecutionResult> ToggleBestPracticeAsync(IdeCommandContext context)
    {
        // Read from _uiSettings (the authoritative flag read by RefreshBestPracticeDiagnosticsAsync).
        // Default is true when the setting has never been written.
        bool enabling = !context.Runtime.UiSettings.BestPracticeDiagnosticsEnabled;
        try
        {
            // Keep both state stores in sync: _uiSettings gates RefreshBestPracticeDiagnosticsAsync;
            // BestPracticeStateManager gates AnalyzeBestPracticeFindings (static path used by pre-write checks).
            context.Runtime.UiSettings.BestPracticeDiagnosticsEnabled = enabling;
            if (enabling)
                BestPracticeStateManager.Enable();
            else
                BestPracticeStateManager.Disable();

            await context.Runtime.ErrorListService
                .RefreshBestPracticeDiagnosticsAsync(context)
                .ConfigureAwait(true);

            string msg = enabling
                ? "Best-practice analysis enabled."
                : "Best-practice analysis disabled.";
            return new CommandExecutionResult(msg, new JObject { ["enabled"] = enabling });
        }
        catch (Exception ex) when (ex is not null)
        {
            throw new CommandErrorException("best_practice_toggle_failed",
                $"Failed to toggle best-practice analysis: {ex.Message}.");
        }
    }

    internal sealed class IdeToggleBestPracticeMenuCommand : IdeCommandBase
    {
        public IdeToggleBestPracticeMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x026A, acceptsParameters: false)
        {
            // Read from _uiSettings to match the toggle's authoritative state store.
            MenuCommand.BeforeQueryStatus += (_, _) => MenuCommand.Checked = runtime.UiSettings.BestPracticeDiagnosticsEnabled;
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeToggleBestPractice";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
            => ToggleBestPracticeAsync(context);
    }

    internal sealed class IdeRequestApprovalCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x024B)
    {
        protected override string CanonicalName => "Tools.VsIdeBridgeRequestApproval";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string operation = args.GetRequiredString("operation");
            BridgeApprovalKind approvalKind = operation switch
            {
                "shell_exec" => BridgeApprovalKind.ShellExec,
                "python_exec" => BridgeApprovalKind.PythonExecution,
                "python_env_mutation" => BridgeApprovalKind.PythonEnvironmentMutation,
                _ => throw new CommandErrorException("invalid_arguments", $"Unsupported approval operation: '{operation}'. Valid values are: shell_exec, python_exec, python_env_mutation."),
            };

            JObject commandData = await context.Runtime.BridgeApprovalService
                .RequestApprovalAsync(
                    context,
                    approvalKind,
                    args.GetString("subject"),
                    args.GetString("details"))
                .ConfigureAwait(true);

            return new CommandExecutionResult("Bridge approval granted.", commandData);
        }
    }

    internal sealed class IdeGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0200)
    {
        protected override string CanonicalName => "Tools.IdeGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult(
                "IDE state captured. " +
                "Next: call_tool with name \"errors\" to check the Error List, " +
                "\"read_file\" to read a file, \"find_text\" to search, " +
                "\"list_projects\" to list projects, or \"list_tool_categories\" to browse all available tools.",
                state);
        }
    }

    internal sealed class IdeGetUiSettingsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x024C)
    {
        protected override string CanonicalName => "Tools.IdeGetUiSettings";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return GetUiSettingsAsync(context);
        }
    }

    internal sealed class IdeWaitForReadyCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0201)
    {
        protected override string CanonicalName => "Tools.IdeWaitForReady";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            int timeout = args.GetInt32("timeout-ms", 120000);
            JObject commandData = await context.Runtime.ReadinessService.WaitForReadyAsync(context, timeout).ConfigureAwait(true);
            return new CommandExecutionResult("Readiness wait completed.", commandData);
        }
    }

    internal sealed class IdeOpenSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0224)
    {
        protected override string CanonicalName => "Tools.IdeOpenSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string solutionPath = args.GetRequiredString("solution");
            if (!File.Exists(solutionPath))
            {
                throw new CommandErrorException("file_not_found", $"Solution file not found: {solutionPath}. Call search_solutions to locate the correct .sln or .slnx path, then retry with the resolved path.");
            }
            string ext = Path.GetExtension(solutionPath);
            if (!string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandErrorException("invalid_file_type", $"The path '{solutionPath}' is not a solution file. The solution parameter must point to a .sln or .slnx file.");
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            context.Dte.Solution.Open(solutionPath);
            return new CommandExecutionResult("Solution opened.", new JObject { ["solutionPath"] = solutionPath });
        }
    }

    internal sealed class IdeLaunchVisualStudioCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x02F8)
    {
        protected override string CanonicalName => "Tools.IdeLaunchVisualStudio";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string devenvPath = args.GetRequiredString("devenv_path");
            string? solutionPath = args.GetString("solution");

            if (!File.Exists(devenvPath))
            {
                throw new CommandErrorException("file_not_found", $"devenv.exe was not found at '{devenvPath}'. Verify the path — the default location is C:\\Program Files\\Microsoft Visual Studio\\<version>\\<edition>\\Common7\\IDE\\devenv.exe.");
            }

            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                string extension = Path.GetExtension(solutionPath);
                if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommandErrorException("invalid_file_type", $"The path '{solutionPath}' is not a solution file. The solution parameter must point to a .sln or .slnx file.");
                }

                if (!File.Exists(solutionPath))
                {
                    throw new CommandErrorException("file_not_found", $"Solution file not found: {solutionPath}. Call search_solutions to locate the correct .sln or .slnx path, then retry with the resolved path.");
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            ProcessStartInfo startInfo = new()
            {
                FileName = devenvPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(devenvPath),
            };

            using Process? process = Process.Start(startInfo);
            _ = process ?? throw new CommandErrorException("launch_failed", $"Visual Studio failed to launch from '{devenvPath}'. Verify that devenv.exe exists at the specified path and that you have permission to run it.");

            if (solutionPath is string verifiedSolutionPath && !string.IsNullOrWhiteSpace(verifiedSolutionPath))
            {
                WritePendingSolutionOpenFlag(process.Id, verifiedSolutionPath);
            }
            else
            {
                WriteNoSolutionFlag(process.Id);
            }

            return new CommandExecutionResult(
                "Visual Studio launch requested.",
                new JObject
                {
                    ["pid"] = process.Id,
                    ["devenvPath"] = devenvPath,
                    ["solutionPath"] = (JToken?)solutionPath ?? JValue.CreateNull(),
                });
        }

        private static void WritePendingSolutionOpenFlag(int pid, string solutionPath)
        {
            string flagDirectory = Path.Combine(Path.GetTempPath(), "vs-ide-bridge");
            Directory.CreateDirectory(flagDirectory);
            File.WriteAllText(Path.Combine(flagDirectory, $"bridge-opensolution-{pid}.flag"), solutionPath);
        }

        private static void WriteNoSolutionFlag(int pid)
        {
            string flagDirectory = Path.Combine(Path.GetTempPath(), "vs-ide-bridge");
            Directory.CreateDirectory(flagDirectory);
            File.WriteAllText(Path.Combine(flagDirectory, $"bridge-nosolution-{pid}.flag"), string.Empty);
        }
    }

    internal sealed class IdeCreateSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x023D)
    {
        protected override string CanonicalName => "Tools.IdeCreateSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string directory = Path.GetFullPath(args.GetRequiredString("directory"));
            string requestedName = args.GetRequiredString("name");
            string solutionName = NormalizeSolutionName(requestedName);
            string solutionPath = Path.Combine(directory, solutionName + ".sln");

            if (File.Exists(solutionPath))
            {
                throw new CommandErrorException("file_exists", $"A solution file already exists at {solutionPath}. To open it, call open_solution with that path. To create a new solution, choose a different name or directory.");
            }

            Directory.CreateDirectory(directory);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            context.Dte.Solution.Create(directory, solutionName);
            context.Dte.Solution.SaveAs(solutionPath);

            return new CommandExecutionResult(
                "Solution created.",
                new JObject
                {
                    ["solutionName"] = solutionName,
                    ["solutionPath"] = solutionPath,
                    ["directory"] = directory,
                });
        }

        private static string NormalizeSolutionName(string requestedName)
        {
            string trimmedName = requestedName.Trim();
            if (trimmedName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                trimmedName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                trimmedName = Path.GetFileNameWithoutExtension(trimmedName);
            }

            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                throw new CommandErrorException("invalid_arguments", "The name parameter must not be empty. Pass a valid solution name like 'MySolution'.");
            }

            if (!string.Equals(Path.GetFileName(trimmedName), trimmedName, StringComparison.Ordinal))
            {
                throw new CommandErrorException("invalid_arguments", "The name parameter must be a plain solution name, not a file path. Pass the name only (e.g. 'MySolution') — use the directory parameter to specify where to create it.");
            }

            if (trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new CommandErrorException("invalid_arguments", $"The name '{requestedName}' contains characters that are not valid in a file name. Remove any of: \\ / : * ? \" < > |");
            }

            return trimmedName;
        }
    }

    internal sealed class IdeCloseIdeCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0231)
    {
        private const int CloseIdeDelayMilliseconds = 300;

        protected override string CanonicalName => "Tools.IdeCloseIde";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            // Schedule quit after the response is written to the pipe
            _ = Task.Run(async () =>
            {
                await Task.Delay(CloseIdeDelayMilliseconds).ConfigureAwait(false);
                await context.Package.JoinableTaskFactory.SwitchToMainThreadAsync();
                context.Dte.Quit();
            });

            return Task.FromResult(new CommandExecutionResult(
                "Closing IDE.",
                new JObject { ["closing"] = true }));
        }
    }

}



