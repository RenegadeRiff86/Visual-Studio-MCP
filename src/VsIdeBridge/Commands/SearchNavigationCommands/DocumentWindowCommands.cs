using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SearchNavigationCommands
{
    internal sealed class IdeOpenDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0204)
    {
        protected override string CanonicalName => "Tools.IdeOpenDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService.OpenDocumentAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1),
                args.GetInt32("column", 1),
                args.GetBoolean("allow-disk-fallback", true)).ConfigureAwait(true);

            return new CommandExecutionResult("Document activated.", commandData);
        }
    }

    internal sealed class IdeListDocumentsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0216)
    {
        protected override string CanonicalName => "Tools.IdeListDocuments";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService.ListOpenDocumentsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {commandData["count"]} open document(s).", commandData);
        }
    }

    internal sealed class IdeActivateDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0217)
    {
        protected override string CanonicalName => "Tools.IdeActivateDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService
                .ActivateOpenDocumentAsync(context.Dte, args.GetRequiredString("query"))
                .ConfigureAwait(true);
            return new CommandExecutionResult("Document tab activated.", commandData);
        }
    }

    internal sealed class IdeCloseDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0218)
    {
        protected override string CanonicalName => "Tools.IdeCloseDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService
                .CloseOpenDocumentsAsync(
                    context.Dte,
                    args.GetString("query"),
                    args.GetBoolean("all", false),
                    args.GetBoolean("save", false))
                .ConfigureAwait(true);
            return new CommandExecutionResult($"Closed {commandData["count"]} document(s).", commandData);
        }
    }

    internal sealed class IdeListOpenTabsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021D)
    {
        protected override string CanonicalName => "Tools.IdeListOpenTabs";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService.ListOpenTabsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {commandData["count"]} open tab(s).", commandData);
        }
    }

    internal sealed class IdeCloseFileCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021E)
    {
        protected override string CanonicalName => "Tools.IdeCloseFile";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService
                .CloseFileAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetString("query"),
                    args.GetBoolean("save", false))
                .ConfigureAwait(true);

            return new CommandExecutionResult($"Closed {commandData["count"]} file(s).", commandData);
        }
    }

    internal sealed class IdeCloseAllExceptCurrentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021F)
    {
        protected override string CanonicalName => "Tools.IdeCloseAllExceptCurrent";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DocumentService
                .CloseAllExceptCurrentAsync(context.Dte, args.GetBoolean("save", false))
                .ConfigureAwait(true);
            return new CommandExecutionResult($"Closed {commandData["count"]} tab(s).", commandData);
        }
    }

    internal sealed class IdeSaveDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0232)
    {
        protected override string CanonicalName => "Tools.IdeSaveDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            bool saveAll = args.GetBoolean("all", false);
            string? filePath = args.GetString("file");
            JObject commandData = await context.Runtime.DocumentService
                .SaveDocumentAsync(context.Dte, filePath, saveAll)
                .ConfigureAwait(true);
            int count = commandData["count"]?.Value<int>() ?? 0;
            return new CommandExecutionResult(saveAll ? $"Saved all {count} document(s)." : $"Saved {count} document(s).", commandData);
        }
    }

    internal sealed class IdeReloadDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x024E)
    {
        protected override string CanonicalName => "Tools.IdeReloadDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string filePath = args.GetRequiredString("file");
            JObject commandData = await context.Runtime.DocumentService
                .ReloadDocumentAsync(filePath)
                .ConfigureAwait(true);
            bool reloaded = commandData["reloaded"]?.Value<bool>() ?? false;
            return new CommandExecutionResult(reloaded ? $"Reloaded {filePath}." : $"Skipped reload for {filePath} (not open).", commandData);
        }
    }

    internal sealed class IdeActivateWindowCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0205)
    {
        protected override string CanonicalName => "Tools.IdeActivateWindow";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.WindowService.ActivateWindowAsync(context.Dte, args.GetRequiredString("window")).ConfigureAwait(true);
            return new CommandExecutionResult("Window activated.", commandData);
        }
    }

    internal sealed class IdeListWindowsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0219)
    {
        protected override string CanonicalName => "Tools.IdeListWindows";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.WindowService.ListWindowsAsync(context.Dte, args.GetString("query")).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {commandData["count"]} window(s).", commandData);
        }
    }

    internal sealed class IdeExecuteVsCommandCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021A)
    {
        protected override string CanonicalName => "Tools.IdeExecuteVsCommand";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.VsCommandService
                .ExecutePositionedCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    args.GetRequiredString("command"),
                    args.GetString("args"),
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false))
                .ConfigureAwait(true);
            return new CommandExecutionResult("Visual Studio command executed.", commandData);
        }
    }
}
