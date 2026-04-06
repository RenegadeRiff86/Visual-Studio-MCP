using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SearchNavigationCommands
{
    internal sealed class IdeFindTextCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0202)
    {
        protected override string CanonicalName => "Tools.IdeFindText";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string query = args.GetRequiredString("query");
            string scope = args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope, OpenScope);
            string? project = args.GetString("project");
            JObject commandData = await context.Runtime.SearchService.FindTextAsync(
                context,
                query,
                scope,
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetInt32("results-window", 1),
                project,
                args.GetString("path")).ConfigureAwait(true);

            return CreateFoundResult("match(es)", commandData);
        }
    }

    internal sealed class IdeFindTextBatchCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x024A)
    {
        protected override string CanonicalName => "Tools.IdeFindTextBatch";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string queriesJson = args.GetRequiredString("queries");
            JArray queriesArray;
            try
            {
                queriesArray = JArray.Parse(queriesJson);
            }
            catch (JsonException ex)
            {
                throw new CommandErrorException(InvalidJsonErrorCode, $"Failed to parse --queries JSON: {ex.Message}");
            }

            string[] queries =
            [.. queriesArray
                .Values<string>()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())];
            if (queries.Length == 0)
            {
                throw new CommandErrorException("invalid_arguments", "Missing required argument --queries with at least one query string.");
            }

            string scope = args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope, OpenScope);
            string? project = args.GetString("project");
            JObject commandData = await context.Runtime.SearchService.FindTextBatchAsync(
                context,
                queries,
                scope,
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetInt32("results-window", 1),
                project,
                args.GetString("path"),
                args.GetInt32("max-queries-per-chunk", 5)).ConfigureAwait(true);

            return CreateFoundResult("match(es)", commandData, "Data.queryResults and Data.matches");
        }
    }

    internal sealed class IdeFindFilesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0203)
    {
        protected override string CanonicalName => "Tools.IdeFindFiles";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string query = args.GetRequiredString("query");
            string rawExtensions = args.GetString("extensions", string.Empty) ?? string.Empty;
            string[] extensions =
            [.. rawExtensions
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            JObject commandData = await context.Runtime.SearchService.FindFilesAsync(
                context,
                query,
                args.GetString("path"),
                extensions,
                args.GetInt32("max-results", 200),
                args.GetBoolean("include-non-project", true)).ConfigureAwait(true);
            return CreateFoundResult("file(s)", commandData);
        }
    }
}
