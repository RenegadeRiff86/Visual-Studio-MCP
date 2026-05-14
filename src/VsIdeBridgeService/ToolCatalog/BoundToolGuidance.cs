using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    internal static string BoundSessionHint =>
        "A Visual Studio instance is bound. Prefer bridge MCP tools for this solution; use git_restore instead of shell git checkout -- <path> for file restore, and use recommend_tools or list_tools_by_category when a needed tool is not visible.";

    internal static JsonObject BuildToolDiscoveryGuidance()
    {
        return new JsonObject
        {
            ["hint"] = "Use recommend_tools for task-based discovery, list_tools_by_category to load a focused group, list_tool_categories to enumerate all groups, or call_tool({\"name\":\"list_tools\",...}) as a last resort to see every tool at once.",
            ["tools"] = new JsonArray { "recommend_tools", "list_tool_categories", "list_tools_by_category", "tool_help" },
            ["recommendedTools"] = BuildBoundRecommendedTools(),
        };
    }

    internal static JsonArray BuildBoundRecommendedTools()
    {
        return
        [
            RecommendedTool("recommend_tools", "Ask the bridge which tools fit the current task — narrower and faster than list_tools."),
            RecommendedTool("list_tools_by_category", "Load a focused group of tools (search, git, project, debug, etc.) instead of the full catalog."),
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
