using System.Text.Json;
using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;
using VsIdeBridge.Tooling.Batch;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string BindSolutionToolName = "bind_solution";
    private const int DefaultBatchChunkSize = 10;
    private const int DefaultBatchMaxSteps = 50;
    private const string ListInstancesToolName = "list_instances";
    private const string RecommendToolsToolName = "recommend_tools";
    private const string VsStateToolName = "vs_state";
    private const string WaitForReadyToolName = "wait_for_ready";

    private static IEnumerable<ToolEntry> CoreTools() =>
        CoreBindingTools()
            .Concat(CoreRegistryTools())
            .Concat(CoreSystemTools())
            .Concat(CoreHttpTools());

    private static IEnumerable<ToolEntry> CoreBindingTools()
    {
        yield return new("bridge_health",
            "Get binding health, discovery source, and last round-trip metrics. " +
            "Response includes a toolDiscovery hint — call list_tool_categories, list_tools, " +
            "list_tools_by_category, or recommend_tools to explore all available bridge capabilities.",
            EmptySchema(), Core,
            (id, _, bridge) => BridgeHealthAsync(id, bridge),
            searchHints: BuildSearchHints(
                workflow: [("list_tool_categories", "Discover available tool groups"), (RecommendToolsToolName, "Find the right tool for a task")],
                related: [(VsStateToolName, "Check current IDE state"), (ListInstancesToolName, "Find all bridge instances")]));

        yield return CreateBatchTool();

        yield return new(ListInstancesToolName,
            "List live VS IDE Bridge instances visible to this MCP server.",
            EmptySchema(), Core,
            (_, _, bridge) => ListInstancesAsync(bridge),
            searchHints: BuildSearchHints(
                workflow: [(BindSolutionToolName, "Bind session to a VS instance by solution name"), ("bind_instance", "Bind to a specific instance by ID")],
                related: [("bridge_health", "Check current binding health")]));

        yield return new("bind_instance",
            "Bind this MCP session to one specific Visual Studio bridge instance by " +
            "instance id, process id, or pipe name.",
            ObjectSchema(
                Opt("instance_id", "Optional exact bridge instance id."),
                OptInt("pid", "Optional Visual Studio process id."),
                Opt("pipe_name", "Optional exact bridge pipe name."),
                Opt("solution_hint", "Optional solution path or name substring.")),
            Core,
            async (id, args, bridge) =>
                (JsonNode)ToolResultFormatter.StructuredToolResult(await bridge.BindAsync(id, args).ConfigureAwait(false)),
            searchHints: BuildSearchHints(
                workflow: [(VsStateToolName, "Confirm IDE state after binding"), (WaitForReadyToolName, "Wait for IntelliSense to load")],
                related: [(BindSolutionToolName, "Bind by solution name"), (ListInstancesToolName, "List available instances")]));

        yield return new(BindSolutionToolName,
            "Bind this MCP session to a VS instance whose solution matches a name or path hint.",
            ObjectSchema(Req("solution", "Solution name or path substring to match.")),
            Core,
            async (id, args, bridge) =>
            {
                JsonObject bindArgs = new() { ["solution_hint"] = args?["solution"]?.DeepClone() };
                return (JsonNode)ToolResultFormatter.StructuredToolResult(await bridge.BindAsync(id, bindArgs).ConfigureAwait(false));
            },
            searchHints: BuildSearchHints(
                workflow: [(VsStateToolName, "Confirm IDE state after binding"), (WaitForReadyToolName, "Wait for IntelliSense to load")],
                related: [("bind_instance", "Bind by instance ID"), (ListInstancesToolName, "List available instances")]));

        yield return BridgeTool(VsStateToolName,
            "Current VS editor state — active document, build mode, solution, and debugger.",
            EmptySchema(), "state", _ => Empty(),
            aliases: ["bridge_state", "get_vs_state", "ide_state"],
            summary: "Current VS editor state — active document, build mode, solution, and debugger.",
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check diagnostics"), ("read_file", "Read the active document")],
                related: [("bridge_health", "Check binding health"), (WaitForReadyToolName, "Wait for IntelliSense")]));

        yield return BridgeTool(WaitForReadyToolName,
            "Block until Visual Studio and IntelliSense are fully loaded. Call this after " +
            "open_solution or vs_open before running any semantic tools. This is intentionally slower than normal inspection commands.",
            EmptySchema(), "ready", _ => Empty(),
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check diagnostics after loading"), ("search_symbols", "Search for symbols"), ("file_outline", "Inspect file structure")],
                related: [(VsStateToolName, "Check current IDE state")]));
    }

    private static ToolEntry CreateBatchTool() =>
        new("batch",
            "Execute multiple bridge or service tools in one MCP round-trip. Use when you need results from " +
            "several tools together (e.g. vs_state + errors + list_projects or state + errors + list-projects). " +
            "Steps format: [{\"command\":\"vs_state\"},{\"command\":\"errors\",\"args\":{\"max\":20}},{\"command\":\"list_tool_categories\"}]. " +
            "Note: prefer read_file_batch for multiple file reads and find_text_batch for multiple searches.",
            ObjectSchema(
                (("steps",
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] =
                            "Batch steps to execute in order. Each step is an object: {\"command\":\"tool-or-command-name\",\"args\":{...},\"id\":\"optional-label\"}. " +
                            "Accepts MCP tool names like vs_state/find_text/list_tool_categories and bridge command aliases like state/find-text. " +
                            "Example: [{\"command\":\"vs_state\"},{\"command\":\"errors\",\"args\":{\"max\":20}}]",
                        ["items"] = ObjectSchema(
                            Req("command", "Tool or bridge command name (e.g. vs_state, errors, find-text)."),
                            Opt("id", "Optional label echoed back in the per-step result."),
                            ("args", new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = "Arguments for the step's tool, matching that tool's input schema.",
                                ["additionalProperties"] = true,
                            }, false)),
                        ["minItems"] = 1,
                    },
                    true)),
                OptBool("stop_on_error", "Stop after the first failing step (default false)."),
                OptInt("chunk_size", "Step results per returned chunk (default 10). Set 0 to return all filtered step results."),
                OptInt("chunk_index", "Zero-based step-result chunk index to return (default 0)."),
                Opt("sort_by", "Optional step sort field: index, id, command, success, summary, warnings, or error."),
                Opt("sort_direction", "Optional sort direction: asc or desc (default asc)."),
                Opt("command", "Optional command-name filter applied to batch step results."),
                OptBool("success", "Optional success filter applied to batch step results."),
                Opt("text", "Optional text filter applied to command, id, summary, and error text."),
                Opt("group_by", "Optional grouping mode: command, success, or error."),
                OptInt("max_steps", "Maximum number of batch steps to execute before returning partial results (default 50)."),
                Opt("data_mode", "Nested step data mode: summary (default), full, or none. Use full only when you need each raw step payload.")),
            Core,
            ExecuteBatchToolAsync,
            searchHints: BuildSearchHints(
                related: [("vs_state", "Check IDE state"), ("errors", "Fetch diagnostics"), ("read_file_batch", "Batch-read multiple files instead")]));

    private static async Task<JsonNode> ExecuteBatchToolAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        JsonArray steps = ReadStepsArgument(id, args);

        bool stopOnError = args?["stop_on_error"]?.GetValue<bool>() ?? false;
        int maxSteps = Math.Max(1, args?["max_steps"]?.GetValue<int?>() ?? DefaultBatchMaxSteps);
        JsonObject response = await ExecuteBatchLocallyAsync(id, steps, stopOnError, maxSteps, bridge).ConfigureAwait(false);
        response = CompactBatchResponse(response, args);
        return ToolResultFormatter.StructuredToolResult(
            response,
            args,
            successText: response["Summary"]?.GetValue<string>());
    }

    private static JsonObject CompactBatchResponse(JsonObject response, JsonObject? args)
    {
        if (response["Data"] is not JsonObject data || !BatchResultCollection.TryFromJsonObject(data, out BatchResultCollection collection))
        {
            return response;
        }

        BatchQueryOptions options = BatchQueryOptions.FromJsonObject(args, DefaultBatchChunkSize);
        response["Data"] = collection.ToJsonObject(options, data);
        return response;
    }

    private static JsonArray ReadStepsArgument(JsonNode? id, JsonObject? args)
    {
        JsonNode raw = args?["steps"]
            ?? throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument steps.");

        if (raw is JsonArray array)
        {
            if (array.Count == 0)
            {
                throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Argument 'steps' must contain at least one step.");
            }
            return array;
        }

        // Tolerate clients that send a JSON-encoded string (legacy CLI shape).
        if (raw is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new McpRequestException(id, McpErrorCodes.InvalidParams, $"Argument 'steps' must be a JSON array. {ex.Message}");
            }
            if (parsed is JsonArray parsedArray)
            {
                if (parsedArray.Count == 0)
                {
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Argument 'steps' must contain at least one step.");
                }
                return parsedArray;
            }
        }

        throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Argument 'steps' must be a JSON array of step objects.");
    }

    private static async Task<JsonObject> ExecuteBatchLocallyAsync(
        JsonNode? id,
        JsonArray steps,
        bool stopOnError,
        int maxSteps,
        BridgeConnection bridge)
    {
        JsonArray results = [];
        int successCount = 0;
        int failureCount = 0;
        bool stoppedEarly = false;
        int stepLimit = Math.Min(steps.Count, maxSteps);

        for (int i = 0; i < stepLimit; i++)
        {
            (JsonObject stepResult, bool succeeded) = await ExecuteBatchStepLocallyAsync(id, steps[i], i, bridge).ConfigureAwait(false);
            if (succeeded)
            {
                successCount++;
            }
            else
            {
                failureCount++;
            }

            results.Add(stepResult);

            if (stopOnError && !succeeded)
            {
                stoppedEarly = i < steps.Count - 1;
                break;
            }
        }

        bool truncated = results.Count < steps.Count && !stoppedEarly;
        JsonArray warnings = [];
        if (truncated)
        {
            warnings.Add($"Batch stopped after {results.Count} of {steps.Count} steps because max_steps was {maxSteps}. Re-run with a higher max_steps value to continue.");
        }

        JsonObject data = new()
        {
            ["batchCount"] = steps.Count,
            ["executedCount"] = results.Count,
            ["successCount"] = successCount,
            ["failureCount"] = failureCount,
            ["stoppedEarly"] = stoppedEarly || truncated,
            ["truncated"] = truncated,
            ["results"] = results,
        };

        string summary = truncated
            ? $"Batch: {successCount}/{results.Count} executed steps succeeded, {failureCount} failed, {steps.Count - results.Count} skipped by max_steps."
            : $"Batch: {successCount}/{steps.Count} succeeded, {failureCount} failed.";

        return new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Command"] = "batch",
            ["Success"] = true,
            ["Summary"] = summary,
            ["Warnings"] = warnings,
            ["Error"] = null,
            ["Data"] = data,
        };
    }

    private static async Task<(JsonObject Result, bool Succeeded)> ExecuteBatchStepLocallyAsync(
        JsonNode? id,
        JsonNode? entry,
        int index,
        BridgeConnection bridge)
    {
        if (entry is not JsonObject step)
        {
            return (CreateBatchStepFailure(index, null, string.Empty, "invalid_batch_entry", "Batch entry must be a JSON object."), false);
        }

        string? stepId = step["id"]?.GetValue<string>();
        string commandName = step["command"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return (CreateBatchStepFailure(index, stepId, commandName, "invalid_batch_entry", "Batch step is missing required field 'command'."), false);
        }

        JsonObject? stepArgs;
        try
        {
            stepArgs = ParseBatchStepArgs(step["args"]);
        }
        catch (JsonException ex)
        {
            return (CreateBatchStepFailure(index, stepId, commandName, "invalid_json", $"Batch step args must be valid JSON. {ex.Message}"), false);
        }
        catch (McpRequestException ex)
        {
            return (CreateBatchStepFailure(index, stepId, commandName, "invalid_arguments", ex.Message), false);
        }

        if (!Registry.TryGet(commandName, out ToolEntry? _))
        {
            return (CreateBatchStepFailure(index, stepId, commandName, "unknown_command", $"Tool or bridge command not registered: {commandName}"), false);
        }

        JsonNode toolResult = await Registry.DispatchAsync(id, commandName, stepArgs, bridge).ConfigureAwait(false);
        JsonObject normalized = NormalizeBatchStepResult(index, stepId, commandName, toolResult);
        return (normalized, normalized["success"]?.GetValue<bool>() ?? false);
    }

    private static JsonObject? ParseBatchStepArgs(JsonNode? args)
    {
        if (args is null)
        {
            return null;
        }

        if (args is JsonObject obj)
        {
            return (JsonObject)obj.DeepClone();
        }

        if (args is JsonValue value && value.TryGetValue(out string? raw) && !string.IsNullOrWhiteSpace(raw))
        {
            JsonNode? parsed = JsonNode.Parse(raw);
            if (parsed is JsonObject parsedObject)
            {
                return parsedObject;
            }

            throw new McpRequestException(null, McpErrorCodes.InvalidParams, "Batch step args string must parse to a JSON object.");
        }

        throw new McpRequestException(null, McpErrorCodes.InvalidParams, "Batch step args must be a JSON object.");
    }

    private static JsonObject NormalizeBatchStepResult(int index, string? stepId, string commandName, JsonNode toolResult)
    {
        JsonObject toolResultObject = toolResult as JsonObject ?? [];
        bool succeeded = !(toolResultObject["isError"]?.GetValue<bool>() ?? false);
        JsonNode? structuredContent = toolResultObject["structuredContent"];

        JsonArray warnings = structuredContent is JsonObject structuredObject && structuredObject["Warnings"] is JsonArray structuredWarnings
            ? (JsonArray)structuredWarnings.DeepClone()
            : [];

        JsonNode data = structuredContent is JsonObject structuredResponse && structuredResponse["Data"] is JsonNode rawData
            ? rawData.DeepClone()
            : structuredContent?.DeepClone() ?? new JsonObject();

        JsonNode? error = succeeded
            ? null
            : BuildBatchStepError(structuredContent as JsonObject, GetBatchStepSummary(toolResultObject));

        return new JsonObject
        {
            ["index"] = index,
            ["id"] = stepId is null ? null : stepId,
            ["command"] = commandName,
            ["success"] = succeeded,
            ["summary"] = GetBatchStepSummary(toolResultObject),
            ["warnings"] = warnings,
            ["data"] = data,
            ["error"] = error,
        };
    }

    private static JsonObject CreateBatchStepFailure(
        int index,
        string? stepId,
        string commandName,
        string errorCode,
        string message)
        => new()
        {
            ["index"] = index,
            ["id"] = stepId is null ? null : stepId,
            ["command"] = commandName,
            ["success"] = false,
            ["summary"] = message,
            ["warnings"] = new JsonArray(),
            ["data"] = new JsonObject(),
            ["error"] = new JsonObject
            {
                ["code"] = errorCode,
                ["message"] = message,
            },
        };

    private static string GetBatchStepSummary(JsonObject toolResult)
        => toolResult["content"]?[0]?["text"]?.GetValue<string>()
            ?? toolResult["structuredContent"]?["Summary"]?.GetValue<string>()
            ?? toolResult.ToJsonString();

    private static JsonObject BuildBatchStepError(JsonObject? structuredContent, string summary)
    {
        if (structuredContent?["Error"] is JsonObject rawError)
        {
            return (JsonObject)rawError.DeepClone();
        }

        return new JsonObject
        {
            ["code"] = "tool_error",
            ["message"] = summary,
        };
    }

    private static IEnumerable<ToolEntry> CoreRegistryTools()
    {
        yield return new(
            ToolDefinitionCatalog.CallTool(
                ObjectSchema(
                    Req("name", "Catalog tool name to invoke, for example read_file, find_text, apply_diff, or git_status. Use recommend_tools, list_tools, or tool_help before calling an unfamiliar tool."),
                    ("arguments", new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] =
                            "Arguments object for the target catalog tool. " +
                            "Example wrapper: call_tool with { name: \"read_file\", arguments: { file: \"h:2\", start_line: 260, end_line: 360 } }. " +
                            "Use handles returned by bridge results as file/path values instead of copying full paths. " +
                            "Call tool_help with the same name to inspect its schema first.",
                        ["additionalProperties"] = true,
                    }, false)))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(RecommendToolsToolName, "Find tools for a task"), ("tool_help", "Inspect the target tool schema before dispatch")],
                    related: [("list_tools", "Browse all known tool names"), ("list_tool_categories", "Browse tool groups")])),
            async (id, args, bridge) => await ExecuteCallToolAsync(id, args, bridge).ConfigureAwait(false));

        yield return new(
            ToolDefinitionCatalog.ListTools(EmptySchema())
                .WithSearchHints(BuildSearchHints(
                    workflow: [("list_tools_by_category", "Get a focused list of tools for a specific category"), (RecommendToolsToolName, "Get personalized tool recommendations for a task")],
                    related: [("tool_help", "Get detailed help for a specific tool")])),
            (_, _, _) =>
            {
                JsonObject toolsList = DefinitionRegistry.Value.BuildCompactToolsList();
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(toolsList, successText: toolsList.ToJsonString()));
            });

        yield return new(
            ToolDefinitionCatalog.ListToolCategories(EmptySchema())
                .WithSearchHints(BuildSearchHints(
                    workflow: [("list_tools_by_category", "Get tools for a specific category")],
                    related: [("list_tools", "List all tools at once"), (RecommendToolsToolName, "Get recommendations for a task")])),
            (_, _, _) =>
            {
                JsonObject categories = DefinitionRegistry.Value.BuildCategoryList();
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(categories, successText: categories.ToJsonString()));
            });

        const string CategoryName = "category";
        yield return new(
            ToolDefinitionCatalog.ListToolsByCategory(
                ObjectSchema(Req(CategoryName, "Category name.")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("tool_help", "Get detailed help for a tool from this category")],
                    related: [("list_tools", "List all tools"), (RecommendToolsToolName, "Get recommendations for a specific task")])),
            (_, args, _) =>
            {
                JsonObject toolsByCategory = DefinitionRegistry.Value.BuildToolsByCategory(
                    args?["category"]?.GetValue<string>() ?? string.Empty);
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(toolsByCategory, successText: toolsByCategory.ToJsonString()));
            });

        yield return new(
            ToolDefinitionCatalog.RecommendTools(
                ObjectSchema(Req("task", "Natural-language description of what you want to do.")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("list_tools_by_category", "Browse tools in the suggested category"), ("tool_help", "Get detailed help for a recommended tool")],
                    related: [("list_tools", "See all available tools")])),
            (_, args, _) =>
            {
                JsonObject recommendations = DefinitionRegistry.Value.RecommendTools(
                    args?["task"]?.GetValue<string>() ?? string.Empty);
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(recommendations, successText: recommendations.ToJsonString()));
            });

        yield return new(
            ToolDefinitionCatalog.ToolHelp(
                ObjectSchema(
                    Opt("name", "Optional tool name for focused help."),
                    Opt(CategoryName, "Optional category name.")))
                .WithSearchHints(BuildSearchHints(
                    related: [("list_tools_by_category", "Browse tools by category"), (RecommendToolsToolName, "Get recommendations for a task")])),
            (_, args, _) =>
            {
                JsonObject help = DefinitionRegistry.Value.BuildToolHelp(
                    args?["name"]?.GetValue<string>(),
                    args?["category"]?.GetValue<string>());
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(help, successText: help.ToJsonString()));
            });
    }

    private static async Task<JsonNode> ExecuteCallToolAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string targetTool = RequiredString(id, args, "name");
        if (string.Equals(targetTool, "call_tool", StringComparison.Ordinal))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "call_tool cannot dispatch to itself.");
        }

        JsonObject? targetArgs = null;
        JsonNode? rawArguments = args?["arguments"];
        if (rawArguments is JsonObject argumentObject)
        {
            targetArgs = (JsonObject)argumentObject.DeepClone();
        }
        else if (rawArguments is not null)
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Argument 'arguments' must be a JSON object when provided.");
        }

        return await Registry.DispatchAsync(id, targetTool, targetArgs, bridge).ConfigureAwait(false);
    }

    private static IEnumerable<ToolEntry> CoreSystemTools()
    {
        // yield return new("vs_open",
        //     "Launch a new Visual Studio instance. This launch path is disabled by default until you explicitly enable it for testing. Prefer starting Visual Studio manually and binding to it for normal use.",
        //     ObjectSchema(
        //         Opt("solution", "Absolute path to a .sln or .slnx file to open."),
        //         Opt("devenv_path", "Explicit path to devenv.exe. Auto-detected if omitted.")),
        //     SystemCategory,
        //     (id, args, bridge) => VsOpenAsync(id, args, bridge),
        //     searchHints: BuildSearchHints(
        //         workflow: [("wait_for_instance", "Wait for the instance to appear"), ("bind_solution", "Bind to the launched solution")],
        //         related: [("vs_close", "Close a VS instance"), ("vs_open_enable", "Enable bridge-driven launch first")]));

        yield return new("vs_open_enable",
            "Enable bridge-driven Visual Studio launch for deliberate testing. This persists until disabled.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.Enable())),
            searchHints: BuildSearchHints(
                workflow: [("vs_open", "Launch a VS instance after enabling")],
                related: [("vs_open_disable", "Disable bridge-driven launch"), ("vs_open_status", "Check current status")]));

        yield return new("vs_open_disable",
            "Disable bridge-driven Visual Studio launch and return to the safer manual-start workflow.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.Disable())),
            searchHints: BuildSearchHints(
                related: [("vs_open_enable", "Re-enable launch"), ("vs_open_status", "Check current status")]));

        yield return new("vs_open_status",
            "Show whether bridge-driven Visual Studio launch is currently enabled for testing.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.GetStatus())),
            searchHints: BuildSearchHints(
                related: [("vs_open_enable", "Enable bridge-driven launch"), ("vs_open_disable", "Disable bridge-driven launch")]));

        yield return new("wait_for_instance",
            "Wait for a newly launched Visual Studio bridge instance to appear and become ready. Prefer list_instances polling over this tool.",
            ObjectSchema(
                Opt("solution", "Optional absolute path to the .sln or .slnx file you expect."),
                OptInt("timeout_ms", "How long to wait in milliseconds (default 60000).")),
            SystemCategory,
            (id, args, bridge) => WaitForInstanceAsync(id, args, bridge),
            searchHints: BuildSearchHints(
                workflow: [("bind_solution", "Bind to the appeared instance"), ("wait_for_ready", "Wait for IntelliSense to load")],
                related: [("list_instances", "List all visible instances")]));

        yield return BridgeTool("ui_settings",
            "Read current IDE Bridge UI and security settings.",
            EmptySchema(), "ui-settings", _ => Empty(),
            searchHints: BuildSearchHints(
                related: [("vs_state", "Check IDE state"), ("bridge_health", "Check binding health")]));

        yield return BridgeTool("capture_vs_window",
            "Activate the bound Visual Studio main window, bring it to the foreground, and capture only that window to a PNG image.",
            ObjectSchema(
                Opt("out", "Optional output PNG path. If omitted, saves under %TEMP%\\vs-ide-bridge\\screenshots.")),
            "capture-vs-window",
            a => Build(("out", OptionalString(a, "out"))),
            category: "documents",
            summary: "Activate the bound Visual Studio window and capture only that window to a PNG.",
            readOnly: true,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [("vs_state", "Confirm the bound instance before capture")],
                related: [("activate_window", "Bring a specific tool window forward first"), ("list_windows", "Inspect current VS windows")]));

    }

    private static IEnumerable<ToolEntry> CoreHttpTools()
    {
        yield return new("http_enable",
            $"Enable the legacy shared HTTP MCP endpoint on localhost:{HttpServerDefaults.HttpPort}. " +
            "Enables local clients that still expect direct JSON-RPC POST bodies to connect to the bridge. " +
            "The enabled state persists across restarts and, when the Windows service is installed, reconciles the service-owned listener instead of only the current process. " +
            $"Clients send POST requests with JSON-RPC 2.0 bodies to {HttpServerDefaults.HttpUrl}.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.Enable())),
            searchHints: BuildSearchHints(
                workflow: [("http_status", "Verify the legacy server is running")],
                related: [("http_disable", "Stop the legacy HTTP server"), ("streamable_http_enable", "Start the preferred HTTP MCP server")]));

        yield return new("http_disable",
            "Disable the legacy shared HTTP MCP endpoint and persist the disabled state across restarts. " +
            "When the Windows service is installed, this reconciles the service-owned listener so the port is actually released.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.Disable())),
            searchHints: BuildSearchHints(
                related: [("http_enable", "Start the legacy HTTP server"), ("http_status", "Check legacy server status")]));

        yield return new("http_status",
            $"Show the persisted legacy HTTP MCP state, the actual listener status on localhost:{HttpServerDefaults.HttpPort}, and whether the shared enable flag is in sync with the live port probe.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.GetStatus())),
            searchHints: BuildSearchHints(
                related: [("http_enable", "Start the legacy HTTP server"), ("http_disable", "Stop the legacy HTTP server"), ("streamable_http_status", "Check preferred HTTP MCP server status")]));

        yield return new("streamable_http_enable",
            $"Enable the preferred Streamable HTTP MCP endpoint at {HttpServerDefaults.StreamableHttpUrl}. " +
            "This listener also serves 2024-11-05-compatible /sse and /messages endpoints on the same port. " +
            "The enabled state persists across restarts and reconciles the service-owned listener when the Windows service is installed.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(StreamableHttpServerController.Enable())),
            searchHints: BuildSearchHints(
                workflow: [("streamable_http_status", "Verify the Streamable HTTP server is running")],
                related: [("streamable_http_disable", "Stop the Streamable HTTP server"), ("http_enable", "Start the legacy HTTP server")]));

        yield return new("streamable_http_disable",
            "Disable the preferred Streamable HTTP MCP endpoint and persist the disabled state across restarts. " +
            "When the Windows service is installed, this reconciles the service-owned listener so the port is actually released.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(StreamableHttpServerController.Disable())),
            searchHints: BuildSearchHints(
                related: [("streamable_http_enable", "Start the Streamable HTTP server"), ("streamable_http_status", "Check Streamable HTTP server status")]));

        yield return new("streamable_http_status",
            $"Show the persisted Streamable HTTP MCP state, the actual listener status on localhost:{HttpServerDefaults.StreamableHttpPort}, and whether the streamable enable flag is in sync with the live port probe.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(StreamableHttpServerController.GetStatus())),
            searchHints: BuildSearchHints(
                related: [("streamable_http_enable", "Start the Streamable HTTP server"), ("streamable_http_disable", "Stop the Streamable HTTP server"), ("http_status", "Check legacy server status")]));
    }
}
