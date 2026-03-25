using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    public JsonObject RecommendTools(string task)
    {
        JsonArray recommendations = new JsonArray();
        foreach ((ToolDefinition Tool, string Reason, int Score) recommendation in ScoreTools(task).Take(7))
        {
            recommendations.Add(new JsonObject
            {
                ["name"] = recommendation.Tool.Name,
                ["reason"] = recommendation.Reason,
                ["category"] = recommendation.Tool.Category,
                ["summary"] = recommendation.Tool.Summary,
            });
        }

        return new JsonObject
        {
            ["task"] = task,
            ["count"] = recommendations.Count,
            ["recommendations"] = recommendations,
        };
    }

    private IEnumerable<(ToolDefinition Tool, string Reason, int Score)> ScoreTools(string task)
    {
        TaskProfile profile = CreateTaskProfile(task);
        List<(ToolDefinition Tool, string Reason, int Score)> scored = [];
        foreach (ToolDefinition tool in _all)
        {
            (int Score, string Reason) toolScore = ScoreTool(tool, profile);
            if (toolScore.Score > 0)
                scored.Add((tool, toolScore.Reason, toolScore.Score));
        }

        return scored
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal);
    }

    private (int Score, string Reason) ScoreTool(ToolDefinition tool, TaskProfile profile)
    {
        int score = 0;
        string reason = string.Empty;

        score += ScoreRecommendedMatches(tool, profile, ref reason);
        score += ScoreCategoryMatches(tool, profile, ref reason);
        score += ScoreKeywordMatches(tool, profile.Tokens, ref reason);
        score += ScoreShellExecPenalty(tool, profile, ref reason);

        if (score <= 0)
            return (0, string.Empty);

        return (score, reason == string.Empty ? "Broadly relevant tool" : reason);
    }

    private int ScoreRecommendedMatches(ToolDefinition tool, TaskProfile profile, ref string reason)
    {
        int score = 0;

        if (profile.LooksLikeNavigationTask && _featuredTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 40;
            reason = ChooseReason(reason, "Featured code navigation tool");
        }

        if (profile.LooksLikeNavigationTask && DefaultRecommendedNavigationToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 45;
            reason = ChooseReason(reason, "Top navigation tool for code understanding");
        }

        if (profile.LooksLikeSolutionExplorerTask && string.Equals(tool.Name, "find_files", StringComparison.OrdinalIgnoreCase))
        {
            score += 70;
            reason = ChooseReason(reason, "Matches Solution Explorer-style file search task");
        }

        if (profile.LooksLikeBuildTask && DefaultRecommendedBuildToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 45;
            reason = ChooseReason(reason, "Primary Visual Studio build tool");
        }

        if (profile.LooksLikeEditTask && DefaultRecommendedEditToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 45;
            reason = ChooseReason(reason, "Primary bridge editing tool");
        }

        if (profile.LooksLikeDiscoveryTask && DefaultRecommendedDiscoveryToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 50;
            reason = ChooseReason(reason, "Primary tool discovery tool");
        }

        return score;
    }

    private static int ScoreCategoryMatches(ToolDefinition tool, TaskProfile profile, ref string reason)
    {
        int score = 0;

        if (profile.LooksLikeNavigationTask && string.Equals(tool.Category, "search", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
            reason = ChooseReason(reason, "Search category matches code-navigation task");
        }

        if (profile.LooksLikeDiagnosticTask && string.Equals(tool.Category, "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            score += 22;
            reason = ChooseReason(reason, "Diagnostics category matches error/build task");
        }

        if (profile.LooksLikeBuildTask && string.Equals(tool.Category, "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
            reason = ChooseReason(reason, "Diagnostics category matches build task");
        }

        if (profile.LooksLikeEditTask && string.Equals(tool.Category, "documents", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reason = ChooseReason(reason, "Documents category matches file-edit task");
        }

        if (profile.LooksLikeEditTask && tool.Mutating)
        {
            score += 10;
            reason = ChooseReason(reason, "Mutating tool matches requested change");
        }

        if (profile.LooksLikeDiscoveryTask && string.Equals(tool.Category, "system", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reason = ChooseReason(reason, "System category matches tool-discovery task");
        }

        return score;
    }

    private static int ScoreKeywordMatches(ToolDefinition tool, string[] tokens, ref string reason)
    {
        int score = 0;

        if (tool.Tags.Count > 0)
        {
            int tagHits = tool.Tags.Count(tag => tokens.Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (tagHits > 0)
            {
                score += tagHits * 12;
                reason = ChooseReason(reason, "Tool tags match task keywords");
            }
        }

        int nameHits = tokens.Count(token => token.Length > 2
            && tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (nameHits > 0)
        {
            score += nameHits * 8;
            reason = ChooseReason(reason, "Tool name matches task keywords");
        }

        int aliasHits = tokens.Count(token => token.Length > 2
            && tool.Aliases.Any(alias => alias.Contains(token, StringComparison.OrdinalIgnoreCase)));
        if (aliasHits > 0)
        {
            score += aliasHits * 10;
            reason = ChooseReason(reason, "Tool aliases match common IDE wording");
        }

        int summaryHits = tokens.Count(token => token.Length > 3
            && tool.Summary.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (summaryHits > 0)
        {
            score += summaryHits * 4;
            reason = ChooseReason(reason, "Summary matches task context");
        }

        int descriptionHits = tokens.Count(token => token.Length > 3
            && tool.Description.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (descriptionHits > 0)
        {
            score += descriptionHits * 3;
            reason = ChooseReason(reason, "Description matches task context");
        }

        return score;
    }

    private static int ScoreShellExecPenalty(ToolDefinition tool, TaskProfile profile, ref string reason)
    {
        if (!string.Equals(tool.Name, "shell_exec", StringComparison.OrdinalIgnoreCase))
            return 0;

        int score = 0;
        if (profile.LooksLikeExternalProcessTask)
        {
            score -= 10;
            reason = ChooseReason(reason, "External process task may require shell execution");
        }
        else
        {
            score -= 80;
        }

        if (profile.LooksLikeBuildTask)
            score -= 40;

        return score;
    }

    private static TaskProfile CreateTaskProfile(string task)
    {
        string normalizedTask = task.Trim().ToLowerInvariant();
        string[] tokens = normalizedTask.Split(
            [' ', '_', '-', '.', ',', ':', ';', '?', '!', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        return new TaskProfile(
            tokens,
            ContainsAny(normalizedTask,
                "find", "search", "symbol", "definition", "reference", "read", "where",
                "inspect", "navigate", "understand", "outline", "trace", "code"),
            ContainsAny(normalizedTask,
                "solution explorer", "filename", "file name", "path fragment", "path", "folder"),
            ContainsAny(normalizedTask,
                "error", "warning", "diagnostic", "build", "broken", "failing"),
            ContainsAny(normalizedTask,
                "change", "edit", "write", "patch", "refactor", "rename", "update",
                "fix", "create", "replace", "overwrite"),
            ContainsAny(normalizedTask,
                "build", "compile", "installer", "package", "publish", "msbuild", "rebuild"),
            ContainsAny(normalizedTask,
                "powershell", "cmd", "command line", "process", "exe", "script", "iscc", "terminal"),
            ContainsAny(normalizedTask,
                "which tool", "what tool", "recommend", "discover", "connect", "instance",
                "bridge", "available tools", "tool list", "list tools", "find tool", "what should",
                "how do i", "getting started", "setup", "mcp"));
    }

    private readonly record struct TaskProfile(
        string[] Tokens,
        bool LooksLikeNavigationTask,
        bool LooksLikeSolutionExplorerTask,
        bool LooksLikeDiagnosticTask,
        bool LooksLikeEditTask,
        bool LooksLikeBuildTask,
        bool LooksLikeExternalProcessTask,
        bool LooksLikeDiscoveryTask);
}
