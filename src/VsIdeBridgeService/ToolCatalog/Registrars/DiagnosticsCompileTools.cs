using System.Text.Json.Nodes;

using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

// Compile-file and code-analysis tools, split from DiagnosticsTools.cs to keep file sizes manageable.
internal static partial class ToolCatalog
{
    private static ToolEntry CreateCompileFileTool()
        => new(CompileFileTool,
            "Compile a single source file through Visual Studio's Build.Compile command. " +
            "Use this for C/C++ file-level compiles, similar to selecting Compile or pressing Ctrl+F7 in Solution Explorer. " +
            "By default waits for the compile to finish and captures a fresh diagnostics snapshot inline. " +
            "Set wait_for_completion=false to fire and return immediately without waiting. " +
            "Headers are not compiled directly by Visual Studio; pass the owning .cpp/.c/.cxx source file or use build/build_errors for the project. " +
            "Requires the debugger to be stopped — if the debugger is active (debugMode is not dbgDesignMode), this tool returns an error; call debug_stop first. " +
            "For several files, use the batch tool with one compile_file step per file, set max_steps under 5, and avoid parallel compile_file calls so each result stays visible.",
            ObjectSchema(
                Req(FileArg, "File path or bridge handle to compile. Prefer handles returned by find_files, find_text, or read_file."),
                OptInt(Line, "Optional 1-based line used when activating the file before compiling (default 1)."),
                OptInt(Column, "Optional 1-based column used when activating the file before compiling (default 1)."),
                OptBool(WaitForCompletion, "Wait for the compile to finish and capture a fresh diagnostics snapshot (default true). Set false to fire and return immediately."),
                OptBool(ErrorsOnly, "When true and wait_for_completion is true, error rows are captured inline so you can read results without a separate errors call (default false)."),
                OptInt(Max, $"Max error rows when errors_only is true (default {DefaultMaxRows}).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                JsonObject stateResponse = await bridge.SendAsync(id, "state", "").ConfigureAwait(false);
                string? debugMode = stateResponse["Data"]?["debugMode"]?.GetValue<string>();
                if (!string.Equals(debugMode, "dbgDesignMode", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeException(
                        $"Debugger is active (mode: {debugMode ?? "unknown"}). " +
                        "Visual Studio shows a 'Stop debugging?' dialog when Build.Compile runs while the debugger is active — the bridge cannot dismiss it. " +
                        "Call debug_stop first to stop the debugger, then retry compile_file.");
                }

                bool waitForCompletion = OptionalBool(args, WaitForCompletion, true);
                string executeArgs = Build(
                    ("command", "Build.Compile"),
                    (FileArg, OptionalString(args, FileArg)),
                    (Line, OptionalText(args, Line) ?? "1"),
                    (Column, OptionalText(args, Column) ?? "1"),
                    BoolArg("wait-for-build", args, WaitForCompletion, waitForCompletion, true));

                JsonObject response = await bridge.SendAsync(id, "execute-command", executeArgs).ConfigureAwait(false);
                response = EnrichCompileFileResponse(response, args);

                if (!waitForCompletion)
                {
                    return BridgeResult(response);
                }

                JsonObject diagnosticsSnapshot = await bridge.DocumentDiagnostics
                    .QueueRefreshAndWaitForSnapshotAsync("compile-file-post-build", clearCached: true)
                    .ConfigureAwait(false);
                response["diagnosticsSnapshot"] = diagnosticsSnapshot;

                bool errorsOnly = args?[ErrorsOnly]?.GetValue<bool>() ?? false;
                if (errorsOnly)
                {
                    JsonObject? errorDiagnostics = await TryCaptureErrorDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                    if (errorDiagnostics is not null)
                    {
                        response["errorDiagnostics"] = errorDiagnostics;
                    }
                    response["errorsOnly"] = true;
                }

                return BridgeResult(response);
            },
            aliases: ["build_file", "compile_selected_file", "single_file_build", "compile_current_file"],
            bridgeCommand: "execute-command",
            summary: "Compile one file through Visual Studio (Ctrl+F7 / Build.Compile).",
            searchHints: BuildSearchHints(
                workflow: [("find_files", "Find the source file to compile"), ("debug_stop", "Stop the debugger first if it is running"), ("errors", "Check compile errors after the command")],
                related: [("build", "Build a project or solution"), (BuildErrorsTool, "Build and return compiler errors"), ("batch", "Compile several files by batching compile_file steps")]));

    private static JsonObject EnrichCompileFileResponse(JsonObject response, JsonObject? args)
    {
        bool success = (response["Success"] ?? response["success"])?.GetValue<bool>() ?? false;
        if (success)
            return response;

        string? targetPath =
            response["Data"]?["location"]?["resolvedPath"]?.GetValue<string>() ??
            response["Data"]?["state"]?["activeDocument"]?.GetValue<string>() ??
            OptionalString(args, FileArg);

        if (!IsHeaderCompileTarget(targetPath))
            return response;

        const string message = "Visual Studio cannot compile header files directly with Build.Compile. Pass the .cpp/.c/.cxx source file that includes this header, or use build/build_errors for the owning project.";
        response["Summary"] = message;

        if (response["Error"] is JsonObject error)
        {
            error["code"] = "unsupported_compile_target";
            error["message"] = message;
        }
        else
        {
            response["Error"] = new JsonObject
            {
                ["code"] = "unsupported_compile_target",
                ["message"] = message,
            };
        }

        JsonObject data = response["Data"] as JsonObject ?? [];
        data["compileFileHint"] = message;
        data["targetKind"] = "header";

        if (!string.IsNullOrWhiteSpace(targetPath))
            data["resolvedTarget"] = targetPath;

        JsonNode? requestedFile = args?[FileArg];
        if (requestedFile is not null)
            data["requestedFile"] = requestedFile.DeepClone();

        response["Data"] = data;
        return response;
    }

    private static bool IsHeaderCompileTarget(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string extension = System.IO.Path.GetExtension(path);
        return string.Equals(extension, ".h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".hh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".hpp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".hxx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".inl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ipp", StringComparison.OrdinalIgnoreCase);
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
                related: [(BuildSolutionTool, "Build instead of analysing"), (BuildErrorsTool, "Build and return errors only")]));
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
