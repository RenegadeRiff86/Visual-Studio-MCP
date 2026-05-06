using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Diagnostics;
using VsIdeBridge.Shared;
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
    private const string ChunkSize = "chunk_size";
    private const string SortBy = "sort_by";
    private const string SortDirection = "sort_direction";
    private const string WaitForCompletion = "wait_for_completion";
    private const string WaitForIntellisenseHyphen = "wait-for-intellisense";
    private const string WaitForCompletionHyphen = "wait-for-completion";
    private const string Configuration = "configuration";
    private const string Platform = "platform";
    private const string ErrorsOnly = "errors_only";
    private const string Warnings = "warnings";
    private const string RequireCleanDiagnostics = "require_clean_diagnostics";
    private const string Diagnostics = "diagnostics";
    private const string Git = "git";
    private const string FileArg = "file";
    private const string Line = "line";
    private const string BuildSolutionTool = "build_solution";
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
    private const string MessagesTool = "messages";
    private const string DiagnosticsSnapshotCommand = "diagnostics-snapshot";
    private const string TailLines = "tail_lines";
    private const string ChunkLines = "chunk_lines";
    private const string ChunkIndex = "chunk_index";
    private const string IncludeChunks = "include_chunks";
    private const string MaxChars = "max_chars";
    private const string Pane = "pane";
    private const string Activate = "activate";

    private const string DefaultMaxRows = "10";
    private const int DefaultCompactDiagnosticsRows = 10;
    private const int DefaultCompactDiagnosticsStateItems = 10;
    private const string Refresh = "refresh";
    private const string ResponseWarningsProperty = "Warnings";
    private const string PassiveDiagnosticsReadDescription = "Read the current passive diagnostics snapshot immediately. This may be stale relative to the live Error List.";
    private const string RefreshDiagnosticsDescription = "Force the Error List to refresh before reading when you need a fresh UI read (default false).";
    private const string PassiveSnapshotStaleWarning = "Using the passive diagnostics snapshot. This list may be stale relative to the current Visual Studio Error List. Use refresh=true for a fresh UI read.";
    private const string TimedOutDirectReadWarning = "The direct Error List read timed out, so the bridge fell back to diagnostics_snapshot instead of failing outright.";
    private const string QuickFallbackStaleWarning = "The live Error List read was interrupted, so the bridge fell back to a quick diagnostics read. This result may be slightly stale relative to the current UI.";
    private const string SuppressedWarningCode = "BP1044";
    private const int SuppressionPromptUserThreshold = 10;

    private static ToolEntry CreateErrorsTool()
        => new DiagnosticRowsToolView(
            "errors",
            ToolDefinitionCatalog.Errors,
            cacheSeverity: "Error",
            defaultSeverity: "Error",
            codeFilterDescription: "Optional diagnostic code prefix filter.",
            groupByDescription: "Optional grouping mode.",
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file containing the error"), ("goto_definition", "Navigate to the error location"), ("apply_diff", "Fix the error")],
                related: [(Warnings, "Check warnings instead"), ("diagnostics_snapshot", "Get a combined IDE + error snapshot"), ("build_errors", "Run MSBuild directly for a definitive build result")]))
        .Create();

    private static ToolEntry CreateWarningsTool()
        => new DiagnosticRowsToolView(
            Warnings,
            ToolDefinitionCatalog.Warnings,
            cacheSeverity: "Warning",
            defaultSeverity: "Warning",
            codeFilterDescription: "Optional warning code prefix filter.",
            groupByDescription: "Optional grouping mode (e.g. code).",
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file with the warning"), ("goto_definition", "Navigate to the warning location")],
                related: [("errors", "Check errors instead"), ("diagnostics_snapshot", "Get a combined IDE + diagnostics snapshot")]))
        .Create();

    private static ToolEntry CreateMessagesTool()
        => new DiagnosticRowsToolView(
            MessagesTool,
            ToolDefinitionCatalog.Messages,
            cacheSeverity: "Message",
            defaultSeverity: "Message",
            codeFilterDescription: "Optional message code prefix filter.",
            groupByDescription: "Optional grouping mode (e.g. code).",
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file behind the message"), ("goto_definition", "Navigate to the message location")],
                related: [(Warnings, "Check warnings instead"), ("errors", "Check errors instead"), ("diagnostics_snapshot", "Get a combined IDE + diagnostics snapshot")]))
        .Create();

    private static JsonObject DiagnosticRowsSchema(string codeFilterDescription, string groupByDescription)
        => ObjectSchema(
            Opt(Severity, "Optional severity filter."),
            OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before a live filtered or refresh read (default false)."),
            OptBool(Quick, PassiveDiagnosticsReadDescription),
            OptBool(Refresh, RefreshDiagnosticsDescription),
            OptInt(Max, "Legacy alias for chunk_size. Defaults to 10 when chunk_size is omitted."),
            OptInt(ChunkSize, "Rows per returned chunk (default 10, or max when set). Set 0 to return all filtered rows."),
            OptInt(ChunkIndex, "Zero-based row chunk index to return (default 0)."),
            Opt(SortBy, "Optional row sort field: severity, code, project, file/path, line, column, message, source, or tool."),
            Opt(SortDirection, "Optional sort direction: asc or desc (default asc)."),
            Opt(Code, codeFilterDescription),
            Opt(Project, ProjectFilterDesc),
            Opt(Path, "Optional path filter."),
            Opt(Text, "Optional message text filter."),
            Opt(GroupBy, groupByDescription));

    private sealed class DiagnosticRowsToolView(
        string commandName,
        Func<JsonObject, ToolDefinition> definitionFactory,
        string cacheSeverity,
        string? defaultSeverity,
        string codeFilterDescription,
        string groupByDescription,
        JsonObject searchHints)
    {
        public ToolEntry Create()
        {
            ToolDefinition definition = definitionFactory(DiagnosticRowsSchema(codeFilterDescription, groupByDescription))
                .WithSearchHints(searchHints);
            return new(definition, HandleAsync);
        }

        private async Task<JsonNode> HandleAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
        {
            if (bridge.DocumentDiagnostics.TryGetCached(cacheSeverity, args, out JsonObject cachedDiagnostics))
            {
                CompactDiagnosticsResponse(cachedDiagnostics, args);
                return BridgeResult(cachedDiagnostics);
            }

            string diagnosticsArgs = Build(
                (Severity, OptionalString(args, Severity) ?? defaultSeverity),
                BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                BoolArg(Quick, args, Quick, ShouldUsePassiveDiagnosticsRead(args), true),
                BoolArg(Refresh, args, Refresh, false, true),
                (Max, GetBridgeDiagnosticsMax(args)),
                (Code, OptionalString(args, Code)),
                (Project, OptionalString(args, Project)),
                (Path, OptionalString(args, Path)),
                (Text, OptionalString(args, Text)),
                ("group-by", OptionalString(args, GroupBy)));
            JsonObject response = await SendDiagnosticsCommandWithSnapshotFallbackAsync(
                    bridge,
                    id,
                    commandName,
                    diagnosticsArgs,
                    args)
                .ConfigureAwait(false);
            CompactDiagnosticsResponse(response, args);
            return BridgeResult(response);
        }
    }

    private static IEnumerable<ToolEntry> DiagnosticsTools() =>
        ErrorDiagnosticsTools()
            .Concat(BuildDiagnosticsTools());

    private static IEnumerable<ToolEntry> ErrorDiagnosticsTools()
    {
        yield return CreateErrorsTool();
        yield return CreateWarningsTool();
        yield return CreateMessagesTool();
        yield return CreateReadOutputTool();

        yield return new("diagnostics_snapshot",
            "One-shot snapshot combining IDE state, build status, debugger mode, and error/warning counts. " +
            "Use at the start of a session or after a build instead of calling errors + vs_state separately. " +
            "This is a passive snapshot and may be stale relative to the current Error List. " +
            "With wait_for_intellisense=false it prefers the fast current snapshot; true is slower but fresher.",
            ObjectSchema(
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness (default false)."),
                OptInt(Max, "Legacy alias for chunk_size. Defaults to 10 when chunk_size is omitted."),
                OptInt(ChunkSize, "Rows per diagnostics bucket chunk (default 10, or max when set). Set 0 to return all filtered rows."),
                OptInt(ChunkIndex, "Zero-based row chunk index to return from each diagnostics bucket (default 0)."),
                Opt(SortBy, "Optional row sort field: severity, code, project, file/path, line, column, message, source, or tool."),
                Opt(SortDirection, "Optional sort direction: asc or desc (default asc).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForIntellisense = args?[WaitForIntellisense]?.GetValue<bool>() ?? false;
                string snapshotArgs = Build(
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    (Quick, waitForIntellisense ? null : "true"));
                JsonObject response = await bridge.SendAsync(id, DiagnosticsSnapshotCommand, snapshotArgs)
                    .ConfigureAwait(false);
                AddPassiveSnapshotWarning(response);
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response, args);
            },
            searchHints: BuildSearchHints(
                related: [("errors", "Get only errors"), (Warnings, "Get only warnings"), ("vs_state", "Check IDE state"), ("build", "Trigger a build")]));
    }

    private static ToolEntry CreateReadOutputTool()
    {
        return BridgeTool("read_output",
            "Read text from a Visual Studio Output window pane such as Build or IDE Bridge. " +
            "Omit pane to read the active Output pane; provide pane to select by name or GUID. " +
            "The selected chunk defaults to the last chunk while chunk metadata keeps the whole output addressable.",
            ObjectSchema(
                Opt(Pane, "Optional Output pane name or GUID. Omit to read the active pane, falling back to Build."),
                OptInt(ChunkLines, "Lines per output chunk (default 200). Set 0 to keep the whole pane as one chunk."),
                OptInt(ChunkIndex, "Zero-based chunk index to return. Omit to return the last chunk."),
                OptBool(IncludeChunks, "Include text for every chunk in the chunks array (default false). By default chunks contain metadata only."),
                OptInt(TailLines, "Legacy alias for chunk_lines."),
                OptInt(MaxChars, "Maximum characters to return from the selected chunk (default 120000). Set 0 for no character cap."),
                OptBool(Activate, "Activate the selected Output pane before reading (default false).")),
            "read-output",
            args => Build(
                (Pane, OptionalString(args, Pane)),
                ("chunk-lines", OptionalText(args, ChunkLines) ?? OptionalText(args, TailLines)),
                ("chunk-index", OptionalText(args, ChunkIndex)),
                ("max-chars", OptionalText(args, MaxChars)),
                BoolArg("include-chunks", args, IncludeChunks, false, false),
                BoolArg(Activate, args, Activate, false, false)),
            category: Diagnostics,
            aliases: ["read_output_window", "output_window"],
            summary: "Read a Visual Studio Output window pane.",
            readOnly: true,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [("build", "Run or start a build before reading Build output"), ("errors", "Check structured Error List diagnostics")],
                related: [("warnings", "Review warning rows"), ("messages", "Review message rows"), ("vs_state", "Check the bound IDE instance")]));
    }

    private static void CompactDiagnosticsResponse(JsonObject response, JsonObject? args)
    {
        if (WantsFullDiagnosticsPayload(args) || response["Data"] is not JsonObject data)
        {
            return;
        }

        int suppressionWarningCount = CountDiagnosticCode(data, SuppressedWarningCode);
        DiagnosticQueryOptions paging = CreateDiagnosticQueryOptions(args);
        CompactDiagnosticsNode(data, paging);
        AddSuppressionRepairPrompt(response, suppressionWarningCount);
    }

    private static void AddSuppressionRepairPrompt(JsonObject response, int suppressionWarningCount)
    {
        if (suppressionWarningCount <= 0)
        {
            return;
        }

        string summaryPrompt = suppressionWarningCount > SuppressionPromptUserThreshold
            ? $"User action: ask before fixing {suppressionWarningCount} in-source warning suppressions ({SuppressedWarningCode})."
            : $"Model action: fix {suppressionWarningCount} in-source warning suppression(s) ({SuppressedWarningCode}) now instead of editing around them.";

        string detailPrompt = suppressionWarningCount > SuppressionPromptUserThreshold
            ? $"Detected {suppressionWarningCount} in-source warning suppressions ({SuppressedWarningCode}). Ask the user before making a broad suppression cleanup pass, then remove them in focused batches and fix the underlying warnings instead of keeping the suppressions."
            : $"Detected {suppressionWarningCount} in-source warning suppression(s) ({SuppressedWarningCode}). Fix them now by removing the suppressions and addressing the underlying warnings instead of editing around them.";

        AppendResponseWarning(response, detailPrompt);
        AppendSummaryPrompt(response, summaryPrompt);
    }

    private static void AppendResponseWarning(JsonObject response, string warningText)
    {
        JsonArray warnings = response[ResponseWarningsProperty] as JsonArray is { } existingWarnings
            ? (JsonArray)existingWarnings.DeepClone()
            : [];

        if (!warnings.Any(node => string.Equals(node?.GetValue<string>(), warningText, StringComparison.Ordinal)))
        {
            warnings.Add(warningText);
        }

        response[ResponseWarningsProperty] = warnings;
    }

    private static void AppendSummaryPrompt(JsonObject response, string promptText)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(summary))
        {
            response["Summary"] = promptText;
            return;
        }

        if (!summary.Contains(promptText, StringComparison.Ordinal))
        {
            response["Summary"] = string.Concat(summary, " ", promptText);
        }
    }

    private static int CountDiagnosticCode(JsonNode? node, string code)
    {
        return node switch
        {
            JsonObject obj => CountDiagnosticCode(obj, code),
            JsonArray arr => arr.Sum(item => CountDiagnosticCode(item, code)),
            _ => 0,
        };
    }

    private static int CountDiagnosticCode(JsonObject obj, string code)
    {
        int count = string.Equals(obj[Code]?.GetValue<string>(), code, StringComparison.Ordinal)
            ? 1
            : 0;

        foreach ((string _, JsonNode? child) in obj)
        {
            count += CountDiagnosticCode(child, code);
        }

        return count;
    }

    private static bool WantsFullDiagnosticsPayload(JsonObject? args)
        => args?["verbose"]?.GetValue<bool?>() == true
            || args?["full"]?.GetValue<bool?>() == true;

    private static void CompactDiagnosticsNode(JsonNode? node, DiagnosticQueryOptions paging)
    {
        switch (node)
        {
            case JsonObject obj:
                CompactDiagnosticsObject(obj, paging);
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    CompactDiagnosticsNode(item, paging);
                }
                break;
        }
    }

    private static async Task<JsonObject> SendDiagnosticsCommandWithSnapshotFallbackAsync(
        BridgeConnection bridge,
        JsonNode? id,
        string command,
        string argsText,
        JsonObject? args)
    {
        JsonObject response;
        try
        {
            response = await bridge.SendAsync(id, command, argsText).ConfigureAwait(false);
        }
        catch (McpRequestException ex) when (IsInterruptedDiagnosticsException(ex))
        {
            JsonObject? quickFallbackFromException = await TryGetQuickDiagnosticsFallbackAsync(bridge, id, command, args)
                .ConfigureAwait(false);
            if (quickFallbackFromException is not null)
            {
                return quickFallbackFromException;
            }

            JsonObject recoverySnapshotResponse = await bridge.SendAsync(id, DiagnosticsSnapshotCommand, BuildDiagnosticsSnapshotArgs(args)).ConfigureAwait(false);
            JsonObject? fallbackFromException = CreateDiagnosticsResultFromSnapshot(recoverySnapshotResponse, command, interruptedDirectRead: true);
            if (fallbackFromException is not null)
            {
                return fallbackFromException;
            }

            throw;
        }

        if (!IsInterruptedDiagnosticsResponse(response))
        {
            return response;
        }

        JsonObject? quickFallback = await TryGetQuickDiagnosticsFallbackAsync(bridge, id, command, args)
            .ConfigureAwait(false);
        if (quickFallback is not null)
        {
            return quickFallback;
        }

        JsonObject fallbackSnapshotResponse = await bridge.SendAsync(id, DiagnosticsSnapshotCommand, BuildDiagnosticsSnapshotArgs(args)).ConfigureAwait(false);
        JsonObject? fallback = CreateDiagnosticsResultFromSnapshot(fallbackSnapshotResponse, command, interruptedDirectRead: true);
        return fallback ?? response;
    }

    private static bool ShouldUsePassiveDiagnosticsRead(JsonObject? args)
    {
        if (args?[Quick] is JsonNode quickNode)
        {
            return quickNode.GetValue<bool>();
        }

        return args?[Refresh]?.GetValue<bool>() != true;
    }

    private static async Task<JsonObject?> TryGetQuickDiagnosticsFallbackAsync(
        BridgeConnection bridge,
        JsonNode? id,
        string command,
        JsonObject? args)
    {
        try
        {
            JsonObject quickResponse = await bridge.SendAsync(id, command, BuildQuickDiagnosticsArgs(args)).ConfigureAwait(false);
            if (quickResponse["Success"]?.GetValue<bool>() != true)
            {
                return null;
            }

            JsonArray warnings = quickResponse[ResponseWarningsProperty] as JsonArray is { } existingWarnings
                ? (JsonArray)existingWarnings.DeepClone()
                : [];
            warnings.Add($"Fell back to a quick '{command}' read after the direct live read was interrupted.");
            warnings.Add(QuickFallbackStaleWarning);
            quickResponse[ResponseWarningsProperty] = warnings;

            JsonObject data = quickResponse["Data"] as JsonObject ?? [];
            data["Cache"] = new JsonObject
            {
                ["source"] = "quick-direct-fallback",
                ["kind"] = command,
                ["mayBeStale"] = true,
                ["capturedAtUtc"] = quickResponse[FinishedAtUtcProperty]?.DeepClone(),
            };
            quickResponse["Data"] = data;
            return quickResponse;
        }
        catch (McpRequestException ex) when (IsInterruptedDiagnosticsException(ex))
        {
            return null;
        }
    }

    private static bool IsInterruptedDiagnosticsResponse(JsonObject response)
    {
        if (response["Success"]?.GetValue<bool>() == true)
        {
            return false;
        }

        string? summary = response["Summary"]?.GetValue<string>();
        if (string.Equals(summary, "The operation was canceled.", StringComparison.Ordinal))
        {
            return true;
        }

        string? errorMessage = response["Error"]?["message"]?.GetValue<string>();
        return string.Equals(errorMessage, "Bridge server interrupted: The operation was canceled.", StringComparison.Ordinal)
            || IsTimedOutDiagnosticsMessage(errorMessage)
            || IsTimedOutDiagnosticsMessage(summary);
    }

    private static bool IsInterruptedDiagnosticsException(McpRequestException ex)
        => string.Equals(ex.Message, "Bridge server interrupted: The operation was canceled.", StringComparison.Ordinal)
            || string.Equals(ex.Message, "The operation was canceled.", StringComparison.Ordinal)
            || IsTimedOutDiagnosticsMessage(ex.Message);

    private static bool IsTimedOutDiagnosticsMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
            && (message.Contains("Timed out waiting for VS bridge response", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Visual Studio may be blocked", StringComparison.OrdinalIgnoreCase));

    private static string BuildQuickDiagnosticsArgs(JsonObject? args)
        => Build(
            (Severity, OptionalString(args, Severity)),
            (Code, OptionalString(args, Code)),
            (Project, OptionalString(args, Project)),
            (Path, OptionalString(args, Path)),
            (Text, OptionalString(args, Text)),
            ("group-by", OptionalString(args, GroupBy)),
            (Max, GetBridgeDiagnosticsMax(args)),
            (Quick, "true"),
            (Refresh, "false"),
            (WaitForIntellisenseHyphen, "false"));

    private static string BuildDiagnosticsSnapshotArgs(JsonObject? args)
    {
        bool waitForIntellisense = args?[WaitForIntellisense]?.GetValue<bool>() ?? false;
        return Build(
            BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
            (Quick, waitForIntellisense ? null : "true"));
    }

    private static JsonObject? CreateDiagnosticsResultFromSnapshot(JsonObject snapshotResponse, string command, bool interruptedDirectRead)
    {
        if (snapshotResponse["Success"]?.GetValue<bool>() != true || snapshotResponse["Data"] is not JsonObject snapshotData)
        {
            return null;
        }

        string bucketName = command switch
        {
            "errors" => "errors",
            "warnings" => "warnings",
            MessagesTool => MessagesTool,
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(bucketName) || snapshotData[bucketName] is not JsonObject bucket)
        {
            return null;
        }

        JsonArray warnings = snapshotResponse[ResponseWarningsProperty] as JsonArray is { } existingWarnings
            ? (JsonArray)existingWarnings.DeepClone()
            : [];
        warnings.Add(PassiveSnapshotStaleWarning);
        if (interruptedDirectRead)
        {
            warnings.Add($"Fell back to diagnostics_snapshot after the direct '{command}' read was interrupted.");
            warnings.Add(TimedOutDirectReadWarning);
        }

        int count = bucket["count"]?.GetValue<int>() ?? 0;
        string itemLabel = command switch
        {
            "errors" => "Error List row(s)",
            "warnings" => "warning row(s)",
            "messages" => "message row(s)",
            _ => "row(s)",
        };

        return new JsonObject
        {
            ["SchemaVersion"] = snapshotResponse["SchemaVersion"]?.DeepClone(),
            ["Command"] = command,
            ["RequestId"] = snapshotResponse["RequestId"]?.DeepClone(),
            ["Success"] = true,
            ["StartedAtUtc"] = snapshotResponse["StartedAtUtc"]?.DeepClone(),
            [FinishedAtUtcProperty] = snapshotResponse[FinishedAtUtcProperty]?.DeepClone(),
            ["Summary"] = $"Captured {count} {itemLabel}.",
            [ResponseWarningsProperty] = warnings,
            ["Error"] = null,
            ["Data"] = bucket.DeepClone(),
            ["Cache"] = BuildPassiveSnapshotCache(command, snapshotResponse, snapshotData),
        };
    }

    private static void AddPassiveSnapshotWarning(JsonObject response)
    {
        JsonArray warnings = response[ResponseWarningsProperty] as JsonArray is { } existingWarnings
            ? (JsonArray)existingWarnings.DeepClone()
            : [];
        warnings.Add(PassiveSnapshotStaleWarning);
        response[ResponseWarningsProperty] = warnings;
    }

    private static JsonObject BuildPassiveSnapshotCache(string command, JsonObject snapshotResponse, JsonObject snapshotData)
    {
        JsonObject cache = new()
        {
            ["source"] = "diagnostics-snapshot",
            ["kind"] = command,
            ["mayBeStale"] = true,
        };

        JsonNode? capturedAtUtc = snapshotData["lastCompletedUtc"]?.DeepClone()
            ?? snapshotResponse[FinishedAtUtcProperty]?.DeepClone();
        if (capturedAtUtc is not null)
        {
            cache["capturedAtUtc"] = capturedAtUtc;
        }

        return cache;
    }

    private static void CompactDiagnosticsObject(JsonObject obj, DiagnosticQueryOptions paging)
    {
        if (obj["rows"] is JsonArray)
        {
            DiagnosticCollection.FromJsonObject(obj).WriteTo(obj, paging);
        }

        CompactPreviewArray(obj, "openDocuments", "openDocumentCount", DefaultCompactDiagnosticsStateItems);
        CompactPreviewArray(obj, "documents", "documentCount", DefaultCompactDiagnosticsStateItems);

        foreach ((string _, JsonNode? child) in obj)
        {
            CompactDiagnosticsNode(child, paging);
        }
    }

    private static void CompactPreviewArray(JsonObject obj, string propertyName, string totalCountPropertyName, int maxItems)
    {
        if (obj[propertyName] is not JsonArray items)
        {
            return;
        }

        int originalCount = items.Count;
        if (originalCount == 0)
        {
            obj[totalCountPropertyName] ??= 0;
            return;
        }

        obj[totalCountPropertyName] ??= originalCount;
        if (originalCount <= maxItems)
        {
            return;
        }

        JsonArray compactItems = [];
        for (int i = 0; i < maxItems; i++)
        {
            compactItems.Add(items[i]?.DeepClone());
        }

        obj[propertyName] = compactItems;
        obj[$"{propertyName}Truncated"] = true;
    }

    private static IEnumerable<ToolEntry> BuildDiagnosticsTools()
        => BuildConfigurationTools()
            .Concat(BuildExecutionTools())
            .Append(CreateRunCodeAnalysisTool());

    private static IEnumerable<ToolEntry> BuildConfigurationTools()
    {
        yield return BridgeTool("build_configurations",
            "List available solution build configurations and platforms.",
            EmptySchema(), "build-configurations", _ => Empty(), Diagnostics,
            searchHints: BuildSearchHints(
                related: [("build", "Trigger a build"), ("set_build_configuration", "Activate a configuration")]));

        yield return BridgeTool("set_build_configuration",
            "Activate one build configuration/platform pair.",
            ObjectSchema(
                Opt(Configuration, "Build configuration (e.g. Debug, Release)."),
                Opt(Platform, "Build platform (e.g. x64).")),
            "set-build-configuration",
            a => Build(
                (Configuration, OptionalString(a, Configuration)),
                (Platform, OptionalString(a, Platform))),
            Diagnostics,
            searchHints: BuildSearchHints(
                workflow: [("build", "Build with the new configuration")],
                related: [("build_configurations", "List available configurations")]));
    }

    private static IEnumerable<ToolEntry> BuildExecutionTools()
    {

        yield return CreateBuildTool(
            "build",
            "Build the solution or a specific project. Omit project to build the entire solution. Use list_projects to discover project names. Set errors_only=true to return the build summary plus only error rows.",
            "build",
            includeProject: true,
            defaultWaitForCompletion: true,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after building"), ("build_errors", "Run MSBuild directly for a definitive result")],
                related: [("rebuild", "Clean then build"), (BuildSolutionTool, "Build the solution explicitly")]));

        yield return CreateBuildTool(
            "rebuild",
            "Rebuild the active solution inside Visual Studio. This performs a clean step before building and is heavier than build. By default it starts in the background and returns immediately. Set wait_for_completion=true to wait for completion. Set errors_only=true only when waiting.",
            "rebuild",
            includeProject: false,
            defaultWaitForCompletion: false,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after rebuilding")],
                related: [("build", "Build without cleaning"), ("rebuild_solution", "Rebuild the solution explicitly")]));

        yield return CreateBuildTool(
             BuildSolutionTool,
            "Build the active solution explicitly. Use this when you want the solution-wide build command rather than the generic build entry. By default it starts in the background and returns immediately so large solution builds do not block the bridge. Set wait_for_completion=true to wait for completion. Set errors_only=true only when waiting.",
            "build",
            includeProject: false,
            defaultWaitForCompletion: false,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after the user reports the build finished")],
                related: [("build", "Build a specific project"), ("rebuild_solution", "Rebuild the solution")]));

        yield return CreateBuildTool(
            "rebuild_solution",
            "Rebuild the active solution explicitly. Use this when you want the solution-wide rebuild command by name. By default it starts in the background and returns immediately. Set wait_for_completion=true to wait for completion. Set errors_only=true only when waiting.",
            "rebuild-solution",
            includeProject: false,
            defaultWaitForCompletion: false,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after rebuilding")],
                related: [("rebuild", "Generic rebuild"), (BuildSolutionTool, "Build without cleaning")]));

        yield return new("build_errors",
            "Build the active solution through Visual Studio and return only compiler errors as structured JSON. " +
            "Equivalent to build_solution with errors_only=true. " +
            "Use build/build_solution for the full build response including warnings and messages.",
            ObjectSchema(
                Opt(Project, "Project name to build (e.g. VsIdeBridgeInstaller). Omit to build the entire solution."),
                Opt(Configuration, "Optional build configuration (e.g. Release)."),
                Opt(Platform, "Optional build platform (e.g. x64)."),
                OptBool(RequireCleanDiagnostics, "When false, bypasses the pre-build dirty-diagnostics guard (default true)."),
                OptInt(Max, "Max error rows to return (default 20).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                string buildArgs = Build(
                    (Project, OptionalString(args, Project)),
                    (Configuration, OptionalString(args, Configuration)),
                    (Platform, OptionalString(args, Platform)),
                    (WaitForCompletionHyphen, "true"),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
                    BoolArg("require-clean-diagnostics", args, RequireCleanDiagnostics, true, true));

                JsonObject response = await bridge.SendAsync(id, "build", buildArgs).ConfigureAwait(false);

                JsonObject diagnosticsSnapshot = await bridge.DocumentDiagnostics
                    .QueueRefreshAndWaitForSnapshotAsync("build-errors-post-build", clearCached: true)
                    .ConfigureAwait(false);
                response["diagnosticsSnapshot"] = diagnosticsSnapshot;

                JsonObject? errorDiagnostics = await TryCaptureErrorDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                if (errorDiagnostics is not null)
                {
                    response["errorDiagnostics"] = errorDiagnostics;
                }
                response["errorsOnly"] = true;
                return BridgeResult(response);
            },
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file with the build error"), ("goto_definition", "Navigate to the error location"), ("apply_diff", "Fix the error")],
                related: [("errors", "Check IDE error list instead"), (BuildSolutionTool, "Build solution with full output")]));
    }

    private static ToolEntry CreateRunCodeAnalysisTool()
    {
        const string TimeoutMs = "timeout_ms";
        return new("run_code_analysis",
            "Run VS code analysis on the solution using the SDK build infrastructure. " +
            "By default this starts analysis and returns immediately so large solutions do not block the MCP call. " +
            "Set wait_for_completion=true to wait for completion and return a paged slice of the Error List.",
            ObjectSchema(
                OptInt(TimeoutMs, "Timeout in milliseconds (default 300000)."),
                OptBool(WaitForCompletion, "When false, start analysis and return immediately (default false). Set true to wait for completion and capture diagnostics."),
                OptInt(Max, "Max diagnostic rows to return in this response (default 50). Call errors to fetch further rows.")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForCompletion = OptionalBool(args, WaitForCompletion, false);
                string analysisArgs = Build(
                    ("timeout-ms", OptionalText(args, TimeoutMs)),
                    BoolArg(WaitForCompletionHyphen, args, WaitForCompletion, false, true));
                JsonObject response = await bridge.SendAsync(id, "run-code-analysis", analysisArgs).ConfigureAwait(false);
                if (!waitForCompletion)
                {
                    return BridgeResult(response);
                }

                JsonObject diagnosticsSnapshot = await bridge.DocumentDiagnostics
                    .QueueRefreshAndWaitForSnapshotAsync("run-code-analysis-post-build", clearCached: true)
                    .ConfigureAwait(false);
                response["diagnosticsSnapshot"] = diagnosticsSnapshot;

                JsonObject? diagnostics = await TryCaptureAnalysisDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                if (diagnostics is not null)
                {
                    response["diagnostics"] = diagnostics;
                }
                return BridgeResult(response);
            },
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file with the analysis finding")],
                related: [(BuildSolutionTool, "Build instead of analysing"), ("build_errors", "Build and return errors only")]));
    }

    private static async Task<JsonObject?> TryCaptureAnalysisDiagnosticsAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        try
        {
            string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : "50";
            return await bridge.SendAsync(
                id,
                "errors",
                Build(
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
