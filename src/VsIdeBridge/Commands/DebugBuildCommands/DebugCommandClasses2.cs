using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    internal sealed class IdeDebugThreadsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0233)
    {
        protected override string CanonicalName => "Tools.IdeDebugThreads";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.GetThreadsAsync(context.Dte).ConfigureAwait(true);
            return CreateCapturedResult("debugger thread(s)", commandData);
        }
    }

    internal sealed class IdeDebugStackCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0234)
    {
        protected override string CanonicalName => "Tools.IdeDebugStack";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.GetStackAsync(
                context.Dte,
                args.GetNullableInt32("thread-id"),
                args.GetInt32("max-frames", 100)).ConfigureAwait(true);
            return CreateCapturedResult("stack frame(s)", commandData);
        }
    }

    internal sealed class IdeDebugLocalsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0235)
    {
        protected override string CanonicalName => "Tools.IdeDebugLocals";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.GetLocalsAsync(
                context.Dte,
                args.GetInt32("max", 200)).ConfigureAwait(true);
            return CreateCapturedResult("local variable(s)", commandData);
        }
    }

    internal sealed class IdeDebugModulesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0236)
    {
        protected override string CanonicalName => "Tools.IdeDebugModules";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.GetModulesAsync(context.Dte).ConfigureAwait(true);
            return CreateCapturedResult("process(es) in the module snapshot", commandData);
        }
    }

    internal sealed class IdeDebugWatchCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0237)
    {
        protected override string CanonicalName => "Tools.IdeDebugWatch";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.EvaluateWatchAsync(
                context.Dte,
                args.GetRequiredString("expression"),
                args.GetInt32("timeout-ms", 1000)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger watch expression evaluated.", commandData);
        }
    }

    internal sealed class IdeDebugExceptionsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0238)
    {
        protected override string CanonicalName => "Tools.IdeDebugExceptions";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject exceptionSettings = await context.Runtime.DebuggerService.GetExceptionsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger exception settings snapshot captured.", exceptionSettings);
        }
    }
}
