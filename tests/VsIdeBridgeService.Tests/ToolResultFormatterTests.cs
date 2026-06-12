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

    [Fact]
    public void DebugLocalsRendersNamesAndValues()
    {
        JsonObject response = new()
        {
            ["Command"] = "debug-locals",
            ["Summary"] = "Captured 2 local variable(s).",
            ["Data"] = new JsonObject
            {
                ["count"] = 2,
                ["locals"] = new JsonArray
                {
                    new JsonObject { ["name"] = "totalCount", ["type"] = "int", ["value"] = "2", ["isValid"] = true },
                    new JsonObject { ["name"] = "toolName", ["type"] = "string", ["value"] = "\"debug_locals\"", ["isValid"] = true },
                },
            },
        };

        JsonObject result = Assert.IsType<JsonObject>(ToolResultFormatter.StructuredToolResult(response));
        string text = result["content"]![0]!["text"]!.GetValue<string>();

        Assert.Contains("totalCount = 2 (int)", text);
        Assert.Contains("toolName = \"debug_locals\" (string)", text);
    }

    [Fact]
    public void DebugWatchRendersExpressionValue()
    {
        JsonObject response = new()
        {
            ["Command"] = "debug-watch",
            ["Summary"] = "Debugger watch expression evaluated.",
            ["Data"] = new JsonObject
            {
                ["expression"] = "response.Command",
                ["name"] = "response.Command",
                ["type"] = "string",
                ["value"] = "\"debug-watch\"",
                ["isValid"] = true,
            },
        };

        JsonObject result = Assert.IsType<JsonObject>(ToolResultFormatter.StructuredToolResult(response));
        string text = result["content"]![0]!["text"]!.GetValue<string>();

        Assert.Contains("debug-watch: response.Command = \"debug-watch\" (string)", text);
        Assert.DoesNotContain("Debugger watch expression evaluated.", text);
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
