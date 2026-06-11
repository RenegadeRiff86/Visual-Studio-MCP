using System.Text.Json.Nodes;
using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class ToolRecommendationTests
{
    [Fact]
    public void GitRestoreTaskRecommendsBridgeRestoreAndListTools()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "restore a corrupted file with git checkout using the bridge instead of shell");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "git_restore"));
        Assert.Contains(tools, item => HasName(item, "list_tools"));
        Assert.DoesNotContain(tools, item => HasName(item, "shell_exec"));
        Assert.Contains("git_restore", recommendation["workflowHint"]!.GetValue<string>());
    }

    [Fact]
    public void GitRebaseTaskRecommendsBridgeRebaseWorkflow()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "fetch upstream then rebase this branch onto upstream master");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "git_rebase"));
        Assert.Contains(tools, item => HasName(item, "git_compare_refs"));
        Assert.Contains(tools, item => HasName(item, "git_log_range"));
        Assert.Contains("git_rebase", recommendation["workflowHint"]!.GetValue<string>());
    }

    [Fact]
    public void GitCompareTaskRecommendsBridgeRangeTools()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "compare my branch with upstream and show ahead behind commits and changed files");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "git_compare_refs"));
        Assert.Contains(tools, item => HasName(item, "git_log_range"));
        Assert.Contains(tools, item => HasName(item, "git_diff_range"));
        Assert.Contains("git_compare_refs", recommendation["workflowHint"]!.GetValue<string>());
    }

    [Fact]
    public void BuildDiagnosticsTaskPrioritizesBuildAndDiagnosticTools()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "read current diagnostics and rebuild the active solution");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "build_errors"));
        Assert.Contains(tools, item => HasName(item, "rebuild_solution"));
        Assert.Contains(tools, item => HasName(item, "errors"));
        Assert.Contains(tools, item => HasName(item, "warnings"));
        Assert.Contains(tools, item => HasName(item, "messages"));
        Assert.Contains("rebuild_solution", recommendation["workflowHint"]!.GetValue<string>());

        int buildErrorsIndex = IndexOf(tools, "build_errors");
        int readFileIndex = IndexOf(tools, "read_file");
        Assert.True(readFileIndex == -1 || buildErrorsIndex < readFileIndex);
    }

    [Fact]
    public void CommonAgentSearchAndEditTermsResolveToBridgeTools()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "grep search for TypeFormatHelper then edit the file with a small replacement");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "find_text"));
        Assert.Contains(tools, item => HasName(item, "read_file"));
        Assert.Contains(tools, item => HasName(item, "apply_diff"));
    }

    [Fact]
    public void CommonAgentRunTestTermsResolveToShellAndDiagnosticsTools()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "run npm test and inspect failing output logs");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "shell_exec"));
        Assert.Contains(tools, item => HasName(item, "read_output"));
        Assert.Contains(tools, item => HasName(item, "errors"));
    }

    [Fact]
    public void LocalModelProviderPromptsStillSurfaceDiscoveryAndActionTools()
    {
        JsonObject recommendation = ToolCatalog.CreateRegistry().Definitions.RecommendTools(
            "Qwen Gemini Grok DeepSeek local model wants to search, read, and edit code");
        JsonArray tools = Assert.IsType<JsonArray>(recommendation["recommendations"]);

        Assert.Contains(tools, item => HasName(item, "list_tools"));
        Assert.Contains(tools, item => HasName(item, "find_text"));
        Assert.Contains(tools, item => HasName(item, "read_file"));
        Assert.Contains(tools, item => HasName(item, "apply_diff"));
    }

    [Fact]
    public void BoundSessionGuidanceIncludesGitAndTabManagementTools()
    {
        JsonObject target = [];
        ToolCatalog.AttachBoundSessionGuidance(target);
        JsonArray tools = Assert.IsType<JsonArray>(target["recommendedTools"]);
        string guidance = target["modelGuidance"]!.GetValue<string>();

        Assert.Contains(tools, item => HasName(item, "list_tools"));
        Assert.Contains(tools, item => HasName(item, "git_restore"));
        Assert.Contains(tools, item => HasName(item, "list_tabs"));
        Assert.Contains(tools, item => HasName(item, "close_others"));
        Assert.Contains("git_restore", guidance);
        Assert.Contains("more than 7 tabs", guidance);
        Assert.Contains("close_file", guidance);
    }

    private static bool HasName(JsonNode? item, string name)
        => string.Equals(item?["name"]?.GetValue<string>(), name, StringComparison.Ordinal);

    private static int IndexOf(JsonArray tools, string name)
    {
        for (int i = 0; i < tools.Count; i++)
        {
            if (HasName(tools[i], name))
                return i;
        }

        return -1;
    }
}
