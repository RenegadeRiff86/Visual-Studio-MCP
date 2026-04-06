using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    internal sealed class IdeBuildSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0212)
    {
        protected override string CanonicalName => "Tools.IdeBuildSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            int timeout = args.GetInt32(TimeoutMillisecondsArgument, DefaultBuildTimeoutMilliseconds);
            await EnsureCleanDiagnosticsAsync(context, args, timeout).ConfigureAwait(true);

            string? project = args.GetString("project");
            JObject buildResult;
            if (!string.IsNullOrWhiteSpace(project))
            {
                await context.Runtime.BridgeApprovalService.RequestApprovalAsync(
                    context,
                    BridgeApprovalKind.Build,
                    subject: $"Build project '{project}'",
                    details: null).ConfigureAwait(true);

                buildResult = await context.Runtime.BuildService.BuildProjectAsync(
                    context,
                    timeout,
                    project!,
                    args.GetString("configuration"),
                    args.GetString("platform")).ConfigureAwait(true);
            }
            else
            {
                await context.Runtime.BridgeApprovalService.RequestApprovalAsync(
                    context,
                    BridgeApprovalKind.Build,
                    subject: "Build solution",
                    details: null).ConfigureAwait(true);

                buildResult = await context.Runtime.BuildService.BuildSolutionAsync(
                    context,
                    timeout,
                    args.GetString("configuration"),
                    args.GetString("platform")).ConfigureAwait(true);
            }

            if (args.GetBoolean(RequireCleanDiagnosticsArgument, true))
            {
                JObject diagnostics = await GetDiagnosticsSnapshotAsync(context, args, timeout, waitForIntellisense: false).ConfigureAwait(true);
                ThrowIfBuildDiagnosticsPresent(diagnostics, args, buildResult, "Build completed but diagnostics remain");
            }

            return new CommandExecutionResult($"Build completed with LastBuildInfo={buildResult["lastBuildInfo"]}.", buildResult);
        }
    }

    internal sealed class IdeRebuildSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0261)
    {
        protected override string CanonicalName => "Tools.IdeRebuildSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            int timeout = args.GetInt32(TimeoutMillisecondsArgument, DefaultBuildTimeoutMilliseconds);
            await EnsureCleanDiagnosticsAsync(context, args, timeout).ConfigureAwait(true);

            await context.Runtime.BridgeApprovalService.RequestApprovalAsync(
                context,
                BridgeApprovalKind.Build,
                subject: "Rebuild solution",
                details: null).ConfigureAwait(true);

            JObject rebuildResult = await context.Runtime.BuildService.RebuildSolutionAsync(
                context,
                timeout,
                args.GetString("configuration"),
                args.GetString("platform")).ConfigureAwait(true);

            if (args.GetBoolean(RequireCleanDiagnosticsArgument, true))
            {
                JObject diagnostics = await GetDiagnosticsSnapshotAsync(context, args, timeout, waitForIntellisense: false).ConfigureAwait(true);
                ThrowIfBuildDiagnosticsPresent(diagnostics, args, rebuildResult, "Rebuild completed but diagnostics remain");
            }

            return new CommandExecutionResult($"Rebuild completed with LastBuildInfo={rebuildResult["lastBuildInfo"]}.", rebuildResult);
        }
    }

    internal sealed class IdeGetErrorListCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0213)
    {
        protected override string CanonicalName => "Tools.IdeGetErrorList";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            bool quick = args.GetBoolean("quick", false);
            JObject errorListResult = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                args.GetBoolean("wait-for-intellisense", !quick),
                args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick)),
                quick,
                CreateErrorListQuery(args)).ConfigureAwait(true);

            return new CommandExecutionResult($"Captured {errorListResult["count"]} Error List row(s).", errorListResult);
        }
    }

    internal sealed class IdeGetWarningsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0230)
    {
        protected override string CanonicalName => "Tools.IdeGetWarnings";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            bool quick = args.GetBoolean("quick", false);
            JObject warningListResult = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                args.GetBoolean("wait-for-intellisense", !quick),
                args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick)),
                quick,
                CreateErrorListQuery(args, "warning")).ConfigureAwait(true);

            return new CommandExecutionResult($"Captured {warningListResult["count"]} warning row(s).", warningListResult);
        }
    }

    internal sealed class IdeGetMessagesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0263)
    {
        protected override string CanonicalName => "Tools.IdeGetMessages";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            bool quick = args.GetBoolean("quick", false);
            JObject messageListResult = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                args.GetBoolean("wait-for-intellisense", !quick),
                args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick)),
                quick,
                CreateErrorListQuery(args, "message")).ConfigureAwait(true);

            return new CommandExecutionResult($"Captured {messageListResult["count"]} message row(s).", messageListResult);
        }
    }

    internal sealed class IdeBuildAndCaptureErrorsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0214)
    {
        protected override string CanonicalName => "Tools.IdeBuildAndCaptureErrors";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            int timeout = GetBuildErrorsTimeout(args);
            await EnsureCleanDiagnosticsAsync(context, args, timeout).ConfigureAwait(true);
            JObject build = await context.Runtime.BuildService.BuildAndCaptureErrorsAsync(
                context,
                timeout,
                args.GetBoolean(WaitForIntellisenseArgument, true)).ConfigureAwait(true);
            JObject errors = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                false,
                timeout,
                query: CreateErrorListQuery(args),
                includeBuildOutputFallback: true).ConfigureAwait(true);

            JObject buildAndErrorsResult = new()
            {
                ["build"] = build,
                ["errors"] = errors,
            };

            if (args.GetBoolean(RequireCleanDiagnosticsArgument, true))
            {
                ThrowIfDiagnosticsPresent(errors, "Build completed but diagnostics remain", args, buildAndErrorsResult);
            }

            return new CommandExecutionResult($"Build finished and captured {errors["count"]} Error List row(s).", buildAndErrorsResult);
        }
    }

    private static void ThrowIfBuildDiagnosticsPresent(JObject diagnostics, CommandArguments args, JObject buildResult, string summaryPrefix)
    {
        JObject buildContext = new() { ["build"] = buildResult };
        ThrowIfDiagnosticsPresent(diagnostics, summaryPrefix, args, buildContext);
    }
}
