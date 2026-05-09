using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    public JsonObject BuildToolHelp(string? toolName = null, string? category = null)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            if (TryGet(toolName, out ToolDefinition? tool))
            {
                return new JsonObject
                {
                    ["Summary"] = $"Help for tool '{tool.Name}'.",
                    ["invocationHint"] = tool.Name == "call_tool"
                        ? "call_tool is directly callable and cannot dispatch to itself."
                        : $"In lazy mode, invoke this catalog tool with call_tool: {{ \"name\": \"call_tool\", \"arguments\": {{ \"name\": \"{tool.Name}\", \"arguments\": {{ ... }} }} }}.",
                    ["invocation"] = tool.BuildInvocationEntry(),
                    ["tool"] = tool.BuildToolObject(),
                };
            }

            return new JsonObject
            {
                ["error"] = $"Tool '{toolName}' not found.",
                ["suggestion"] = "Use list_tools, list_tool_categories, or recommend_tools to discover available tools.",
            };
        }

        if (!string.IsNullOrWhiteSpace(category))
            return BuildToolsByCategory(category);

        return BuildCategoryList();
    }
}
