using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> CoreTools() =>
        CoreBindingTools()
            .Concat(CoreRegistryTools())
            .Concat(CoreSystemTools());

    private static IEnumerable<ToolEntry> CoreBindingTools()
    {
        yield return new("bridge_health",
            "Get binding health, discovery source, and last round-trip metrics.",
            EmptySchema(), Core,
            (id, _, bridge) => BridgeHealthAsync(id, bridge));

        yield return BridgeTool("batch",
            "Execute multiple bridge commands in one round-trip. Use when you need results from " +
            "several commands together (e.g. state + errors + list-projects). " +
            "Steps format: [{\"command\":\"state\"},{\"command\":\"errors\",\"args\":\"{\\\"max\\\":20}\"},{\"command\":\"list-projects\"}]. " +
            "Note: prefer read_file_batch for multiple file reads and find_text_batch for multiple searches.",
            ObjectSchema(Req("steps",
                "JSON array of command steps. Each step: {\"command\":\"cmd-name\",\"args\":\"{...}\",\"id\":\"optional-label\"}. " +
                "Example: [{\"command\":\"state\"},{\"command\":\"errors\",\"args\":\"{\\\"max\\\":20}\"}]")),
            "batch",
            a => Build(("steps", OptionalString(a, "steps"))),
            Core);

        yield return new("list_instances",
            "List live VS IDE Bridge instances visible to this MCP server.",
            EmptySchema(), Core,
            (_, _, bridge) => ListInstancesAsync(bridge));

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
                (JsonNode)ToolResultFormatter.StructuredToolResult(await bridge.BindAsync(id, args).ConfigureAwait(false)));

        yield return new("bind_solution",
            "Bind this MCP session to a VS instance whose solution matches a name or path hint.",
            ObjectSchema(Req("solution", "Solution name or path substring to match.")),
            Core,
            async (id, args, bridge) =>
            {
                JsonObject bindArgs = new() { ["solution_hint"] = args?["solution"]?.DeepClone() };
                return (JsonNode)ToolResultFormatter.StructuredToolResult(await bridge.BindAsync(id, bindArgs).ConfigureAwait(false));
            });

        yield return BridgeTool("vs_state",
            "Current VS editor state — active document, build mode, solution, and debugger.",
            EmptySchema(), "state", _ => Empty(),
            aliases: ["bridge_state", "get_vs_state", "ide_state"],
            summary: "Current VS editor state — active document, build mode, solution, and debugger.");

        yield return BridgeTool("wait_for_ready",
            "Block until Visual Studio and IntelliSense are fully loaded. Call this after " +
            "open_solution or vs_open before running any semantic tools. This is intentionally slower than normal inspection commands.",
            EmptySchema(), "ready", _ => Empty());
    }

    private static IEnumerable<ToolEntry> CoreRegistryTools()
    {
        yield return new(ToolDefinitionCatalog.ListTools(EmptySchema()),
            (_, _, _) => Task.FromResult<JsonNode>(
                ToolResultFormatter.StructuredToolResult(DefinitionRegistry.Value.BuildCompactToolsList())));

        yield return new(ToolDefinitionCatalog.ListToolCategories(EmptySchema()),
            (_, _, _) => Task.FromResult<JsonNode>(
                ToolResultFormatter.StructuredToolResult(DefinitionRegistry.Value.BuildCategoryList())));

        const string CategoryName = "category";
        yield return new(
            ToolDefinitionCatalog.ListToolsByCategory(
                ObjectSchema(Req(CategoryName, "Category name."))),
            (_, args, _) => Task.FromResult<JsonNode>(
                ToolResultFormatter.StructuredToolResult(
                    DefinitionRegistry.Value.BuildToolsByCategory(
                        args?["category"]?.GetValue<string>() ?? string.Empty))));

        yield return new(
            ToolDefinitionCatalog.RecommendTools(
                ObjectSchema(Req("task", "Natural-language description of what you want to do."))),
            (_, args, _) => Task.FromResult<JsonNode>(
                ToolResultFormatter.StructuredToolResult(
                    DefinitionRegistry.Value.RecommendTools(
                        args?["task"]?.GetValue<string>() ?? string.Empty))));

        yield return BridgeTool("tool_help",
            "Return MCP tool help. Pass name for one tool, category for a group, or omit both for the category index.",
            ObjectSchema(
                Opt("name", "Optional tool name for focused help."),
                Opt(CategoryName, "Optional category: core, search, diagnostics, documents, debug, git, python, project, or system.")),
            "help",
            a => Build(
                ("name", OptionalString(a, "name")),
                (CategoryName, OptionalString(a, CategoryName))),
            SystemCategory,
            aliases: ["help"]);
    }

    private static IEnumerable<ToolEntry> CoreSystemTools()
    {
        yield return new("vs_open",
            "Launch a new Visual Studio instance. This launch path is disabled by default until you explicitly enable it for testing. Prefer starting Visual Studio manually and binding to it for normal use.",
            ObjectSchema(
                Opt("solution", "Absolute path to a .sln or .slnx file to open."),
                Opt("devenv_path", "Explicit path to devenv.exe. Auto-detected if omitted.")),
            SystemCategory,
            (id, args, bridge) => VsOpenAsync(id, args, bridge));

        yield return new("vs_open_enable",
            "Enable bridge-driven Visual Studio launch for deliberate testing. This persists until disabled.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.Enable())));

        yield return new("vs_open_disable",
            "Disable bridge-driven Visual Studio launch and return to the safer manual-start workflow.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.Disable())));

        yield return new("vs_open_status",
            "Show whether bridge-driven Visual Studio launch is currently enabled for testing.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.GetStatus())));

        yield return new("wait_for_instance",
            "Wait for a newly launched Visual Studio bridge instance to appear and become ready. Prefer list_instances polling over this tool.",
            ObjectSchema(
                Opt("solution", "Optional absolute path to the .sln or .slnx file you expect."),
                OptInt("timeout_ms", "How long to wait in milliseconds (default 60000).")),
            SystemCategory,
            (id, args, bridge) => WaitForInstanceAsync(id, args, bridge));

        yield return BridgeTool("ui_settings",
            "Read current IDE Bridge UI and security settings.",
            EmptySchema(), "ui-settings", _ => Empty());

        yield return new("http_enable",
            "Start the HTTP MCP server on localhost:8080. " +
            "Enables Ollama and other local LLM clients to connect directly to the bridge. " +
            "The enabled state persists across restarts. " +
            "Clients send POST requests with JSON-RPC 2.0 bodies to http://localhost:8080/.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.Enable())));

        yield return new("http_disable",
            "Stop the HTTP MCP server and persist the disabled state across restarts.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.Disable())));

        yield return new("http_status",
            "Show whether the HTTP MCP server is running, its port, and the URL to connect to.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.GetStatus())));
    }
}
