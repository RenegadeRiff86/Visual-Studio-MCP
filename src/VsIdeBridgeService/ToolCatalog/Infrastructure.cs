using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static readonly Lazy<ToolRegistry> DefinitionRegistry = new(BuildDefinitionRegistry);

    private static ToolRegistry BuildDefinitionRegistry()
    {
        return new ToolRegistry(CreateEntries().Select(static entry => entry.Definition));
    }

    // Send one bridge command and wrap the bridge response as an MCP tool result.
    private static ToolEntry BridgeTool(
        ToolDefinition definition,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs)
        => new(definition,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args);
            });

    private static ToolEntry BridgeTool(
        string name,
        string description,
        JsonObject schema,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs,
        string category = "core",
        string? title = null,
        JsonObject? annotations = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? summary = null,
        bool? readOnly = null,
        bool? mutating = null,
        bool? destructive = null)
        => new(name, description, schema, category,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args);
            },
            title,
            annotations,
            outputSchema: null,
            aliases,
            tags,
            bridgeCommand: pipeCommand,
            summary,
            readOnly,
            mutating,
            destructive);

    private static JsonNode BridgeResult(JsonObject response, JsonObject? args = null)
    {
        bool success = response["Success"]?.GetValue<bool>() ?? false;
        return ToolResultFormatter.StructuredToolResult(response, args, isError: !success);
    }
}

internal static class ToolResultFormatter
{
    internal static JsonNode StructuredToolResult(
        JsonObject response,
        JsonObject? args = null,
        bool isError = false,
        string? successText = null)
    {
        bool showFullPayload = isError || WantsFullSuccessPayload(args);
        string text = showFullPayload
            ? response.ToJsonString()
            : successText ?? CreateSuccessText(response);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = isError,
            ["structuredContent"] = response.DeepClone(),
        };
    }

    private static bool WantsFullSuccessPayload(JsonObject? args)
        => args?["verbose"]?.GetValue<bool?>() == true
            || args?["full"]?.GetValue<bool?>() == true;

    private static string CreateSuccessText(JsonObject response)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            string? command = response["Command"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(command)
                ? summary
                : $"{command}: {summary}";
        }

        string? commandName = response["Command"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(commandName)
            ? "Command completed successfully."
            : $"{commandName}: completed successfully.";
    }
}
