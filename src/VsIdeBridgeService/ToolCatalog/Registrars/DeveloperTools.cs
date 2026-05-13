using VsIdeBridgeService.SystemTools;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string DeveloperToolsCategory = "developer_tools";

    private static IEnumerable<ToolEntry> DeveloperTools()
    {
        yield return new("bridge_log_summary",
            "Parse installed VS IDE Bridge MCP and Visual Studio extension logs. " +
            "This developer tool is for debugging the bridge itself, not normal user-project diagnostics.",
            ObjectSchema(
                Opt("log", "Log set to inspect: all, mcp, extension, or vsix (default all)."),
                OptInt("tail_lines", "Number of tail lines to read per log file (default 300, max 2000)."),
                OptInt("max_events", "Maximum parsed notable events to return across logs (default 80, max 300)."),
                Opt("text", "Optional case-insensitive text filter applied before event extraction."),
                OptBool("include_raw", "Include raw filtered tail lines, capped per file (default false).")),
            DeveloperToolsCategory,
            (id, args, bridge) => BridgeLogSummaryTool.ExecuteAsync(id, args, bridge),
            aliases: ["parse_bridge_logs", "read_bridge_logs", "bridge_logs", "mcp_logs", "vsix_logs"],
            tags: ["bridge", "logs", "developer", "mcp", "vsix", "diagnostics"],
            summary: "Parse installed bridge logs for bridge development.",
            readOnly: true,
            searchHints: BuildSearchHints(
                related: [("bridge_health", "Confirm the active bridge binding"), ("errors", "Check current solution diagnostics"), ("read_output", "Inspect Visual Studio output panes")]));

        yield return new("set_version",
            "Update VS IDE Bridge release version files. " +
            "This developer tool is intended for the bridge codebase only.",
            ObjectSchema(
                Req("version", "New version string (e.g. 2.1.0).")),
            DeveloperToolsCategory,
            (id, args, bridge) => SetVersionTool.ExecuteAsync(id, args, bridge),
            aliases: ["version_numbering", "bump_bridge_version", "set_bridge_version"],
            tags: ["bridge", "version", "release", "developer"],
            summary: "Update bridge release version files.",
            destructive: true,
            searchHints: BuildSearchHints(
                workflow: [("build", "Rebuild after changing the version"), ("errors", "Check for version-related errors")],
                related: [("bridge_log_summary", "Inspect bridge logs if release tooling misbehaves"), ("git_status", "Review version file changes")]));
    }
}
