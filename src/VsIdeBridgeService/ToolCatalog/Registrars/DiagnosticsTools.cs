using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;
using VsIdeBridgeService.Diagnostics;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string Severity = "severity";
    private const string WaitForIntellisense = "wait_for_intellisense";
    private const string Quick = "quick";
    private const string Max = "max";
    private const string Code = "code";
    private const string Project = "project";
    private const string Path = "path";
    private const string Text = "text";
    private const string GroupBy = "group_by";
    private const string WaitForIntellisenseHyphen = "wait-for-intellisense";
    private const string Configuration = "configuration";
    private const string Platform = "platform";
    private const string Diagnostics = "diagnostics";
    private const string Git = "git";
    private const string FileArg = "file";
    private const string Line = "line";
    private const string Column = "column";
    private const string Query = "query";
    private const string Documents = "documents";
    private const string Message = "message";
    private const string Paths = "paths";
    private const string PostCheck = "post_check";
    private const string Scope = "scope";
    private const string Search = "search";
    private const string Debug = "debug";
    private const string Python = "python";
    private const string Core = "core";
    private const string SystemCategory = "system";

    private static ToolEntry CreateErrorsTool()
    {
        ToolDefinition errorsDefinition = ToolDefinitionCatalog.Errors(
            ObjectSchema(
                Opt(Severity, "Optional severity filter."),
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness first (default true)."),
                OptBool(Quick, "Read current snapshot immediately (default false)."),
                OptInt(Max, "Max rows to return. Defaults to 50 when no filters are set."),
                Opt(Code, "Optional diagnostic code prefix filter."),
                Opt(Project, ProjectFilterDesc),
                Opt(Path, "Optional path filter."),
                Opt(Text, "Optional message text filter."),
                Opt(GroupBy, "Optional grouping mode.")));
        return new(errorsDefinition,
            async (id, args, bridge) =>
            {
                if (bridge.DocumentDiagnostics.TryGetCachedErrors(args, out JsonObject cachedErrors))
                {
                    return BridgeResult(cachedErrors);
                }

                bool hasFilters = args?[Code] is not null || args?[Severity] is not null
                    || args?[Project] is not null || args?[Path] is not null || args?[Text] is not null;
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : "50");

                bool useQuick = false;
                string? severityValue = OptionalString(args, Severity) ?? "Error";
                string errorArgs = Build(
                    (Severity, severityValue),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    BoolArg(Quick, args, Quick, useQuick, true),
                    (Max, maxValue),
                    (Code, OptionalString(args, Code)),
                    (Project, OptionalString(args, Project)),
                    (Path, OptionalString(args, Path)),
                    (Text, OptionalString(args, Text)),
                    ("group-by", OptionalString(args, GroupBy)));
                JsonObject response = await bridge.SendAsync(id, "errors", errorArgs)
                    .ConfigureAwait(false);
                if (response["Data"] is JsonObject errDataObj)
                {
                    int count = errDataObj["count"]?.GetValue<int>() ?? 0;
                    int totalCount = errDataObj["totalCount"]?.GetValue<int>() ?? count;
                    if (count < totalCount)
                        errDataObj["truncated"] = true;
                }
                return BridgeResult(response);
            });
    }

    private static ToolEntry CreateWarningsTool()
    {
        return new("warnings",
            "Capture warning rows with optional code/path/project filters.",
            ObjectSchema(
                Opt(Severity, "Optional severity filter."),
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness first (default true)."),
                OptBool(Quick, "Read current snapshot immediately (default false)."),
                OptInt(Max, "Max rows to return. Defaults to 50 when no filters are set."),
                Opt(Code, "Optional warning code prefix filter."),
                Opt(Project, ProjectFilterDesc),
                Opt(Path, "Optional path filter."),
                Opt(Text, "Optional message text filter."),
                Opt(GroupBy, "Optional grouping mode (e.g. code).")),
            "diagnostics",
            async (id, args, bridge) =>
            {
                if (bridge.DocumentDiagnostics.TryGetCachedWarnings(args, out JsonObject cachedWarnings))
                {
                    return BridgeResult(cachedWarnings);
                }

                bool hasFilters = args?[Code] is not null || args?[Severity] is not null
                    || args?[Project] is not null || args?[Path] is not null || args?[Text] is not null;
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : "50");

                bool useQuick = false;
                string warningArgs = Build(
                    (Severity, OptionalString(args, Severity)),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    BoolArg(Quick, args, Quick, useQuick, true),
                    (Max, maxValue),
                    (Code, OptionalString(args, Code)),
                    (Project, OptionalString(args, Project)),
                    (Path, OptionalString(args, Path)),
                    (Text, OptionalString(args, Text)),
                    ("group-by", OptionalString(args, GroupBy)));
                JsonObject response = await bridge.SendAsync(id, "warnings", warningArgs)
                    .ConfigureAwait(false);
                if (response["Data"] is JsonObject warnDataObj)
                {
                    int count = warnDataObj["count"]?.GetValue<int>() ?? 0;
                    int totalCount = warnDataObj["totalCount"]?.GetValue<int>() ?? count;
                    if (count < totalCount)
                        warnDataObj["truncated"] = true;
                }
                return BridgeResult(response);
            });
    }

    private static IEnumerable<ToolEntry> DiagnosticsTools()
    {
        yield return CreateErrorsTool();
        yield return CreateWarningsTool();

        // remaining tools...
        yield return BridgeTool("diagnostics_snapshot",
            "One-shot snapshot combining IDE state, build status, debugger mode, and error/warning counts. " +
            "Use at the start of a session or after a build instead of calling errors + vs_state separately.",
            ObjectSchema(OptBool(WaitForIntellisense, "Wait for IntelliSense readiness (default false).")),
            "diagnostics-snapshot",
            a => Build(BoolArg(WaitForIntellisenseHyphen, a, WaitForIntellisense, false, true)),
            Diagnostics);

        yield return BridgeTool("build_configurations",
            "List available solution build configurations and platforms.",
            EmptySchema(), "build-configurations", _ => Empty(), Diagnostics);

        yield return BridgeTool("set_build_configuration",
            "Activate one build configuration/platform pair.",
            ObjectSchema(
                Opt(Configuration, "Build configuration (e.g. Debug, Release)."),
                Opt(Platform, "Build platform (e.g. x64).")),
            "set-build-configuration",
            a => Build(
                (Configuration, OptionalString(a, Configuration)),
                (Platform, OptionalString(a, Platform))),
            Diagnostics);

        yield return new("build",
            "Build the solution or a specific project. Omit project to build the entire solution. Use list_projects to discover project names.",
            ObjectSchema(
                Opt("project", "Project name to build (e.g. VsIdeBridgeInstaller). Omit to build the entire solution."),
                Opt(Configuration, "Optional build configuration (e.g. Release)."),
                Opt(Platform, "Optional build platform (e.g. x64)."),
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before building (default true)."),
                OptBool("require_clean_diagnostics", "When false, bypasses the pre-build dirty-diagnostics guard (default false).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                JsonNode? preBuild = null;
                try
                {
                    if (!bridge.DocumentDiagnostics.TryGetCachedErrors(null, out JsonObject pre))
                    {
                        Task<JsonObject> preTask = bridge.SendAsync(id, "errors", "--quick");
                        if (await Task.WhenAny(preTask, Task.Delay(3_000)).ConfigureAwait(false) != preTask)
                        {
                            throw new OperationCanceledException("Pre-build snapshot timed out.");
                        }
                        pre = preTask.Result;
                    }

                    bool preSuccess = pre["Success"]?.GetValue<bool>() ?? false;
                    if (preSuccess)
                    {
                        int ec = pre["Data"]?["errorCount"]?.GetValue<int>() ?? 0;
                        int wc = pre["Data"]?["warningCount"]?.GetValue<int>() ?? 0;
                        int mc = pre["Data"]?["messageCount"]?.GetValue<int>() ?? 0;
                        if (ec > 0 || wc > 0 || mc > 0)
                        {
                            preBuild = pre;
                        }
                    }
                }
                catch
                {
                    // Best-effort pre-build snapshot — if it fails, proceed without it.
                }

                string buildArgs = Build(
                    ("project", OptionalString(args, "project")),
                    (Configuration, OptionalString(args, Configuration)),
                    (Platform, OptionalString(args, Platform)),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
                    BoolArg("require-clean-diagnostics", args, "require_clean_diagnostics", false, true));

                JsonObject response = await bridge.SendAsync(id, "build", buildArgs).ConfigureAwait(false);
                if (preBuild is not null)
                {
                    response["preBuildDiagnostics"] = preBuild;
                }

                return BridgeResult(response);
            });

        yield return new("build_errors",
            "Run MSBuild directly and return compiler errors as structured JSON. " +
            "Definitive build result — unaffected by IntelliSense state. " +
            "Use to verify a clean build without triggering the full VS build pipeline.",
            ObjectSchema(
                Opt(Configuration, "Build configuration (default Release)."),
                Opt("project", "Project name or path. Omit for the whole solution."),
                OptInt(Max, "Max error rows to return (default 20).")),
            Diagnostics,
            (id, args, bridge) => BuildErrorsTool.ExecuteAsync(id, args, bridge));
    }
}
