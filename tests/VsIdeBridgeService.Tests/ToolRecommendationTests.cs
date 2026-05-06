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
    public void BoundSessionGuidanceIncludesDiscoveryAndGitRestoreTools()
    {
        JsonObject target = [];
        ToolCatalog.AttachBoundSessionGuidance(target);
        JsonArray tools = Assert.IsType<JsonArray>(target["recommendedTools"]);

        Assert.Contains(tools, item => HasName(item, "list_tools"));
        Assert.Contains(tools, item => HasName(item, "git_restore"));
        Assert.Contains("git_restore", target["modelGuidance"]!.GetValue<string>());
    }

    private static bool HasName(JsonNode? item, string name)
        => string.Equals(item?["name"]?.GetValue<string>(), name, StringComparison.Ordinal);
}
