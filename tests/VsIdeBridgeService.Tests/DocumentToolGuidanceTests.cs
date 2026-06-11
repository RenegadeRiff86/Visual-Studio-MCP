using System.Text.Json.Nodes;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class DocumentToolGuidanceTests
{
    [Fact]
    public void ListTabsHelpExplainsSevenTabCleanupWorkflow()
    {
        JsonObject help = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("list_tabs");
        JsonObject tool = Assert.IsType<JsonObject>(help["tool"]);
        string description = tool["description"]!.GetValue<string>();
        JsonObject annotations = Assert.IsType<JsonObject>(tool["annotations"]);
        JsonObject searchHints = Assert.IsType<JsonObject>(annotations["search_hints"]);
        JsonArray workflow = Assert.IsType<JsonArray>(searchHints["workflow"]);

        Assert.Contains("more than 7 tabs", description);
        Assert.Contains(workflow, item => ToolHintName(item) == "close_file");
        Assert.Contains(workflow, item => ToolHintName(item) == "close_document");
        Assert.Contains(workflow, item => ToolHintName(item) == "close_others");
    }

    private static string? ToolHintName(JsonNode? item)
        => item is JsonObject hint ? hint["tool"]?.GetValue<string>() : null;
}
