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
                return BridgeResult(response);
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
                return BridgeResult(response);
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

    private static JsonNode BridgeResult(JsonObject response)
    {
        bool success = response["Success"]?.GetValue<bool>() ?? false;
        return ToolResult(response, isError: !success);
    }

    private static JsonNode ToolResult(JsonObject response, bool isError = false)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = response.ToJsonString(),
                },
            },
            ["isError"] = isError,
            ["structuredContent"] = response.DeepClone(),
        };
    }
}
