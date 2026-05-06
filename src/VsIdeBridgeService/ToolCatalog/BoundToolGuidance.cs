using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    internal static string BoundSessionHint =>
        "A Visual Studio instance is bound. Prefer bridge MCP tools for this solution; use git_restore instead of shell git checkout -- <path> for file restore, and call list_tools when a needed tool is not visible.";

    internal static JsonObject BuildToolDiscoveryGuidance()
    {
        return new JsonObject
        {
            ["hint"] = "Call list_tools to browse every bridge tool, list_tool_categories for tool groups, list_tools_by_category for focused browsing, or recommend_tools for task-based discovery.",
            ["tools"] = new JsonArray { "list_tools", "list_tool_categories", "list_tools_by_category", "recommend_tools", "tool_help" },
            ["recommendedTools"] = BuildBoundRecommendedTools(),
        };
    }

    internal static JsonArray BuildBoundRecommendedTools()
    {
        return
        [
            RecommendedTool("list_tools", "Browse the full bridge tool surface when the current client only exposed a subset."),
            RecommendedTool("recommend_tools", "Ask the bridge which tools fit the current task."),
            RecommendedTool("bridge_health", "Confirm the bound instance and rediscover bridge guidance."),
            RecommendedTool("vs_state", "Inspect the active solution, document, build state, and debugger state."),
            RecommendedTool("find_files", "Locate files through the bound solution instead of shell directory crawling."),
            RecommendedTool("read_file", "Inspect current editor-backed content before editing."),
            RecommendedTool("apply_diff", "Apply targeted in-solution edits through the live editor."),
            RecommendedTool("errors", "Read current Error List errors without starting a build."),
            RecommendedTool("build_errors", "Build through Visual Studio and return compiler errors."),
            RecommendedTool("git_status", "Review repository state through the bridge before version-control changes."),
            RecommendedTool("git_restore", "Discard file changes through the bridge instead of shell git checkout -- <path>."),
        ];
    }

    internal static void AttachBoundSessionGuidance(JsonObject target)
    {
        target["modelGuidance"] = BoundSessionHint;
        target["recommendedTools"] = BuildBoundRecommendedTools();
        target["toolDiscovery"] = BuildToolDiscoveryGuidance();
    }

    private static JsonObject RecommendedTool(string name, string reason)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["reason"] = reason,
        };
    }
}
