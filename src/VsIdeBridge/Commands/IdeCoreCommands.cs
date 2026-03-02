using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class IdeCoreCommands
{
    private static Task<CommandExecutionResult> GetHelpResultAsync()
    {
        var commands = new JArray(
            "Tools.IdeGetState",
            "Tools.IdeWaitForReady",
            "Tools.IdeFindText",
            "Tools.IdeFindFiles",
            "Tools.IdeOpenDocument",
            "Tools.IdeListDocuments",
            "Tools.IdeListOpenTabs",
            "Tools.IdeActivateDocument",
            "Tools.IdeCloseDocument",
            "Tools.IdeCloseFile",
            "Tools.IdeCloseAllExceptCurrent",
            "Tools.IdeActivateWindow",
            "Tools.IdeListWindows",
            "Tools.IdeExecuteVsCommand",
            "Tools.IdeFindAllReferences",
            "Tools.IdeShowCallHierarchy",
            "Tools.IdeGetDocumentSlice",
            "Tools.IdeGetSmartContextForQuery",
            "Tools.IdeApplyUnifiedDiff",
            "Tools.IdeSetBreakpoint",
            "Tools.IdeListBreakpoints",
            "Tools.IdeRemoveBreakpoint",
            "Tools.IdeClearAllBreakpoints",
            "Tools.IdeDebugGetState",
            "Tools.IdeDebugStart",
            "Tools.IdeDebugStop",
            "Tools.IdeDebugBreak",
            "Tools.IdeDebugContinue",
            "Tools.IdeDebugStepOver",
            "Tools.IdeDebugStepInto",
            "Tools.IdeDebugStepOut",
            "Tools.IdeBuildSolution",
            "Tools.IdeGetErrorList",
            "Tools.IdeBuildAndCaptureErrors",
            "Tools.IdeOpenSolution",
            "Tools.IdeGoToDefinition",
            "Tools.IdeGetFileOutline",
            "Tools.IdeBatchCommands");

        return Task.FromResult(new CommandExecutionResult(
            "Command catalog written.",
            new JObject
            {
                ["commands"] = commands,
                ["example"] = @"Tools.IdeGetState --out ""C:\temp\ide-state.json""",
                ["documentSliceExample"] = @"Tools.IdeGetDocumentSlice --file ""C:\repo\src\foo.cpp"" --start-line 120 --end-line 180 --out ""C:\temp\slice.json""",
                ["smartContextExample"] = @"Tools.IdeGetSmartContextForQuery --query ""where is GUI_App::OnInit used"" --max-contexts 3 --out ""C:\temp\smart-context.json""",
                ["referencesExample"] = @"Tools.IdeFindAllReferences --file ""C:\repo\src\foo.cpp"" --line 42 --column 13 --out ""C:\temp\references.json""",
                ["callHierarchyExample"] = @"Tools.IdeShowCallHierarchy --file ""C:\repo\src\foo.cpp"" --line 42 --column 13 --out ""C:\temp\call-hierarchy.json""",
                ["applyDiffExample"] = @"Tools.IdeApplyUnifiedDiff --patch-file ""C:\temp\change.diff"" --out ""C:\temp\apply-diff.json""",
                ["openSolutionExample"] = @"Tools.IdeOpenSolution --solution ""C:\path\to\solution.sln"" --out ""C:\temp\open-solution.json"""
            }));
    }

    private static async Task<CommandExecutionResult> GetSmokeTestResultAsync(IdeCommandContext context)
    {
        var state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
        return new CommandExecutionResult(
            "Smoke test captured IDE state.",
            new JObject
            {
                ["success"] = true,
                ["state"] = state,
            });
    }

    internal sealed class IdeHelpMenuCommand : IdeCommandBase
    {
        public IdeHelpMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0102, acceptsParameters: false)
        {
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeHelpMenu";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return GetHelpResultAsync();
        }
    }

    internal sealed class IdeSmokeTestMenuCommand : IdeCommandBase
    {
        public IdeSmokeTestMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0103, acceptsParameters: false)
        {
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeSmokeTestMenu";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return GetSmokeTestResultAsync(context);
        }
    }

    internal sealed class IdeHelpCommand : IdeCommandBase
    {
        public IdeHelpCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0100)
        {
        }

        protected override string CanonicalName => "Tools.IdeHelp";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return GetHelpResultAsync();
        }
    }

    internal sealed class IdeSmokeTestCommand : IdeCommandBase
    {
        public IdeSmokeTestCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0101)
        {
        }

        protected override string CanonicalName => "Tools.IdeSmokeTest";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return await GetSmokeTestResultAsync(context).ConfigureAwait(true);
        }
    }

    internal sealed class IdeGetStateCommand : IdeCommandBase
    {
        public IdeGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0200)
        {
        }

        protected override string CanonicalName => "Tools.IdeGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("IDE state captured.", state);
        }
    }

    internal sealed class IdeWaitForReadyCommand : IdeCommandBase
    {
        public IdeWaitForReadyCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0201)
        {
        }

        protected override string CanonicalName => "Tools.IdeWaitForReady";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var timeout = args.GetInt32("timeout-ms", 120000);
            var data = await context.Runtime.ReadinessService.WaitForReadyAsync(context, timeout).ConfigureAwait(true);
            return new CommandExecutionResult("Readiness wait completed.", data);
        }
    }

    internal sealed class IdeOpenSolutionCommand : IdeCommandBase
    {
        public IdeOpenSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0224)
        {
        }

        protected override string CanonicalName => "Tools.IdeOpenSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var solutionPath = args.GetRequiredString("solution");
            if (!File.Exists(solutionPath))
            {
                throw new CommandErrorException("file_not_found", $"Solution file not found: {solutionPath}");
            }
            if (!string.Equals(Path.GetExtension(solutionPath), ".sln", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandErrorException("invalid_file_type", $"File is not a solution file: {solutionPath}");
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            context.Dte.Solution.Open(solutionPath);
            return new CommandExecutionResult("Solution opened.", new JObject { ["solutionPath"] = solutionPath });
        }
    }

    internal sealed class IdeBatchCommandsCommand : IdeCommandBase
    {
        public IdeBatchCommandsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0225)
        {
        }

        protected override string CanonicalName => "Tools.IdeBatchCommands";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var batchFile = args.GetRequiredString("batch-file");
            if (!File.Exists(batchFile))
            {
                throw new CommandErrorException("file_not_found", $"Batch file not found: {batchFile}");
            }

            var json = File.ReadAllText(batchFile);
            JArray steps;
            try
            {
                steps = JArray.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse batch file: {ex.Message}");
            }

            var stopOnError = args.GetBoolean("stop-on-error", false);
            var results = new JArray();
            var successCount = 0;
            var failureCount = 0;
            var stoppedEarly = false;

            for (var i = 0; i < steps.Count; i++)
            {
                var step = (JObject)steps[i];
                var commandName = (string?)step["command"] ?? string.Empty;
                var commandArgs = (string?)step["args"] ?? string.Empty;

                JObject stepResult;
                if (!context.Runtime.TryGetCommand(commandName, out var cmd))
                {
                    failureCount++;
                    stepResult = new JObject
                    {
                        ["index"] = i,
                        ["command"] = commandName,
                        ["success"] = false,
                        ["summary"] = $"Unknown command: {commandName}",
                        ["data"] = new JObject(),
                        ["error"] = new JObject { ["code"] = "unknown_command", ["message"] = $"Command not registered: {commandName}" },
                    };
                }
                else
                {
                    var parsedArgs = CommandArgumentParser.Parse(commandArgs);
                    try
                    {
                        var result = await cmd.ExecuteDirectAsync(context, parsedArgs).ConfigureAwait(true);
                        successCount++;
                        stepResult = new JObject
                        {
                            ["index"] = i,
                            ["command"] = commandName,
                            ["success"] = true,
                            ["summary"] = result.Summary,
                            ["data"] = result.Data,
                            ["error"] = JValue.CreateNull(),
                        };
                    }
                    catch (CommandErrorException ex)
                    {
                        failureCount++;
                        stepResult = new JObject
                        {
                            ["index"] = i,
                            ["command"] = commandName,
                            ["success"] = false,
                            ["summary"] = ex.Message,
                            ["data"] = new JObject(),
                            ["error"] = new JObject { ["code"] = ex.Code, ["message"] = ex.Message },
                        };
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        stepResult = new JObject
                        {
                            ["index"] = i,
                            ["command"] = commandName,
                            ["success"] = false,
                            ["summary"] = ex.Message,
                            ["data"] = new JObject(),
                            ["error"] = new JObject { ["code"] = "internal_error", ["message"] = ex.Message },
                        };
                    }
                }

                results.Add(stepResult);

                if (stopOnError && failureCount > 0)
                {
                    stoppedEarly = i < steps.Count - 1;
                    break;
                }
            }

            var data = new JObject
            {
                ["batchCount"] = steps.Count,
                ["successCount"] = successCount,
                ["failureCount"] = failureCount,
                ["stoppedEarly"] = stoppedEarly,
                ["results"] = results,
            };

            return new CommandExecutionResult(
                $"Batch: {successCount}/{steps.Count} succeeded, {failureCount} failed.",
                data);
        }
    }
}
