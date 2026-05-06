using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    private const string DiagnosticsCategory = "diagnostics";

    public static ToolDefinition Errors(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "errors",
            DiagnosticsCategory,
            "Read current Error List.",
            "Read current Error List diagnostics without triggering a build. After edits or builds, prefer wait_for_ready first and use build_errors when you need a fresh build plus Error List snapshot.",
            parameterSchema,
            bridgeCommand: "errors",
            title: "Error List Diagnostics",
            aliases: ["error_list", "diagnostics", "list_errors"],
            tags: ["diagnostics", "errors", "build", "warnings"]);

    public static ToolDefinition Warnings(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "warnings",
            DiagnosticsCategory,
            "Read current Error List warnings.",
            "Read current Error List warning rows without triggering a build. Use this when you want compiler and analyzer warnings without mixing them with errors or build messages.",
            parameterSchema,
            bridgeCommand: "warnings",
            title: "Error List Warnings",
            aliases: ["list_warnings", "diagnostic_warnings", "error_list_warnings"],
            tags: ["diagnostics", "warnings", "build", "error-list"]);

    public static ToolDefinition Messages(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "messages",
            DiagnosticsCategory,
            "Read current Error List messages.",
            "Read current Error List message rows without triggering a build. Use this when you want informational and build message output without mixing it into warnings.",
            parameterSchema,
            bridgeCommand: "messages",
            title: "Error List Messages",
            aliases: ["list_messages", "diagnostic_messages", "error_list_messages"],
            tags: ["diagnostics", "messages", "build", "error-list"]);
}
