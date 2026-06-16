using System.Text.Json.Nodes;
using VsIdeBridge.Shared;
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
    public void DeveloperToolsCategoryContainsBridgeMaintenanceTools()
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        Assert.True(registry.TryGet("bridge_log_summary", out ToolEntry? logTool));
        Assert.Equal("developer_tools", logTool.Definition.Category);
        Assert.True(logTool.Definition.ReadOnly);
        Assert.False(logTool.Definition.Mutating);
        Assert.Contains("parse_bridge_logs", logTool.Definition.Aliases);

        Assert.True(registry.TryGet("bridge_installed_version", out ToolEntry? installedTool));
        Assert.Equal("developer_tools", installedTool.Definition.Category);
        Assert.True(installedTool.Definition.ReadOnly);
        Assert.False(installedTool.Definition.Mutating);

        Assert.True(registry.TryGet("set_version", out ToolEntry? versionTool));
        Assert.Equal("developer_tools", versionTool.Definition.Category);
        Assert.True(versionTool.Definition.Mutating);
    }

    [Theory]
    [InlineData("git_status", false, false, false)]
    [InlineData("git_add", false, true, false)]
    [InlineData("git_fetch", false, true, false)]
    [InlineData("git_reset", false, true, false)]
    [InlineData("git_restore", false, true, true)]
    [InlineData("git_commit_amend", false, true, true)]
    [InlineData("git_stash_push", false, true, false)]
    [InlineData("git_log", false, false, false)]
    [InlineData("git_log_range", false, false, false)]
    [InlineData("git_compare_refs", false, false, false)]
    [InlineData("git_diff_range", false, false, false)]
    [InlineData("git_file_history", false, false, false)]
    [InlineData("git_blame", false, false, false)]
    [InlineData("git_merge_base", false, false, false)]
    [InlineData("git_cherry", false, false, false)]
    [InlineData("git_conflicts", false, false, false)]
    [InlineData("git_rebase", false, true, true)]
    [InlineData("git_rebase_continue", false, true, false)]
    [InlineData("git_rebase_abort", false, true, true)]
    [InlineData("git_rebase_skip", false, true, true)]
    [InlineData("github_issue_list", false, false, false)]
    [InlineData("github_issue_search", false, false, false)]
    [InlineData("github_issue_view", false, false, false)]
    [InlineData("github_issue_comment", false, true, false)]
    [InlineData("github_issue_close", false, true, true)]
    [InlineData("github_issue_reopen", false, true, false)]
    [InlineData("github_issue_edit", false, true, false)]
    [InlineData("github_issue_create", false, true, false)]
    [InlineData("github_pr_list", false, false, false)]
    [InlineData("github_pr_view", false, false, false)]
    [InlineData("github_pr_diff", false, false, false)]
    [InlineData("github_pr_comments", false, false, false)]
    [InlineData("github_pr_reviews", false, false, false)]
    [InlineData("github_pr_review_threads", false, false, false)]
    [InlineData("github_pr_checks", false, false, false)]
    [InlineData("github_actions_failed_logs", false, false, false)]
    public void GitCategoryIncludesUsableHistoryRebaseAndGitHubIssueTools(string toolName, bool expectedReadOnly, bool expectedMutating, bool expectedDestructive)
    {
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        Assert.True(registry.TryGet(toolName, out ToolEntry? entry));
        Assert.Equal("git", entry.Definition.Category);
        Assert.Equal(expectedReadOnly, entry.Definition.ReadOnly);
        Assert.Equal(expectedMutating, entry.Definition.Mutating);
        Assert.Equal(expectedDestructive, entry.Definition.Destructive);

        if (toolName == "github_issue_view")
        {
            JsonObject help = registry.Definitions.BuildToolHelp(toolName);
            JsonObject tool = Assert.IsType<JsonObject>(help["tool"]);
            string description = tool["description"]!.GetValue<string>();
            JsonObject schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
            JsonObject properties = Assert.IsType<JsonObject>(schema["properties"]);

            Assert.Contains("not saved to disk", description);
            Assert.Contains("comments", properties.Select(property => property.Key));
        }
    }

    [Fact]
    public void DefaultCategoriesIncludeDeveloperTools()
    {
        ToolCategoryDefinition category = ToolRegistry.DefaultCategoryDefinitions.Single(category =>
            category.Name == "developer_tools");
        ToolCategoryDefinition memoryCategory = ToolRegistry.DefaultCategoryDefinitions.Single(category =>
            category.Name == "memory");
        ToolExecutionRegistry registry = ToolCatalog.CreateRegistry();

        Assert.Equal("Bridge development", category.Summary);
        Assert.Contains("Bridge-code-only", category.Description);
        Assert.Equal("Codex memory", memoryCategory.Summary);
        Assert.True(registry.TryGet("memory_search", out ToolEntry? searchTool));
        Assert.Equal("memory", searchTool.Definition.Category);
        Assert.True(searchTool.Definition.ReadOnly);
        Assert.True(registry.TryGet("memory_read", out ToolEntry? readTool));
        Assert.Equal("memory", readTool.Definition.Category);
        Assert.True(readTool.Definition.ReadOnly);
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
        JsonObject handleGuide = Assert.IsType<JsonObject>(help["handleGuide"]);
        Assert.Contains("HandleService", handleGuide["runtime"]!.GetValue<string>());
        Assert.Contains("canonical file reference", handleGuide["summary"]!.GetValue<string>());
        Assert.Contains("find_files", handleGuide["create"]!.GetValue<string>());
        Assert.Contains("4 or fewer", handleGuide["batchingPolicy"]!.GetValue<string>());
        JsonArray examples = Assert.IsType<JsonArray>(handleGuide["callToolExamples"]);
        Assert.Contains(examples, example => example?.GetValue<string>().Contains("\"file\":\"h:2\"") == true);
        JsonObject invocation = Assert.IsType<JsonObject>(help["invocation"]);
        Assert.Equal("read_file", invocation["name"]!.GetValue<string>());
    }

    [Fact]
    public void CallHierarchyHelpExposesNativeOptionsAndCorrectAliases()
    {
        JsonObject help = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("call_hierarchy");
        JsonObject tool = Assert.IsType<JsonObject>(help["tool"]);
        string description = tool["description"]!.GetValue<string>();
        JsonObject schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        JsonObject properties = Assert.IsType<JsonObject>(schema["properties"]);
        JsonObject annotations = Assert.IsType<JsonObject>(tool["annotations"]);
        JsonArray aliases = Assert.IsType<JsonArray>(annotations["aliases"]);

        Assert.Contains("C/C++", description);
        Assert.Contains("activate_window", properties.Select(property => property.Key));
        Assert.Contains("select_word", properties.Select(property => property.Key));
        Assert.Contains("timeout_ms", properties.Select(property => property.Key));
        Assert.Equal("call_hierarchy", tool["name"]!.GetValue<string>());
        Assert.Contains(aliases, alias => alias?.GetValue<string>() == "caller_hierarchy");
        Assert.DoesNotContain(aliases, alias => alias?.GetValue<string>() == "call_hierachy");
    }

    [Fact]
    public void DebugInspectionHelpExposesThreadFrameAndTimeoutOptions()
    {
        JsonObject localsHelp = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("debug_locals");
        JsonObject localsTool = Assert.IsType<JsonObject>(localsHelp["tool"]);
        JsonObject localsSchema = Assert.IsType<JsonObject>(localsTool["inputSchema"]);
        JsonObject localsProperties = Assert.IsType<JsonObject>(localsSchema["properties"]);
        string localsDescription = localsTool["description"]!.GetValue<string>();

        Assert.Contains("selected stack frame", localsDescription);
        Assert.Contains("thread_id", localsProperties.Select(property => property.Key));
        Assert.Contains("frame_index", localsProperties.Select(property => property.Key));

        JsonObject watchHelp = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("debug_watch");
        JsonObject watchTool = Assert.IsType<JsonObject>(watchHelp["tool"]);
        JsonObject watchSchema = Assert.IsType<JsonObject>(watchTool["inputSchema"]);
        JsonObject watchProperties = Assert.IsType<JsonObject>(watchSchema["properties"]);
        string watchDescription = watchTool["description"]!.GetValue<string>();

        Assert.Contains("selected thread and stack frame", watchDescription);
        Assert.Contains("thread_id", watchProperties.Select(property => property.Key));
        Assert.Contains("frame_index", watchProperties.Select(property => property.Key));
        Assert.Contains("timeout_ms", watchProperties.Select(property => property.Key));
    }

    [Fact]
    public void ApplyDiffEditsSchemaAllowsPerEditReplaceAll()
    {
        JsonObject help = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("apply_diff");
        JsonObject tool = Assert.IsType<JsonObject>(help["tool"]);
        JsonObject schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        JsonObject properties = Assert.IsType<JsonObject>(schema["properties"]);
        JsonObject edits = Assert.IsType<JsonObject>(properties["edits"]);
        JsonObject items = Assert.IsType<JsonObject>(edits["items"]);
        JsonObject editProperties = Assert.IsType<JsonObject>(items["properties"]);
        JsonObject replaceAll = Assert.IsType<JsonObject>(editProperties["replace_all"]);
        JsonArray required = Assert.IsType<JsonArray>(items["required"]);

        Assert.Equal("boolean", replaceAll["type"]!.GetValue<string>());
        Assert.Contains("explicit file", replaceAll["description"]!.GetValue<string>());
        Assert.Contains("4 or fewer", edits["description"]!.GetValue<string>());
        Assert.DoesNotContain(required, item => item?.GetValue<string>() == "replace_all");

        JsonObject projectHelp = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("query_project_items");
        JsonObject projectTool = Assert.IsType<JsonObject>(projectHelp["tool"]);
        string projectDescription = projectTool["description"]!.GetValue<string>();
        JsonObject file = Assert.IsType<JsonObject>(properties["file"]);
        string applyDescription = tool["description"]!.GetValue<string>();

        Assert.Contains("project file itself", projectDescription);
        Assert.Contains("read_file or apply_diff", projectDescription);
        Assert.Contains("Project/MSBuild files", applyDescription);
        Assert.Contains("query_project_items", applyDescription);
        Assert.Contains("Project/MSBuild files", file["description"]!.GetValue<string>());
    }

    [Fact]
    public void BatchHelpWarnsForSmallMutatingBatches()
    {
        JsonObject help = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("batch");
        JsonObject tool = Assert.IsType<JsonObject>(help["tool"]);
        string description = tool["description"]!.GetValue<string>();
        JsonObject schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
        JsonObject properties = Assert.IsType<JsonObject>(schema["properties"]);
        JsonObject maxSteps = Assert.IsType<JsonObject>(properties["max_steps"]);

        Assert.Contains("instead of launching parallel tool calls", description);
        Assert.Contains("max_steps under 5", description);
        Assert.Contains("set this under 5", maxSteps["description"]!.GetValue<string>());
    }

    [Fact]
    public void CompileFileHelpExplainsHeaderCompileLimitation()
    {
        JsonObject help = ToolCatalog.CreateRegistry().Definitions.BuildToolHelp("compile_file");
        JsonObject tool = Assert.IsType<JsonObject>(help["tool"]);
        string description = tool["description"]!.GetValue<string>();
        JsonObject annotations = Assert.IsType<JsonObject>(tool["annotations"]);

        Assert.Contains("Build.Compile", description);
        Assert.Contains("Headers are not compiled directly", description);
        Assert.Contains(".cpp", description);
        Assert.Contains("max_steps under 5", description);
        Assert.Contains("avoid parallel compile_file calls", description);
        Assert.Equal("execute-command", annotations["bridgeCommand"]!.GetValue<string>());
    }

    [Fact]
    public void DiscoveryHelpIncludesHandleGuide()
    {
        JsonObject list = ToolCatalog.CreateRegistry().Definitions.BuildCompactToolsList();

        JsonObject handleGuide = Assert.IsType<JsonObject>(list["handleGuide"]);
        JsonArray prefixes = Assert.IsType<JsonArray>(handleGuide["prefixes"]);

        Assert.Contains(prefixes, prefix =>
            prefix is JsonObject item
            && item["prefix"]?.GetValue<string>() == "h"
            && item["kind"]?.GetValue<string>() == "SearchHit");
        Assert.Contains("handle directly", handleGuide["policy"]!.GetValue<string>());
        Assert.Contains("find_text/search_symbols", handleGuide["create"]!.GetValue<string>());
        Assert.Contains("file + old_content + new_content", handleGuide["applyDiffPolicy"]!.GetValue<string>());
        Assert.Contains("4 or fewer", handleGuide["applyDiffPolicy"]!.GetValue<string>());
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
    [InlineData("compile_file", "execute-command")]
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
