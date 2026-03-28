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

internal static class IdeCoreCommands
{
    private const string WarningsCommandName = "warnings";
    private const string WarningsPropertyName = "warnings";
    private const string ExampleCppPath = @"C:\repo\src\foo.cpp";
    private static readonly string WarningsCommandExample =
        CreateExampleCommand(WarningsCommandName, ("group_by", new JValue("code")));

    private static string CreateExampleCommand(string commandName, params (string Name, JToken Value)[] args)
    {
        if (args.Length == 0)
            return commandName;

        JObject payload = new JObject();
        foreach ((string name, JToken value) in args)
            payload[name] = value.DeepClone();

        return commandName + " " + payload.ToString(Formatting.None);
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

    internal static async Task<CommandExecutionResult> ExecuteBatchAsync(IdeCommandContext context, JArray steps, bool stopOnError)
    {
        JArray results = new JArray();
        int successCount = 0;
        int failureCount = 0;
        bool stoppedEarly = false;

        for (int i = 0; i < steps.Count; i++)
        {
            (JObject stepResult, bool succeeded) = await ExecuteBatchStepAsync(context, steps[i], i).ConfigureAwait(true);
            if (succeeded) successCount++; else failureCount++;
            results.Add(stepResult);

            if (stopOnError && !(stepResult.Value<bool?>("success") ?? false))
            {
                stoppedEarly = i < steps.Count - 1;
                break;
            }
        }

        JObject commandData = new JObject
        {
            ["batchCount"] = steps.Count,
            ["successCount"] = successCount,
            ["failureCount"] = failureCount,
            ["stoppedEarly"] = stoppedEarly,
            ["results"] = results,
        };

        return new CommandExecutionResult(
            $"Batch: {successCount}/{steps.Count} succeeded, {failureCount} failed.",
            commandData);
    }

    private static async Task<(JObject result, bool succeeded)> ExecuteBatchStepAsync(
        IdeCommandContext context, JToken entry, int index)
    {
        if (entry is not JObject step)
        {
            JObject stepResult = new JObject
            {
                ["index"] = index,
                ["id"] = JValue.CreateNull(),
                ["command"] = string.Empty,
                ["success"] = false,
                ["summary"] = "Batch entry must be a JSON object.",
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = "invalid_batch_entry", ["message"] = "Batch entry must be a JSON object." },
            };
            return (stepResult, false);
        }

        string? stepId = (string?)step["id"];
        string commandName = (string?)step["command"] ?? string.Empty;
        string commandArgs = (string?)step["args"] ?? string.Empty;

        if (!context.Runtime.TryGetCommand(commandName, out var cmd))
        {
            JObject stepResult = new JObject
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = false,
                ["summary"] = $"Unknown command: {commandName}",
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = "unknown_command", ["message"] = $"Command not registered: {commandName}" },
            };
            return (stepResult, false);
        }

        CommandArguments parsedArgs = CommandArgumentParser.Parse(commandArgs);
        try
        {
            CommandExecutionResult commandResult = await cmd.ExecuteDirectAsync(context, parsedArgs).ConfigureAwait(true);
            JObject stepResult = new JObject
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = true,
                ["summary"] = commandResult.Summary,
                [WarningsPropertyName] = commandResult.Warnings,
                ["data"] = commandResult.Data,
                ["error"] = JValue.CreateNull(),
            };
            return (stepResult, true);
        }
        catch (CommandErrorException ex)
        {
            JObject stepResult = new JObject
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = false,
                ["summary"] = ex.Message,
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = ex.Code, ["message"] = ex.Message },
            };
            return (stepResult, false);
        }
        catch (Exception ex)
        {
            JObject stepResult = new JObject
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = false,
                ["summary"] = ex.Message,
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = "internal_error", ["message"] = ex.Message },
            };
            return (stepResult, false);
        }
    }

    private static Task<CommandExecutionResult> GetHelpResultAsync()
    {
        BridgeCommandMetadata[] commandMetadata = BridgeCommandCatalog.All
            .OrderBy(item => item.PipeName, StringComparer.Ordinal)
            .ToArray();
        string generatedAtUtc = DateTime.UtcNow.ToString("O");
        JArray commandDetails = BuildCommandDetails(commandMetadata);

        JArray commands = new JArray();
        JArray legacyCommands = new JArray();
        foreach (BridgeCommandMetadata command in commandMetadata)
        {
            commands.Add(command.PipeName);
            legacyCommands.Add(command.CanonicalName);
        }

        return Task.FromResult(new CommandExecutionResult(
            "Command catalog written.",
            new JObject
            {
                ["schemaVersion"] = "vs-ide-bridge.help.v1",
                ["generatedAtUtc"] = generatedAtUtc,
                ["catalog"] = new JObject
                {
                    ["schemaVersion"] = "vs-ide-bridge.command-catalog.v1",
                    ["generatedAtUtc"] = generatedAtUtc,
                    ["count"] = commandMetadata.Length,
                    ["commands"] = commandDetails.DeepClone(),
                    ["nameField"] = "name",
                    ["canonicalNameField"] = "canonicalName",
                    ["exampleField"] = "example",
                    ["aliasesField"] = "aliases",
                    ["notes"] = new JArray
                    {
                        "Use name for pipe/MCP command routing.",
                        "Use canonicalName only for compatibility mapping or VS Command Window fallbacks.",
                    },
                },
                ["commands"] = commands,
                ["legacyCommands"] = legacyCommands,
                ["note"] = "Pipe requests accept the simple command names in commands[]. The legacy Tools.Ide* names still work in Visual Studio and over the pipe.",
                ["commandDetails"] = commandDetails,
                ["recipes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "find-symbol-definition",
                        ["summary"] = "Use symbol search before text search when you know the identifier name.",
                        ["command"] = CreateExampleCommand("search-symbols", ("query", new JValue("propose_export_file_name_and_path")), ("kind", new JValue("function")))
                    },
                    new JObject
                    {
                        ["name"] = "inspect-symbol-at-location",
                        ["summary"] = "Use quick info to get the destination location and nearby definition context.",
                        ["command"] = CreateExampleLocationCommand("quick-info")
                    },
                    new JObject
                    {
                        ["name"] = "group-current-warnings",
                        ["summary"] = "Filter the Error List down to warnings and group them by code.",
                        ["command"] = WarningsCommandExample
                    },
                    new JObject
                    {
                        ["name"] = "fetch-multiple-slices",
                        ["summary"] = "Use inline ranges JSON when you need several code windows in one round-trip.",
                        ["command"] = CreateExampleCommand("document-slices", ("ranges", CreateExampleSlicesRanges()))
                    }
                },
                ["example"] = CreateExampleCommand("state", ("out", new JValue(@"C:\temp\ide-state.json"))),
                ["documentSliceExample"] = CreateExampleCommand("document-slice", ("file", new JValue(ExampleCppPath)), ("start_line", new JValue(120)), ("end_line", new JValue(180)), ("out", new JValue(@"C:\temp\slice.json"))),
                ["documentSlicesExample"] = CreateExampleCommand("document-slices", ("ranges", CreateExampleSlicesRanges())),
                ["searchSymbolsExample"] = CreateExampleCommand("search-symbols", ("query", new JValue("propose_export_file_name_and_path")), ("kind", new JValue("function"))),
                ["quickInfoExample"] = CreateExampleLocationCommand("quick-info"),
                ["findTextPathExample"] = CreateExampleCommand("find-text", ("query", new JValue("OnInit")), ("path", new JValue("src\\libslic3r"))),
                ["fileSymbolsExample"] = CreateExampleCommand("file-symbols", ("file", new JValue(ExampleCppPath)), ("kind", new JValue("function"))),
                ["smartContextExample"] = CreateExampleCommand("smart-context", ("query", new JValue("where is GUI_App::OnInit used")), ("max_contexts", new JValue(3)), ("out", new JValue(@"C:\temp\smart-context.json"))),
                ["referencesExample"] = CreateExampleCommand("find-references", [.. CreateExampleLocationArguments(), ("out", new JValue(@"C:\temp\references.json"))]),
                ["callHierarchyExample"] = CreateExampleCommand("call-hierarchy", [.. CreateExampleLocationArguments(), ("out", new JValue(@"C:\temp\call-hierarchy.json"))]),
                ["applyDiffFormat"] = "Use unified diff text with ---/+++ file headers and @@ hunks, or editor patch text with *** Begin Patch / *** End Patch and *** Update File / *** Add File / *** Delete File blocks.",
                ["applyDiffExample"] = CreateExampleCommand("apply-diff", ("patch_file", new JValue(@"C:\temp\change.diff")), ("out", new JValue(@"C:\temp\apply-diff.json"))),
                ["openSolutionExample"] = CreateExampleCommand("open-solution", ("solution", new JValue(@"C:\path\to\solution.sln")), ("out", new JValue(@"C:\temp\open-solution.json")))
            }));
    }

    private static JArray BuildCommandDetails(IEnumerable<BridgeCommandMetadata> commandMetadata)
    {
        JArray details = new JArray();
        foreach (BridgeCommandMetadata command in commandMetadata)
        {
            JArray aliases = new JArray();
            foreach (string alias in PipeCommandNames.GetAliases(command.CanonicalName))
            {
                if (string.Equals(alias, command.PipeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                aliases.Add(alias);
            }

            details.Add(new JObject
            {
                ["name"] = command.PipeName,
                ["canonicalName"] = command.CanonicalName,
                ["legacyName"] = command.CanonicalName,
                ["description"] = command.Description,
                ["example"] = command.Example,
                ["aliases"] = aliases,
            });
        }

        return details;
    }

    private static async Task<CommandExecutionResult> GetSmokeTestResultAsync(IdeCommandContext context)
    {
        JObject state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
        return new CommandExecutionResult(
            "Smoke test captured IDE state.",
            new JObject
            {
                ["success"] = true,
                ["state"] = state,
            });
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
                HttpServerStateManager.Enable();
            }
            else
            {
                HttpServerStateManager.Disable();
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
        catch (Exception ex)
        {
            throw new CommandErrorException("http_toggle_failed", $"Failed to toggle HTTP server: {ex.Message}");
        }
    }

    private static string? TryResolveSolutionDocPath(IdeCommandContext context, string fileName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            string? solutionPath = context.Dte.Solution?.FullName;
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                return null;
            }

            string? solutionDirectory = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return null;
            }

            string documentPath = Path.Combine(solutionDirectory, fileName);
            return File.Exists(documentPath) ? documentPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CommandExecutionResult> ShowHelpMenuAsync(IdeCommandContext context)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        string? readmePath = TryResolveSolutionDocPath(context, "README.md");
        string? bugsPath = TryResolveSolutionDocPath(context, "BUGS.md");
        if (!string.IsNullOrWhiteSpace(readmePath))
        {
            context.Dte.ItemOperations.OpenFile(readmePath);
        }

        string message = string.IsNullOrWhiteSpace(readmePath)
            ? "README.md could not be resolved from the current solution. Start with the repo README for setup and usage, check BUGS.md for current runtime gaps, and use Tools.IdeHelp only when you need the raw command catalog."
            : $"Opened README.md for the main product guide.{Environment.NewLine}{Environment.NewLine}Check BUGS.md for current runtime gaps and use Tools.IdeHelp only when you need the raw command catalog.";

        VsShellUtilities.ShowMessageBox(
            context.Package,
            message,
            "VS IDE Bridge",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

        return new CommandExecutionResult(
            string.IsNullOrWhiteSpace(readmePath) ? "Displayed IDE Bridge help." : "Opened IDE Bridge help.",
            new JObject
            {
                ["readmePath"] = (JToken?)readmePath ?? JValue.CreateNull(),
                ["bugsPath"] = (JToken?)bugsPath ?? JValue.CreateNull(),
                ["commandWindowHelp"] = "Tools.IdeHelp",
            });
    }

    internal sealed class IdeHelpMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0102, acceptsParameters: false)
    {
        protected override string CanonicalName => "Tools.VsIdeBridgeHelpMenu";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ShowHelpMenuAsync(context);
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
                _ => throw new CommandErrorException("invalid_arguments", $"Unsupported approval operation: {operation}"),
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

    internal sealed class IdeHelpCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0100)
    {
        protected override string CanonicalName => "Tools.IdeHelp";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return GetHelpResultAsync();
        }
    }

    internal sealed class IdeSmokeTestCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0101)
    {
        protected override string CanonicalName => "Tools.IdeSmokeTest";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return await GetSmokeTestResultAsync(context).ConfigureAwait(true);
        }
    }

    internal sealed class IdeGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0200)
    {
        protected override string CanonicalName => "Tools.IdeGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("IDE state captured.", state);
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
                throw new CommandErrorException("file_not_found", $"Solution file not found: {solutionPath}");
            }
            string ext = Path.GetExtension(solutionPath);
            if (!string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandErrorException("invalid_file_type", $"File is not a solution file: {solutionPath}");
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
                throw new CommandErrorException("file_not_found", $"devenv.exe not found: {devenvPath}");
            }

            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                string extension = Path.GetExtension(solutionPath);
                if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommandErrorException("invalid_file_type", $"File is not a solution file: {solutionPath}");
                }

                if (!File.Exists(solutionPath))
                {
                    throw new CommandErrorException("file_not_found", $"Solution file not found: {solutionPath}");
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
            if (process is null)
            {
                throw new CommandErrorException("launch_failed", "Visual Studio launch failed: Process.Start returned null.");
            }

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
                throw new CommandErrorException("file_exists", $"Solution file already exists: {solutionPath}");
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
                throw new CommandErrorException("invalid_arguments", "Argument --name must not be empty.");
            }

            if (!string.Equals(Path.GetFileName(trimmedName), trimmedName, StringComparison.Ordinal))
            {
                throw new CommandErrorException("invalid_arguments", "Argument --name must be a solution name, not a path.");
            }

            if (trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new CommandErrorException("invalid_arguments", $"Argument --name contains invalid file name characters: {requestedName}");
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

    internal sealed class IdeBatchCommandsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0225)
    {
        protected override string CanonicalName => "Tools.IdeBatchCommands";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string batchFile = args.GetRequiredString("batch-file");
            if (!File.Exists(batchFile))
            {
                throw new CommandErrorException("file_not_found", $"Batch file not found: {batchFile}");
            }

            string json = File.ReadAllText(batchFile);
            JArray steps;
            try
            {
                steps = JArray.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse batch file: {ex.Message}");
            }

            bool stopOnError = args.GetBoolean("stop-on-error", false);
            return await ExecuteBatchAsync(context, steps, stopOnError).ConfigureAwait(true);
        }
    }
}



