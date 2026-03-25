using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition ListTools(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tools",
            "system",
            "List all tools.",
            "Return every tool in a compact scan-friendly format with category and safety flags.",
            parameterSchema,
            tags: ["discovery", "catalog", "list"]);

    public static ToolDefinition ListToolCategories(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tool_categories",
            "system",
            "List tool categories.",
            "Return the available tool categories, counts, and highlighted navigation tools.",
            parameterSchema,
            tags: ["discovery", "categories", "catalog"]);

    public static ToolDefinition ListToolsByCategory(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tools_by_category",
            "system",
            "List tools in one category.",
            "Return the tools for one category in the same compact discovery format.",
            parameterSchema,
            tags: ["discovery", "category", "catalog"]);

    public static ToolDefinition RecommendTools(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "recommend_tools",
            "system",
            "Recommend tools for a task.",
            "Accept a natural-language task and return the best matching tools with short reasons.",
            parameterSchema,
            tags: ["discovery", "recommendation", "task"]);
}
