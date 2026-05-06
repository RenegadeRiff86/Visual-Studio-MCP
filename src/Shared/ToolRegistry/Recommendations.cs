using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    public JsonObject RecommendTools(string task)
    {
        TaskProfile profile = CreateTaskProfile(task);
        List<(ToolDefinition Tool, string Reason, int Score)> selected = [.. ScoreTools(task).Take(7)];
        if (!selected.Any(static item => string.Equals(item.Tool.Name, "list_tools", StringComparison.Ordinal))
            && TryGet("list_tools", out ToolDefinition? listTools))
        {
            selected.Add((listTools, "Browse the full bridge tool surface when a needed tool is not visible", 0));
        }

        JsonArray recommendations = [];
        bool includesReadFile = false;
        bool includesApplyDiff = false;
        foreach (var (tool, reason, _) in selected)
        {
            includesReadFile |= string.Equals(tool.Name, "read_file", StringComparison.Ordinal);
            includesApplyDiff |= string.Equals(tool.Name, "apply_diff", StringComparison.Ordinal);
            recommendations.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["reason"] = reason,
                ["category"] = tool.Category,
                ["summary"] = tool.Summary,
            });
        }

        string workflowHint = string.Empty;
        if (profile.LooksLikeEditTask)
        {
            if (includesReadFile && includesApplyDiff)
            {
                workflowHint = "For in-solution code edits, inspect the target with read_file first, then apply changes with apply_diff.";
            }
            else if (includesApplyDiff)
            {
                workflowHint = "For in-solution code edits, use apply_diff as the default targeted edit tool.";
            }
        }
        else if (profile.LooksLikePythonTask)
        {
            workflowHint = "For Python work, list interpreters with python_list_envs, then create or select one with python_create_env or python_set_project_env before installing packages.";
        }
        else if (profile.LooksLikeNuGetTask)
        {
            workflowHint = "For package management, use nuget_add_package / nuget_remove_package against a specific project; review query_project_references afterward.";
        }
        else if (profile.LooksLikeGitTask)
        {
            workflowHint = profile.LooksLikeRestoreTask
                ? "For restoring files in a bound solution, review git_status or git_diff_unstaged, then use git_restore with explicit paths instead of shell git checkout -- <path>."
                : "For Git work, review status with git_status / git_diff_unstaged before staging with git_add and committing with git_commit.";
        }
        else if (profile.LooksLikeDebugTask)
        {
            workflowHint = "For debugging, set breakpoints with set_breakpoint, start with debug_start, inspect with debug_locals / debug_stack, then step or continue.";
        }

        return new JsonObject
        {
            ["Summary"] = $"{recommendations.Count} recommendations for '{task}'.",
            ["task"] = task,
            ["count"] = recommendations.Count,
            ["workflowHint"] = workflowHint,
            ["discoveryHint"] = "Call list_tools to browse the complete bridge tool surface when a needed tool is not visible.",
            ["discoveryTools"] = new JsonArray { "list_tools", "list_tool_categories", "list_tools_by_category", "tool_help" },
            ["recommendations"] = recommendations,
        };
    }

    private IEnumerable<(ToolDefinition Tool, string Reason, int Score)> ScoreTools(string task)
    {
        TaskProfile profile = CreateTaskProfile(task);
        List<(ToolDefinition Tool, string Reason, int Score)> scored = [];
        foreach (ToolDefinition tool in _all)
        {
            var (score, reason) = ScoreTool(tool, profile);
            if (score > 0)
                scored.Add((tool, reason, score));
        }

        return scored
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal);
    }
}
