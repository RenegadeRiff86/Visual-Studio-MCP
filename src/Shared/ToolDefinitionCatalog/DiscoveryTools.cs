using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    private const string DiscoveryTag = "discovery";

    public static ToolDefinition ListTools(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tools",
            "system",
            "List all tools.",
            "Return every bridge catalog tool in a compact scan-friendly format with category, safety flags, and call_tool invocation guidance. Names returned here are not necessarily directly exposed MCP tools in lazy mode.",
            parameterSchema,
            tags: [DiscoveryTag, "catalog", "list"]);

    public static ToolDefinition CallTool(JsonObject parameterSchema)
        => CreateMutatingTool(
            "call_tool",
            "system",
            "Call a discovered bridge tool.",
            "Invoke a bridge catalog tool by name after discovering it with recommend_tools, list_tools, or tool_help. Use this wrapper for tools such as read_file, find_text, apply_diff, and git_status when they are not directly exposed in the MCP protocol tools/list response. The target tool may be read-only or mutating, so inspect the target schema before destructive operations.",
            parameterSchema,
            tags: [DiscoveryTag, "dispatch", "lazy"]);

    public static ToolDefinition ListToolCategories(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tool_categories",
            "system",
            "List tool categories.",
            "Return the available tool categories, counts, and highlighted navigation tools.",
            parameterSchema,
            tags: [DiscoveryTag, "categories", "catalog"]);

    public static ToolDefinition ListToolsByCategory(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tools_by_category",
            "system",
            "List tools in one category.",
            "Return bridge catalog tools for one category in the same compact discovery format, including call_tool invocation guidance.",
            parameterSchema,
            tags: [DiscoveryTag, "category", "catalog"]);

    public static ToolDefinition RecommendTools(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "recommend_tools",
            "system",
            "Recommend tools for a task.",
            "Accept a natural-language task and return the best matching tools with short reasons.",
            parameterSchema,
            tags: [DiscoveryTag, "recommendation", "task"]);

    public static ToolDefinition ToolHelp(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "tool_help",
            "system",
            "Get help for tools.",
            "Return tool help. Pass name for one tool, category for a group, or omit both for the category index.",
            parameterSchema,
            aliases: ["help"],
            tags: [DiscoveryTag, "help", "catalog"]);
}
