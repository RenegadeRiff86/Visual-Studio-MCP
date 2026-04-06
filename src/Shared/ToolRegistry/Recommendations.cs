using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    public JsonObject RecommendTools(string task)
    {
        JsonArray recommendations = [];
        bool includesReadFile = false;
        bool includesApplyDiff = false;
        foreach (var (tool, reason, _) in ScoreTools(task).Take(7))
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
        if (includesReadFile && includesApplyDiff)
        {
            workflowHint = "For in-solution code edits, inspect the target with read_file first, then apply changes with apply_diff.";
        }
        else if (includesApplyDiff)
        {
            workflowHint = "For in-solution code edits, use apply_diff as the default targeted edit tool.";
        }

        return new JsonObject
        {
            ["Summary"] = $"{recommendations.Count} recommendations for '{task}'.",
            ["task"] = task,
            ["count"] = recommendations.Count,
            ["workflowHint"] = workflowHint,
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
