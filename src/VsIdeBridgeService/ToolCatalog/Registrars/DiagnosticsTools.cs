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
    private const string ErrorsOnly = "errors_only";
    private const string RequireCleanDiagnostics = "require_clean_diagnostics";
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

    private const string DefaultMaxRows = "50";
    private const int DefaultCompactDiagnosticsRows = 50;

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
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : DefaultMaxRows);

                bool useQuick = args?[Quick]?.GetValue<bool>() ?? false;
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
                CompactDiagnosticsResponse(response, args);
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
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : DefaultMaxRows);

                bool useQuick = args?[Quick]?.GetValue<bool>() ?? false;
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
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response);
            });
    }

    private static IEnumerable<ToolEntry> DiagnosticsTools() =>
        ErrorDiagnosticsTools()
            .Concat(BuildDiagnosticsTools());

    private static IEnumerable<ToolEntry> ErrorDiagnosticsTools()
    {
        yield return CreateErrorsTool();
        yield return CreateWarningsTool();

        yield return new("diagnostics_snapshot",
            "One-shot snapshot combining IDE state, build status, debugger mode, and error/warning counts. " +
            "Use at the start of a session or after a build instead of calling errors + vs_state separately. " +
            "With wait_for_intellisense=false it prefers the fast current snapshot; true is slower but fresher.",
            ObjectSchema(OptBool(WaitForIntellisense, "Wait for IntelliSense readiness (default false).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForIntellisense = args?[WaitForIntellisense]?.GetValue<bool>() ?? false;
                string snapshotArgs = Build(
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    (Quick, waitForIntellisense ? null : "true"));
                JsonObject response = await bridge.SendAsync(id, "diagnostics-snapshot", snapshotArgs)
                    .ConfigureAwait(false);
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response, args);
            });
    }

    private static void CompactDiagnosticsResponse(JsonObject response, JsonObject? args)
    {
        if (WantsFullDiagnosticsPayload(args) || response["Data"] is not JsonObject data)
        {
            return;
        }

        CompactDiagnosticsNode(data, DefaultCompactDiagnosticsRows);
    }

    private static bool WantsFullDiagnosticsPayload(JsonObject? args)
        => args?["verbose"]?.GetValue<bool?>() == true
            || args?["full"]?.GetValue<bool?>() == true;

    private static void CompactDiagnosticsNode(JsonNode? node, int maxRows)
    {
        switch (node)
        {
            case JsonObject obj:
                CompactDiagnosticsObject(obj, maxRows);
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    CompactDiagnosticsNode(item, maxRows);
                }
                break;
        }
    }

    private static void CompactDiagnosticsObject(JsonObject obj, int maxRows)
    {
        if (obj["rows"] is JsonArray rows)
        {
            int count = obj["count"]?.GetValue<int>() ?? rows.Count;
            int totalCount = obj["totalCount"]?.GetValue<int>() ?? count;
            bool truncated = count < totalCount || rows.Count > maxRows;

            if (rows.Count > maxRows)
            {
                JsonArray compactRows = [];
                for (int i = 0; i < maxRows; i++)
                {
                    compactRows.Add(rows[i]?.DeepClone());
                }

                obj["rows"] = compactRows;
                obj["count"] = maxRows;
            }

            if (truncated)
            {
                obj["truncated"] = true;
            }
        }

        foreach ((string _, JsonNode? child) in obj)
        {
            CompactDiagnosticsNode(child, maxRows);
        }
    }

    private static IEnumerable<ToolEntry> BuildDiagnosticsTools()
    {
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

        yield return CreateBuildTool(
            "build",
            "Build the solution or a specific project. Omit project to build the entire solution. Use list_projects to discover project names. Set errors_only=true to return the build summary plus only error rows.",
            "build",
            includeProject: true);

        yield return CreateBuildTool(
            "rebuild",
            "Rebuild the active solution inside Visual Studio. This performs a clean step before building and is heavier than build. Set errors_only=true to return the rebuild summary plus only error rows.",
            "rebuild",
            includeProject: false);

        yield return CreateBuildTool(
            "build_solution",
            "Build the active solution explicitly. Use this when you want the solution-wide build command rather than the generic build entry. Set errors_only=true to keep the response compact.",
            "build",
            includeProject: false);

        yield return CreateBuildTool(
            "rebuild_solution",
            "Rebuild the active solution explicitly. Use this when you want the solution-wide rebuild command by name. Set errors_only=true to keep the response compact.",
            "rebuild-solution",
            includeProject: false);

        yield return new("build_errors",
            "Run MSBuild directly and return compiler errors as structured JSON. " +
            "Definitive build result — unaffected by IntelliSense state. " +
            "Use to verify a clean build without triggering the full VS build pipeline. Expect this to be slower than normal bridge inspection tools. Use build/build_solution/rebuild/rebuild_solution with errors_only=true when you want the IDE build path but compact error output.",
            ObjectSchema(
                Opt(Configuration, "Build configuration (default Release)."),
                Opt("project", "Project name or path. Omit for the whole solution."),
                OptInt(Max, "Max error rows to return (default 20).")),
            Diagnostics,
            (id, args, bridge) => BuildErrorsTool.ExecuteAsync(id, args, bridge));
    }

    private static ToolEntry CreateBuildTool(string name, string description, string pipeCommand, bool includeProject)
    {
        List<(string Name, JsonObject Schema, bool Required)> properties = new List<(string Name, JsonObject Schema, bool Required)>();
        if (includeProject)
        {
            properties.Add(Opt(Project, "Project name to build (e.g. VsIdeBridgeInstaller). Omit to build the entire solution."));
        }

        properties.Add(Opt(Configuration, "Optional build configuration (e.g. Release)."));
        properties.Add(Opt(Platform, "Optional build platform (e.g. x64)."));
        properties.Add(OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before building (default true)."));
        properties.Add(OptBool(RequireCleanDiagnostics, "When false, bypasses the pre-build dirty-diagnostics guard (default false)."));
        properties.Add(OptBool(ErrorsOnly, "When true, return the build summary plus only error rows so warnings and messages do not flood the response."));
        properties.Add(OptInt(Max, "Max error rows to return when errors_only is true (default 50)."));

        return new(name,
            description,
            ObjectSchema(properties.ToArray()),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool errorsOnly = args?[ErrorsOnly]?.GetValue<bool>() ?? false;
                JsonObject? preBuild = errorsOnly ? null : await TryCapturePreBuildDiagnosticsAsync(id, bridge).ConfigureAwait(false);

                string buildArgs = Build(
                    (Project, includeProject ? OptionalString(args, Project) : null),
                    (Configuration, OptionalString(args, Configuration)),
                    (Platform, OptionalString(args, Platform)),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
                    BoolArg("require-clean-diagnostics", args, RequireCleanDiagnostics, false, true));

                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs).ConfigureAwait(false);

                if (errorsOnly)
                {
                    JsonObject? errorDiagnostics = await TryCaptureErrorDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                    if (errorDiagnostics is not null)
                    {
                        response["errorDiagnostics"] = errorDiagnostics;
                    }

                    response["errorsOnly"] = true;
                }
                else if (preBuild is not null)
                {
                    response["preBuildDiagnostics"] = preBuild;
                }

                return BridgeResult(response);
            });
    }

    private static async Task<JsonObject?> TryCapturePreBuildDiagnosticsAsync(JsonNode? id, BridgeConnection bridge)
    {
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
            if (!preSuccess)
            {
                return null;
            }

            int errorCount = pre["Data"]?["errorCount"]?.GetValue<int>() ?? 0;
            int warningCount = pre["Data"]?["warningCount"]?.GetValue<int>() ?? 0;
            int messageCount = pre["Data"]?["messageCount"]?.GetValue<int>() ?? 0;
            return errorCount > 0 || warningCount > 0 || messageCount > 0 ? pre : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonObject?> TryCaptureErrorDiagnosticsAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        try
        {
            string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : DefaultMaxRows;
            return await bridge.SendAsync(
                id,
                "errors",
                Build(
                    (Severity, "Error"),
                    (Quick, "true"),
                    (WaitForIntellisenseHyphen, "false"),
                    (Max, maxValue))).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
