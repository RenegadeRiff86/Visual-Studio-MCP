using System.Text.Json.Nodes;
using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class BatchToolRegistrationTests
{
    private static readonly string[] BatchParameters =
    [
        "steps",
        "stop_on_error",
        "chunk_size",
        "chunk_index",
        "sort_by",
        "sort_direction",
        "command",
        "success",
        "text",
        "group_by",
        "data_mode",
    ];

    [Fact]
    public void BatchToolExposesPagingFilteringAndDataModeSchema()
    {
        ToolDefinition definition = GetDefinition("batch");
        JsonObject properties = GetSchemaProperties(definition);

        foreach (string parameter in BatchParameters)
        {
            Assert.True(properties.ContainsKey(parameter), $"batch should expose '{parameter}'.");
        }

        Assert.Contains("summary (default), full, or none", properties["data_mode"]!["description"]!.GetValue<string>());
    }

    private static ToolDefinition GetDefinition(string toolName)
    {
        Assert.True(ToolCatalog.CreateRegistry().TryGetDefinition(toolName, out ToolDefinition? definition));
        return definition;
    }

    private static JsonObject GetSchemaProperties(ToolDefinition definition)
    {
        JsonObject schema = definition.ParameterSchema;
        return Assert.IsType<JsonObject>(schema["properties"]);
    }
}
