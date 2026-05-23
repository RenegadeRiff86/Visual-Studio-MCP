using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    private const string NavigationTag = "navigation";

    public static ToolDefinition FindFiles(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_files",
            "search",
            "Find files by name — use instead of find/ls.",
            "Find files by name or path fragment across the solution. Faster and VS-aware — use this instead of filesystem find or ls " +
            "when working in a VS solution. This is the quickest way to create an f:N file handle. Returns ranked matches with " +
            "project membership. Each match includes a \"handle\" field — pass it directly as the file argument to read_file, " +
            "read_file_batch, file_outline, open_file, apply_diff, or write_file instead of copying the full path.",
            parameterSchema,
            bridgeCommand: "find-files",
            title: "Solution Explorer File Search",
            aliases: ["ls", "list_directory", "find_files_by_name", "solution_explorer_search", "search_solution_explorer", "find_solution_file", "search_files", "find_file_by_name", "find", "ls_files"],
            tags: ["code", NavigationTag, "files", "path", "solution", "explorer"]);

    public static ToolDefinition FindText(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_text",
            "search",
            "Search code text — use instead of Grep; see related search tools in the description.",
            "Full-text search across the solution. Use this instead of Grep or rg for literal or regex text — faster and VS-aware. " +
            "Before choosing this tool, consider the rest of the search family: find_files or glob for filenames, search_symbols for " +
            "named types/members, file_outline or file_symbols for members in a known file, find_text_batch for multiple patterns, " +
            "smart_context for open-ended exploration, and goto_definition, peek_definition, find_references, count_references, or " +
            "call_hierarchy for symbol navigation. Call list_tools_by_category with category=search to see the full set. This is a " +
            "direct way to create an h:N handle from a text match. Returns file, line, and match context. Each match includes a " +
            "\"handle\" field — pass it directly as the file argument to read_file, read_file_batch, file_outline, apply_diff, " +
            "write_file, or any other tool that accepts a file argument.",
            parameterSchema,
            bridgeCommand: "find-text",
            title: "Text Search",
            aliases: ["search", "search_code", "search_file_content", "grep_search", "search_text", "text_search", "grep_text", "grep", "rg", "ripgrep"],
            tags: ["code", NavigationTag, "search", "text", "grep"]);

    public static ToolDefinition FindTextBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_text_batch",
            "search",
            "Search multiple patterns at once — use instead of repeated Grep calls.",
            "Search for multiple text patterns in one call. Always prefer over repeated find_text or Grep calls — sends all queries " +
            "in one round-trip and returns all results. Ideal for mapping usages of several symbols or finding multiple strings " +
            "before a refactor. This creates h:N handles for matched rows. Each match includes a \"handle\" field — pass it " +
            "directly as the file argument to read_file, file_outline, apply_diff, or write_file instead of copying the full path. " +
            "When the bridge is busy, keep max_queries_per_chunk at 5 or lower rather than running parallel searches.",
            parameterSchema,
            bridgeCommand: "find-text-batch",
            title: "Batched Text Search",
            aliases: ["search_many", "search_batch", "search_text_batch", "text_search_batch", "grep_text_batch", "multi_grep", "grep_batch"],
            tags: ["code", NavigationTag, "search", "text", "batch", "grep"]);

}
