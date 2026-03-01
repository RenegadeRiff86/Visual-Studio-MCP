using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class SearchNavigationCommands
{
    internal sealed class IdeFindTextCommand : IdeCommandBase
    {
        public IdeFindTextCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0202)
        {
        }

        protected override string CanonicalName => "Tools.IdeFindText";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("query");
            var scope = args.GetEnum("scope", "solution", "solution", "project", "document");
            var project = args.GetString("project");
            var data = await context.Runtime.SearchService.FindTextAsync(
                context,
                query,
                scope,
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetInt32("results-window", 1),
                project).ConfigureAwait(true);

            return new CommandExecutionResult($"Found {data["count"]} match(es).", data);
        }
    }

    internal sealed class IdeFindFilesCommand : IdeCommandBase
    {
        public IdeFindFilesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0203)
        {
        }

        protected override string CanonicalName => "Tools.IdeFindFiles";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("query");
            var data = await context.Runtime.SearchService.FindFilesAsync(context, query).ConfigureAwait(true);
            return new CommandExecutionResult($"Found {data["count"]} file(s).", data);
        }
    }

    internal sealed class IdeOpenDocumentCommand : IdeCommandBase
    {
        public IdeOpenDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0204)
        {
        }

        protected override string CanonicalName => "Tools.IdeOpenDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DocumentService.OpenDocumentAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1),
                args.GetInt32("column", 1)).ConfigureAwait(true);

            return new CommandExecutionResult("Document activated.", data);
        }
    }

    internal sealed class IdeListDocumentsCommand : IdeCommandBase
    {
        public IdeListDocumentsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0216)
        {
        }

        protected override string CanonicalName => "Tools.IdeListDocuments";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DocumentService.ListOpenDocumentsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {data["count"]} open document(s).", data);
        }
    }

    internal sealed class IdeActivateDocumentCommand : IdeCommandBase
    {
        public IdeActivateDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0217)
        {
        }

        protected override string CanonicalName => "Tools.IdeActivateDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DocumentService
                .ActivateOpenDocumentAsync(context.Dte, args.GetRequiredString("query"))
                .ConfigureAwait(true);
            return new CommandExecutionResult("Document tab activated.", data);
        }
    }

    internal sealed class IdeCloseDocumentCommand : IdeCommandBase
    {
        public IdeCloseDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0218)
        {
        }

        protected override string CanonicalName => "Tools.IdeCloseDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DocumentService
                .CloseOpenDocumentsAsync(
                    context.Dte,
                    args.GetString("query"),
                    args.GetBoolean("all", false),
                    args.GetBoolean("save", false))
                .ConfigureAwait(true);
            return new CommandExecutionResult($"Closed {data["count"]} document(s).", data);
        }
    }

    internal sealed class IdeActivateWindowCommand : IdeCommandBase
    {
        public IdeActivateWindowCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0205)
        {
        }

        protected override string CanonicalName => "Tools.IdeActivateWindow";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.WindowService.ActivateWindowAsync(context.Dte, args.GetRequiredString("window")).ConfigureAwait(true);
            return new CommandExecutionResult("Window activated.", data);
        }
    }

    internal sealed class IdeListWindowsCommand : IdeCommandBase
    {
        public IdeListWindowsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0219)
        {
        }

        protected override string CanonicalName => "Tools.IdeListWindows";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.WindowService.ListWindowsAsync(context.Dte, args.GetString("query")).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {data["count"]} window(s).", data);
        }
    }

    internal sealed class IdeExecuteVsCommandCommand : IdeCommandBase
    {
        public IdeExecuteVsCommandCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x021A)
        {
        }

        protected override string CanonicalName => "Tools.IdeExecuteVsCommand";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.VsCommandService
                .ExecuteCommandAsync(context.Dte, args.GetRequiredString("command"), args.GetString("args"))
                .ConfigureAwait(true);
            return new CommandExecutionResult("Visual Studio command executed.", data);
        }
    }

    internal sealed class IdeFindAllReferencesCommand : IdeCommandBase
    {
        private static readonly string[] CandidateCommands = { "Edit.FindAllReferences" };

        public IdeFindAllReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x021B)
        {
        }

        protected override string CanonicalName => "Tools.IdeFindAllReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    args.GetString("file"),
                    args.GetString("document"),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false),
                    "references",
                    args.GetBoolean("activate-window", true),
                    args.GetInt32("timeout-ms", 5000))
                .ConfigureAwait(true);

            return new CommandExecutionResult("Find All References executed.", data);
        }
    }

    internal sealed class IdeShowCallHierarchyCommand : IdeCommandBase
    {
        private static readonly string[] CandidateCommands = { "View.CallHierarchy" };

        public IdeShowCallHierarchyCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x021C)
        {
        }

        protected override string CanonicalName => "Tools.IdeShowCallHierarchy";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    args.GetString("file"),
                    args.GetString("document"),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false),
                    "Call Hierarchy",
                    args.GetBoolean("activate-window", true),
                    args.GetInt32("timeout-ms", 5000))
                .ConfigureAwait(true);

            return new CommandExecutionResult("Call Hierarchy executed.", data);
        }
    }

    internal sealed class IdeGetDocumentSliceCommand : IdeCommandBase
    {
        public IdeGetDocumentSliceCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0215)
        {
        }

        protected override string CanonicalName => "Tools.IdeGetDocumentSlice";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var baseLine = args.GetInt32("line", 1);
            var contextBefore = args.GetInt32("context-before", 0);
            var contextAfter = args.GetInt32("context-after", 0);
            var startLine = args.GetInt32("start-line", Math.Max(1, baseLine - contextBefore));
            var endLine = args.GetInt32("end-line", Math.Max(startLine, baseLine + contextAfter));

            var data = await context.Runtime.DocumentService.GetDocumentSliceAsync(
                context.Dte,
                args.GetString("file"),
                startLine,
                endLine,
                args.GetBoolean("include-line-numbers", true)).ConfigureAwait(true);

            return new CommandExecutionResult(
                $"Captured lines {data["actualStartLine"]}-{data["actualEndLine"]}.",
                data);
        }
    }
}
