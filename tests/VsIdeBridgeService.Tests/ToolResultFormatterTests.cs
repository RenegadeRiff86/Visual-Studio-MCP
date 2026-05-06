using System.Text.Json.Nodes;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class ToolResultFormatterTests
{
    [Fact]
    public void WarningsWithRowsRemainSuccessfulToolResults()
    {
        JsonObject response = CreateWarningsResponse(success: true);

        JsonObject result = Assert.IsType<JsonObject>(ToolResultFormatter.StructuredToolResult(response));

        Assert.False(result["isError"]!.GetValue<bool>());
        JsonObject structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        JsonObject data = Assert.IsType<JsonObject>(structured["Data"]);
        Assert.Equal(2, data["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public void FailedWarningsResponseRemainsError()
    {
        JsonObject response = CreateWarningsResponse(success: false);

        JsonObject result = Assert.IsType<JsonObject>(ToolResultFormatter.StructuredToolResult(response, isError: true));

        Assert.True(result["isError"]!.GetValue<bool>());
    }

    private static JsonObject CreateWarningsResponse(bool success)
        => new()
        {
            ["SchemaVersion"] = 1,
            ["Command"] = "warnings",
            ["Success"] = success,
            ["Summary"] = "Captured 2 warning row(s).",
            ["Warnings"] = new JsonArray(),
            ["Error"] = success
                ? null
                : new JsonObject
                {
                    ["code"] = "bridge_error",
                    ["message"] = "Bridge command failed.",
                },
            ["Data"] = new JsonObject
            {
                ["count"] = 2,
                ["totalCount"] = 2,
                ["rows"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["severity"] = "Warning",
                        ["code"] = "BP1001",
                        ["message"] = "Example warning.",
                    },
                },
            },
        };
}
