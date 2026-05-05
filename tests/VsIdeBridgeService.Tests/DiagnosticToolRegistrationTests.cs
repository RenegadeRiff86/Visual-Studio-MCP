using System.Text.Json.Nodes;
using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class DiagnosticToolRegistrationTests
{
    private static readonly string[] SharedDiagnosticParameters =
    [
        "severity",
        "wait_for_intellisense",
        "quick",
        "refresh",
        "max",
        "chunk_size",
        "chunk_index",
        "sort_by",
        "sort_direction",
        "code",
        "project",
        "path",
        "text",
        "group_by",
    ];

    [Theory]
    [InlineData("errors", "Optional diagnostic code prefix filter.")]
    [InlineData("warnings", "Optional warning code prefix filter.")]
    [InlineData("messages", "Optional message code prefix filter.")]
    public void DiagnosticRowToolsExposeSharedFilterSchema(string toolName, string codeDescription)
    {
        ToolDefinition definition = GetDefinition(toolName);
        JsonObject properties = GetSchemaProperties(definition);

        foreach (string parameter in SharedDiagnosticParameters)
        {
            Assert.True(properties.ContainsKey(parameter), $"{toolName} should expose '{parameter}'.");
        }

        Assert.Equal(codeDescription, properties["code"]!["description"]!.GetValue<string>());
        Assert.Equal("Rows per returned chunk (default 10, or max when set). Set 0 to return all filtered rows.",
            properties["chunk_size"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void WarningsToolUsesCatalogDefinitionMetadata()
    {
        ToolDefinition definition = GetDefinition("warnings");

        Assert.True(definition.ReadOnly);
        Assert.Equal("diagnostics", definition.Category);
        Assert.Equal("warnings", definition.BridgeCommand);
        Assert.Equal("Error List Warnings", definition.Title);
        Assert.Contains("list_warnings", definition.Aliases);
        Assert.Contains("diagnostic_warnings", definition.Aliases);
        Assert.NotNull(definition.SearchHints);
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
