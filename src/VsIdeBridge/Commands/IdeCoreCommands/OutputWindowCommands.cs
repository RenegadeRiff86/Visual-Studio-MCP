using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class IdeCoreCommands
{
    internal sealed class IdeReadOutputWindowCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0266)
    {
        protected override string CanonicalName => "Tools.IdeReadOutputWindow";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            int? chunkLines = GetNullableInt32(args, "chunk-lines", "chunk_lines", "tail-lines", "tail_lines");
            int? chunkIndex = GetNullableInt32(args, "chunk-index", "chunk_index");
            int? maxChars = GetNullableInt32(args, "max-chars", "max_chars");
            JObject output = await context.Runtime.OutputWindowService
                .ReadOutputWindowAsync(
                    context.Dte,
                    args.GetString("pane"),
                    chunkLines,
                    chunkIndex,
                    maxChars,
                    args.GetBoolean("include-chunks", args.GetBoolean("include_chunks", defaultValue: false)),
                    args.GetBoolean("activate", defaultValue: false))
                .ConfigureAwait(true);

            string pane = output["pane"]?.ToString() ?? string.Empty;
            int returnedLineCount = output["returnedLineCount"]?.Value<int>() ?? 0;
            int lineCount = output["lineCount"]?.Value<int>() ?? 0;
            int chunkCount = output["chunkCount"]?.Value<int>() ?? 1;
            int selectedChunkIndex = output["selectedChunkIndex"]?.Value<int>() ?? 0;
            return new CommandExecutionResult(
                $"Read chunk {selectedChunkIndex + 1}/{chunkCount} ({returnedLineCount}/{lineCount} line(s)) from Output pane '{pane}'.",
                output);
        }

        private static int? GetNullableInt32(CommandArguments args, params string[] names)
        {
            foreach (string name in names)
            {
                int? value = args.GetNullableInt32(name);
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }
    }
}
