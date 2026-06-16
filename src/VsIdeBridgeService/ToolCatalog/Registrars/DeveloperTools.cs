using System.IO;
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
                Opt("severity", "Minimum severity for extension log events: all, warning, or error (default all)."),
                Opt("source", "Optional source component name filter for extension log (e.g. DocumentService)."),
                OptBool("include_raw", "Include raw filtered tail lines, capped per file (default false).")),
            DeveloperToolsCategory,
            (id, args, bridge) => BridgeLogSummaryTool.ExecuteAsync(id, args, bridge),
            aliases: ["parse_bridge_logs", "read_bridge_logs", "bridge_logs", "mcp_logs", "vsix_logs"],
            tags: ["bridge", "logs", "developer", "mcp", "vsix", "diagnostics"],
            summary: "Parse installed bridge logs for bridge development.",
            readOnly: true,
            searchHints: BuildSearchHints(
                related: [("bridge_health", "Confirm the active bridge binding"), ("errors", "Check current solution diagnostics"), ("read_output", "Inspect Visual Studio output panes")]));

        yield return new("bridge_installed_version",
            "Inspect source version files and the installed VS IDE Bridge payload under Program Files. " +
            "This read-only developer tool helps compare repo source with the active installed bridge stack.",
            ObjectSchema(
                Opt("root", "Optional installed bridge root (default C:\\Program Files\\VsIdeBridge).")),
            DeveloperToolsCategory,
            (id, args, bridge) => BridgeInstalledVersionTool.ExecuteAsync(id, args, bridge),
            aliases: ["installed_bridge_version", "bridge_version", "bridge_install_info"],
            tags: ["bridge", "installed", "version", "developer", "release"],
            summary: "Inspect source and installed bridge versions.",
            readOnly: true,
            searchHints: BuildSearchHints(
                workflow: [("bridge_log_summary", "Inspect installed bridge logs"), ("git_status", "Check source changes before packaging")],
                related: [("set_version", "Update source version files"), ("build_installer", "Package the bridge installer")]));

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

        yield return new("build_installer",
            "Compile the VS IDE Bridge Inno Setup installer script (installer/inno/vs-ide-bridge.iss) " +
            "using ISCC.exe. Produces installer/output/vs-ide-bridge-setup-{version}.exe. " +
            "This developer tool is intended for the bridge codebase only. " +
            "Build and rebuild the solution first, then call this to produce the installer.",
            ObjectSchema(
                Opt("configuration", "Build configuration to package: Release or Debug (default Release). Must match the configuration used to build the solution binaries."),
                Opt("iss_file", "Solution-relative or absolute path to the .iss script (default installer/inno/vs-ide-bridge.iss)."),
                OptInt("timeout_seconds", "Override the compile timeout in seconds (default 120)."))
            ,
            DeveloperToolsCategory,
            async (id, args, bridge) =>
            {
                string solutionDirectory = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string issRelative = args?["iss_file"]?.GetValue<string>()
                    ?? System.IO.Path.Combine("installer", "inno", "vs-ide-bridge.iss");
                string issPath = System.IO.Path.IsPathRooted(issRelative)
                    ? issRelative
                    : System.IO.Path.Combine(solutionDirectory, issRelative);

                if (!System.IO.File.Exists(issPath))
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                        $"Installer script not found: {issPath}");

                string? configuration = args?["configuration"]?.GetValue<string>();
                int timeoutMs = (args?["timeout_seconds"]?.GetValue<int>() ?? 120) * 1000;
                return await InnoSetupRunner.RunAsync(id, issPath, configuration, timeoutMs).ConfigureAwait(false);
            },
            aliases: ["compile_installer", "inno_build", "iscc", "make_installer"],
            tags: ["bridge", "installer", "inno", "iscc", "developer", "release"],
            summary: "Compile the bridge installer using Inno Setup (ISCC.exe).",
            destructive: true,
            searchHints: BuildSearchHints(
                workflow: [("set_version", "Bump version before building the installer"), ("rebuild_solution", "Build solution binaries first"), ("git_status", "Verify release state before compiling")],
                related: [("set_version", "Update version numbers in all release files")]));
    }
}
