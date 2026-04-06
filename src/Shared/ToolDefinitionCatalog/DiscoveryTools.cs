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
            "Return every tool in a compact scan-friendly format with category and safety flags.",
            parameterSchema,
            tags: [DiscoveryTag, "catalog", "list"]);

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
            "Return the tools for one category in the same compact discovery format.",
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
