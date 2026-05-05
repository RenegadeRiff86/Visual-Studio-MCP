using System.Text.Json.Nodes;
using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class SearchToolRegistrationTests
{
    private static readonly string[] SharedSearchParameters =
    [
        "chunk_size",
        "chunk_index",
        "sort_by",
        "sort_direction",
        "text",
        "source",
        "group_by",
    ];

    private static readonly string[] SharedReadBatchParameters =
    [
        "chunk_size",
        "chunk_index",
        "sort_by",
        "sort_direction",
        "path",
        "text",
        "group_by",
    ];

    [Theory]
    [InlineData("find_files")]
    [InlineData("find_text")]
    [InlineData("find_text_batch")]
    [InlineData("search_symbols")]
    [InlineData("search_solutions")]
    [InlineData("smart_context")]
    [InlineData("file_symbols")]
    [InlineData("glob")]
    public void SearchToolsExposeSharedFilterAndPagingSchema(string toolName)
    {
        ToolDefinition definition = GetDefinition(toolName);
        JsonObject properties = GetSchemaProperties(definition);

        foreach (string parameter in SharedSearchParameters)
        {
            Assert.True(properties.ContainsKey(parameter), $"{toolName} should expose '{parameter}'.");
        }

        Assert.Equal("Rows per returned chunk (default 25). Set 0 to return all filtered rows.",
            properties["chunk_size"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void FindFilesKeepsProjectPostFilterAvailable()
    {
        ToolDefinition definition = GetDefinition("find_files");
        JsonObject properties = GetSchemaProperties(definition);

        Assert.True(properties.ContainsKey("project"));
    }

    [Fact]
    public void ReadFileBatchExposesSharedSliceSchema()
    {
        ToolDefinition definition = GetDefinition("read_file_batch");
        JsonObject properties = GetSchemaProperties(definition);

        foreach (string parameter in SharedReadBatchParameters)
        {
            Assert.True(properties.ContainsKey(parameter), $"read_file_batch should expose '{parameter}'.");
        }

        Assert.Equal("Slices per returned chunk (default 10). Set 0 to return all filtered slices.",
            properties["chunk_size"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void DiagnosticShapedFindTextCanBeRejectedBeforeSolutionScan()
    {
        JsonObject args = new()
        {
            ["query"] = "BP1002 Numeric literal.*3.*appears 19 times",
            ["scope"] = "solution",
        };

        Assert.True(ToolCatalog.TryGetDiagnosticSearchCode(args, out string code));
        Assert.Equal("BP1002", code);
    }

    [Fact]
    public void FindTextWithPathFilterStaysAvailableForDiagnosticTextInSource()
    {
        JsonObject args = new()
        {
            ["query"] = "BP1002 Numeric literal.*3.*appears 19 times",
            ["path"] = "src",
        };

        Assert.False(ToolCatalog.TryGetDiagnosticSearchCode(args, out _));
    }

    [Fact]
    public void DiagnosticShapedFindTextBatchCanBeRejectedBeforeSolutionScan()
    {
        JsonObject args = new()
        {
            ["queries"] = new JsonArray("HashEdge", "BP1002 Numeric literal.*3.*appears 19 times"),
            ["scope"] = "solution",
        };

        Assert.True(ToolCatalog.TryGetDiagnosticSearchCodeFromBatch(args, out string code, out string query));
        Assert.Equal("BP1002", code);
        Assert.Equal("BP1002 Numeric literal.*3.*appears 19 times", query);
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
