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
            "Find files by name or path fragment across the solution. Faster and VS-aware — use this instead of filesystem find or ls when working in a VS solution. Returns ranked matches with project membership.",
            parameterSchema,
            bridgeCommand: "find-files",
            title: "Solution Explorer File Search",
            aliases: ["solution_explorer_search", "search_solution_explorer", "find_solution_file", "search_files", "find_file_by_name", "find", "ls_files"],
            tags: ["code", NavigationTag, "files", "path", "solution", "explorer"]);

    public static ToolDefinition FindText(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_text",
            "search",
            "Search code text — use instead of Grep.",
            "Full-text search across the solution. Use this instead of Grep or rg — faster and VS-aware. Returns file, line, and match context. Use search_symbols when looking for a named definition. Use find_text_batch when you have multiple patterns to search.",
            parameterSchema,
            bridgeCommand: "find-text",
            title: "Text Search",
            aliases: ["search_text", "text_search", "grep_text", "grep", "rg", "ripgrep"],
            tags: ["code", NavigationTag, "search", "text", "grep"]);

    public static ToolDefinition FindTextBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_text_batch",
            "search",
            "Search multiple patterns at once — use instead of repeated Grep calls.",
            "Search for multiple text patterns in one call. Always prefer over repeated find_text or Grep calls — sends all queries in one round-trip and returns all results. Ideal for mapping usages of several symbols or finding multiple strings before a refactor.",
            parameterSchema,
            bridgeCommand: "find-text-batch",
            title: "Batched Text Search",
            aliases: ["search_text_batch", "text_search_batch", "grep_text_batch", "multi_grep", "grep_batch"],
            tags: ["code", NavigationTag, "search", "text", "batch", "grep"]);

}
