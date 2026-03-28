using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string MatchCase = "match_case";

    private static IEnumerable<ToolEntry> SearchTools()
        =>
        FileReadTools()
            .Concat(TextSearchTools())
            .Concat(CodeExplorationTools());

    private static IEnumerable<ToolEntry> FileReadTools()
    {
        yield return BridgeTool(ToolDefinitionCatalog.ReadFile(
            ObjectSchema(
                Req("file", FileDesc),
                OptInt("start_line", "First 1-based line to read. Use with end_line."),
                OptInt("end_line", "Last 1-based line to read (inclusive). Use with start_line."),
                OptInt("line", "Anchor 1-based line. Use with context_before/context_after."),
                OptInt("context_before", "Lines before anchor (default 10)."),
                OptInt("context_after", "Lines after anchor (default 30)."),
                OptBool("reveal_in_editor", "Reveal slice in editor (default true)."))),
            "document-slice",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("start-line", OptionalText(a, "start_line")),
                ("end-line", OptionalText(a, "end_line")),
                (Line, OptionalText(a, Line)),
                ("context-before", OptionalText(a, "context_before")),
                ("context-after", OptionalText(a, "context_after")),
                BoolArg("reveal-in-editor", a, "reveal_in_editor", true, true)));

        yield return BridgeTool(ToolDefinitionCatalog.ReadFileBatch(
            ObjectSchema(
                (("ranges",
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ranges to read in order.",
                        ["items"] = ObjectSchema(
                            Req("file", FileDesc),
                            OptInt("start_line", "First 1-based line."),
                            OptInt("end_line", "Last 1-based line."),
                            OptInt("line", "Anchor line."),
                            OptInt("context_before", "Lines before anchor."),
                            OptInt("context_after", "Lines after anchor.")),
                        ["minItems"] = 1,
                    },
                    true)))),
            "document-slices",
            a => Build(("ranges", a?["ranges"]?.ToJsonString())));
    }

    private static IEnumerable<ToolEntry> TextSearchTools()
    {
        yield return BridgeTool(ToolDefinitionCatalog.FindFiles(
            ObjectSchema(
                Req(Query, "File name or path fragment. Use instead of Glob/find/ls when you know the filename or a partial path. For glob patterns like **/*.cs use the glob tool instead."),
                Opt(Path, "Optional path fragment filter."),
                OptArr("extensions", "Optional extension filters like ['.cmake','.txt']."),
                OptBool("include_non_project", "Include disk files under solution root that are not in projects (default true)."),
                OptInt("max_results", "Optional max result count (default 200)."),
                Opt(Scope, "Optional scope: solution (default), project, document, or open."))),
            "find-files",
            a => Build(
                (Query, OptionalString(a, Query)),
                (Path, OptionalString(a, Path)),
                ("extensions", a?["extensions"]?.ToJsonString()),
                BoolArg("include-non-project", a, "include_non_project", true, true),
                ("max-results", OptionalText(a, "max_results")),
                (Scope, OptionalString(a, Scope))));

        yield return BridgeTool(ToolDefinitionCatalog.FindText(
            ObjectSchema(
                Req(Query, "Search text or regex pattern."),
                Opt(Path, "Optional path or directory filter."),
                Opt(Scope, "Scope: solution (default), project, or document."),
                Opt(Project, ProjectFilterDesc),
                OptBool(MatchCase, "Case-sensitive match (default false)."),
                OptBool("whole_word", "Match whole word only (default false)."),
                OptBool("regex", "Treat query as a regular expression (default false)."))),
            "find-text",
            a => Build(
                (Query, OptionalString(a, Query)),
                (Path, OptionalString(a, Path)),
                (Scope, OptionalString(a, Scope)),
                (Project, OptionalString(a, Project)),
                BoolArg("match-case", a, MatchCase, false, true),
                BoolArg("whole-word", a, "whole_word", false, true),
                BoolArg("regex", a, "regex", false, true)));

        yield return BridgeTool(ToolDefinitionCatalog.FindTextBatch(
            ObjectSchema(
                (("queries",
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Queries to search for in order.",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["minItems"] = 1,
                    },
                    true)),
                Opt(Scope, "Optional scope: solution, project, document, or open."),
                Opt(Project, ProjectFilterDesc),
                Opt(Path, "Optional path filter."),
                OptInt("results_window", "Optional Find Results window number."),
                OptInt("max_queries_per_chunk", "Optional max query count per chunk (default 5)."),
                OptBool(MatchCase, "Case-sensitive match (default false)."),
                OptBool("whole_word", "Match whole word only (default false)."),
                OptBool("regex", "Treat queries as regular expressions (default false)."))),
            "find-text-batch",
            a => Build(
                ("queries", a?["queries"]?.ToJsonString()),
                (Scope, OptionalString(a, Scope)),
                (Project, OptionalString(a, Project)),
                (Path, OptionalString(a, Path)),
                ("results-window", OptionalText(a, "results_window")),
                ("max-queries-per-chunk", OptionalText(a, "max_queries_per_chunk")),
                BoolArg("match-case", a, MatchCase, false, true),
                BoolArg("whole-word", a, "whole_word", false, true),
                BoolArg("regex", a, "regex", false, true)));

        yield return BridgeTool(ToolDefinitionCatalog.SearchSymbols(
            ObjectSchema(
                Req(Query, "Symbol search text."),
                Opt("kind", "Optional symbol kind filter."),
                Opt(Scope, "Optional scope: solution, project, document, or open."),
                Opt(Project, ProjectFilterDesc),
                Opt(Path, "Optional path or directory filter."),
                OptInt(Max, "Optional max result count."),
                OptBool(MatchCase, "Case-sensitive match (default false)."))),
            "search-symbols",
            a => Build(
                (Query, OptionalString(a, Query)),
                ("kind", OptionalString(a, "kind")),
                (Scope, OptionalString(a, Scope)),
                (Project, OptionalString(a, Project)),
                (Path, OptionalString(a, Path)),
                (Max, OptionalText(a, Max)),
                BoolArg("match-case", a, MatchCase, false, true)));
    }

    private static IEnumerable<ToolEntry> CodeExplorationTools()
    {
        yield return BridgeTool("search_solutions",
            "Search for solution files (.sln/.slnx) on disk under a given root directory. " +
            "Defaults to %USERPROFILE%\\source\\repos.",
            ObjectSchema(
                Opt(Path, "Root directory to search."),
                Opt(Query, "Filter by solution name (case-insensitive substring)."),
                OptInt("max_depth", "Max directory depth to recurse (default 6)."),
                OptInt(Max, "Max results to return (default 200).")),
            "search-solutions",
            a => Build(
                (Path, OptionalString(a, Path)),
                (Query, OptionalString(a, Query)),
                ("max-depth", OptionalText(a, "max_depth")),
                (Max, OptionalText(a, Max))),
            Search);

        yield return BridgeTool("smart_context",
            "First call for open-ended code exploration. " +
            "Collects focused context for a natural-language query — searches symbols, usages, and related definitions. " +
            "Prefer over read_file + find_text when you don't know exactly where to look. It is more expensive than direct read/search tools.",
            ObjectSchema(
                Req(Query, "Natural-language description of what you are looking for."),
                OptInt("max_contexts", "Max context blocks to return (default 3).")),
            "smart-context",
            a => Build(
                (Query, OptionalString(a, Query)),
                ("max-contexts", OptionalText(a, "max_contexts"))),
            Search);

        yield return BridgeTool("file_symbols",
            "List symbols in one file with optional kind filtering. " +
            "More targeted than file_outline — use when you want only functions, " +
            "only classes, etc.",
            ObjectSchema(
                Req("file", FileDesc),
                Opt("kind", "Optional symbol kind filter (e.g. function, class, member).")),
            "file-symbols",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("kind", OptionalString(a, "kind"))),
            Search);

        yield return new("glob",
            "Find files by glob pattern — use instead of Glob. " +
            "Supports **/*.cs, src/**/*.tsx, *.sln and any pattern supported by " +
            "Microsoft.Extensions.FileSystemGlobbing. Root defaults to solution directory.",
            ObjectSchema(
                Req("pattern", "Glob pattern, e.g. \"**/*.cs\", \"src/**/*.tsx\", \"*.sln\"."),
                Opt(Path, "Root directory. Defaults to solution directory."),
                OptInt(Max, "Max results (default 200).")),
            Search,
            (id, args, bridge) => SystemTools.GlobTool.ExecuteAsync(id, args, bridge),
            aliases: ["find_by_pattern", "glob_files", "list_files"],
            summary: "Find files by glob pattern — use instead of Glob.",
            readOnly: true,
            mutating: false,
            destructive: false);
    }
}
