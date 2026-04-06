using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    public JsonObject BuildCategoryList()
    {
        JsonArray categories = [];
        foreach (ToolCategoryDefinition category in _categories)
        {
            int count = _all.Count(tool => string.Equals(tool.Category, category.Name, StringComparison.Ordinal));
            categories.Add(new JsonObject
            {
                ["name"] = category.Name,
                ["summary"] = category.Summary,
                ["description"] = category.Description,
                ["toolCount"] = count,
            });
        }

        JsonArray featuredTools = [];
        foreach (string toolName in _featuredTools)
            featuredTools.Add(toolName);

        return new JsonObject
        {
            ["Summary"] = $"{categories.Count} categories.",
            ["count"] = categories.Count,
            ["categories"] = categories,
            ["featuredTools"] = featuredTools,
        };
    }

    public JsonObject BuildCompactToolsList()
    {
        JsonArray tools = [];
        HashSet<string> emitted = new(StringComparer.Ordinal);

        foreach (string featuredTool in _featuredTools)
        {
            if (TryGet(featuredTool, out ToolDefinition? tool) && emitted.Add(tool.Name))
                tools.Add(tool.BuildCompactDiscoveryEntry());
        }

        foreach (ToolDefinition tool in _all)
        {
            if (emitted.Add(tool.Name))
                tools.Add(tool.BuildCompactDiscoveryEntry());
        }

        return new JsonObject
        {
            ["Summary"] = $"{tools.Count} tools. Use list_tools_by_category for a focused list.",
            ["navigationToolsFirst"] = true,
            ["count"] = tools.Count,
            ["tools"] = tools,
        };
    }

    public JsonObject BuildToolsByCategory(string category)
    {
        ToolCategoryDefinition? categoryDefinition = _categories.FirstOrDefault(
            item => string.Equals(item.Name, category, StringComparison.OrdinalIgnoreCase));
        if (categoryDefinition is null)
        {
            JsonArray validCategories = [.. _categories.Select(item => JsonValue.Create(item.Name))];
            return new JsonObject
            {
                ["error"] = $"Unknown category '{category}'.",
                ["validCategories"] = validCategories,
            };
        }

        JsonArray tools = [];
        foreach (ToolDefinition tool in GetByCategory(category))
            tools.Add(tool.BuildCompactDiscoveryEntry());

        return new JsonObject
        {
            ["Summary"] = $"{tools.Count} tools in category '{categoryDefinition.Name}'.",
            ["category"] = categoryDefinition.Name,
            ["summary"] = categoryDefinition.Summary,
            ["description"] = categoryDefinition.Description,
            ["count"] = tools.Count,
            ["tools"] = tools,
        };
    }
}
