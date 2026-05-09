using System.Text.Json.Nodes;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class McpToolSurfaceTests
{
    [Fact]
    public void LazyToolSurfaceAdvertisesOnlyBootstrapTools()
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        string[] names = ToolNames(registry.BuildToolsList(McpToolSurface.Lazy));

        Assert.Equal(McpToolSurface.Lazy.VisibleToolNames!, names);
        Assert.Contains("call_tool", names);
        Assert.Contains("tool_help", names);
        Assert.DoesNotContain("read_file", names);
        Assert.DoesNotContain("apply_diff", names);
    }

    [Fact]
    public void FullToolSurfaceStillAdvertisesRegisteredToolsDirectly()
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        string[] names = ToolNames(registry.BuildToolsList(McpToolSurface.Full));

        Assert.Contains("read_file", names);
        Assert.Contains("apply_diff", names);
        Assert.True(names.Length > McpToolSurface.Lazy.VisibleToolNames!.Count);
    }

    [Fact]
    public void LazyToolSurfaceDoesNotRemoveHiddenToolsFromDispatchRegistry()
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        Assert.True(registry.TryGet("read_file", out _));
        Assert.True(registry.TryGet("apply_diff", out _));
    }

    [Fact]
    public void CatalogDiscoveryEntriesShowCallToolInvocationForHiddenTools()
    {
        JsonObject list = ToolCatalog.CreateRegistry().Definitions.BuildCompactToolsList();
        Assert.Contains("call_tool", list["invocationHint"]!.GetValue<string>());

        JsonArray tools = Assert.IsType<JsonArray>(list["tools"]);
        JsonObject readFile = Assert.IsType<JsonObject>(tools.Single(tool =>
            tool!["name"]!.GetValue<string>() == "read_file"));
        JsonObject invocation = Assert.IsType<JsonObject>(readFile["invocation"]);

        Assert.Equal("call_tool", invocation["tool"]!.GetValue<string>());
        Assert.Equal("read_file", invocation["name"]!.GetValue<string>());
        Assert.Contains("\"name\": \"read_file\"", invocation["pattern"]!.GetValue<string>());
    }

    [Fact]
    public void ToolHelpShowsCallToolInvocationForCatalogTools()
    {
        JsonObject help = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("read_file");

        Assert.Contains("call_tool", help["invocationHint"]!.GetValue<string>());
        JsonObject invocation = Assert.IsType<JsonObject>(help["invocation"]);
        Assert.Equal("read_file", invocation["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedKnownToolDispatchReturnsToolHelp()
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        JsonNode result = await registry.DispatchAsync(
            id: null,
            name: "glob",
            args: [],
            bridge: new BridgeConnection([]));
        JsonObject payload = Assert.IsType<JsonObject>(result);
        JsonArray content = Assert.IsType<JsonArray>(payload["content"]);
        string text = content[0]!["text"]!.GetValue<string>();

        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("Tool help for 'glob'", text);
        Assert.Contains("\"name\":\"glob\"", text);
        Assert.Contains("\"tool\":\"call_tool\"", text);
    }

    [Fact]
    public async Task DispatchValidatesRequiredArgumentsBeforeHandler()
    {
        JsonObject payload = await DispatchAsync("file_outline", []);
        string text = ResultText(payload);

        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("Missing required argument 'file'.", text);
        Assert.Contains("Tool help for 'file_outline'", text);
        Assert.Contains("\"tool\":\"call_tool\"", text);
    }

    [Fact]
    public async Task DispatchRejectsUnexpectedArgumentsBeforeHandler()
    {
        JsonObject payload = await DispatchAsync("git_current_branch", new JsonObject { ["bogus"] = true });
        string text = ResultText(payload);

        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("Unexpected argument 'bogus'.", text);
        Assert.Contains("Tool help for 'git_current_branch'", text);
    }

    [Fact]
    public async Task DispatchRejectsWrongArgumentTypesBeforeHandler()
    {
        JsonObject payload = await DispatchAsync("symbol_info", new JsonObject
        {
            ["file"] = "src/Foo.cs",
            ["line"] = "12",
            ["column"] = 1,
        });
        string text = ResultText(payload);

        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("Argument 'line' must be an integer.", text);
        Assert.Contains("Tool help for 'symbol_info'", text);
    }

    [Fact]
    public async Task DispatchConvertsUnhandledFailuresToToolHelp()
    {
        ToolExecutionRegistry registry = new(
        [
            new ToolEntry(
                "boom",
                "Always fails for test coverage.",
                SchemaHelpers.EmptySchema(),
                "test",
                async (_, _, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("boom exploded");
                }),
        ]);

        JsonNode result = await registry.DispatchAsync(null, "boom", [], new BridgeConnection([]));
        JsonObject payload = Assert.IsType<JsonObject>(result);
        string text = ResultText(payload);

        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("boom exploded", text);
        Assert.Contains("Tool help for 'boom'", text);
    }

    [Theory]
    [InlineData("write_file", "write-file")]
    [InlineData("python_set_startup_file", "set-python-startup-file")]
    [InlineData("python_get_startup_file", "get-python-startup-file")]
    public void CatalogToolsUseExpectedPipeCommandNames(string toolName, string expectedBridgeCommand)
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        Assert.True(registry.TryGet(toolName, out ToolEntry? entry));
        Assert.Equal(expectedBridgeCommand, entry.Definition.BridgeCommand);
    }

    [Fact]
    public void BridgeErrorResultsCanIncludeInlineToolHelp()
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();
        Assert.True(registry.TryGet("find_text", out ToolEntry? entry));
        JsonObject toolHelp = new()
        {
            ["Summary"] = "Help for tool 'find_text'.",
            ["invocation"] = entry.Definition.BuildInvocationEntry(),
            ["tool"] = entry.Definition.BuildToolObject(),
        };
        JsonObject response = new()
        {
            ["Success"] = false,
            ["Error"] = new JsonObject
            {
                ["code"] = "invalid_arguments",
                ["message"] = "Missing required argument --query.",
            },
        };

        JsonObject payload = Assert.IsType<JsonObject>(ToolResultFormatter.StructuredToolResult(
            response,
            isError: true,
            toolHelp: toolHelp));
        string text = ResultText(payload);

        Assert.Contains("Missing required argument --query.", text);
        Assert.Contains("Tool help for 'find_text'", text);
        Assert.Contains("\"name\":\"find_text\"", text);
        Assert.Contains("\"tool\":\"call_tool\"", text);
    }

    [Fact]
    public async Task BatchReturnsPartialResultsWhenMaxStepsIsReached()
    {
        JsonArray steps =
        [
            new JsonObject { ["command"] = "git_current_branch", ["args"] = new JsonObject { ["bogus"] = true } },
            new JsonObject { ["command"] = "git_current_branch", ["args"] = new JsonObject { ["bogus"] = true } },
            new JsonObject { ["command"] = "git_current_branch", ["args"] = new JsonObject { ["bogus"] = true } },
        ];
        JsonObject args = new()
        {
            ["steps"] = steps,
            ["max_steps"] = 2,
            ["chunk_size"] = 0,
        };

        JsonObject payload = await DispatchAsync("batch", args);
        JsonObject structured = Assert.IsType<JsonObject>(payload["structuredContent"]);
        JsonObject data = Assert.IsType<JsonObject>(structured["Data"]);
        JsonArray results = Assert.IsType<JsonArray>(data["results"]);

        Assert.False(payload["isError"]!.GetValue<bool>());
        Assert.True(data["truncated"]!.GetValue<bool>());
        Assert.Equal(3, data["batchCount"]!.GetValue<int>());
        Assert.Equal(2, data["executedCount"]!.GetValue<int>());
        Assert.Equal(2, results.Count);
        Assert.Contains("skipped by max_steps", structured["Summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpToolsListUsesLazySurfaceByDefault()
    {
        JsonObject request = new()
        {
            ["id"] = 1,
            ["method"] = "tools/list",
        };

        JsonObject? response = await McpServerMode.HandleRequestAsync(
            request,
            new BridgeConnection([]),
            controlClient: null);

        JsonArray tools = Assert.IsType<JsonArray>(response!["result"]!["tools"]);
        Assert.Equal(McpToolSurface.Lazy.VisibleToolNames!, ToolNames(tools));
    }

    [Theory]
    [InlineData("full", "full")]
    [InlineData("all", "full")]
    [InlineData("lazy", "lazy")]
    [InlineData("minimal", "lazy")]
    [InlineData("unexpected", "lazy")]
    public void ToolSurfaceCanBeSelectedByArgument(string raw, string expected)
    {
        McpToolSurface surface = McpToolSurface.FromArgs(["--tool-surface", raw]);

        Assert.Equal(expected, surface.Name);
    }

    private static async Task<JsonObject> DispatchAsync(string name, JsonObject args)
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();
        JsonNode result = await registry.DispatchAsync(null, name, args, new BridgeConnection([]));
        return Assert.IsType<JsonObject>(result);
    }

    private static string ResultText(JsonObject payload)
    {
        JsonArray content = Assert.IsType<JsonArray>(payload["content"]);
        return content[0]!["text"]!.GetValue<string>();
    }

    private static string[] ToolNames(JsonArray tools)
        => [.. tools.Select(static tool => tool!["name"]!.GetValue<string>())];
}
