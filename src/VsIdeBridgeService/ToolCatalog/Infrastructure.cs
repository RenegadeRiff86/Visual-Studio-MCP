using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static readonly Lazy<ToolRegistry> DefinitionRegistry = new(BuildDefinitionRegistry);

    private static ToolRegistry BuildDefinitionRegistry()
    {
        return new ToolRegistry(CreateEntries().Select(static entry => entry.Definition));
    }

    // Build structured search_hints from workflow and related tool lists.
    private static JsonObject BuildSearchHints(
        IEnumerable<(string tool, string reason)>? workflow = null,
        IEnumerable<(string tool, string reason)>? related = null)
    {
        JsonObject hints = [];
        if (workflow is not null)
        {
            JsonArray workflowArray = [];
            foreach ((string tool, string reason) in workflow)
                workflowArray.Add(new JsonObject { ["tool"] = tool, ["reason"] = reason });
            hints["workflow"] = workflowArray;
        }

        if (related is not null)
        {
            JsonArray relatedArray = [];
            foreach ((string tool, string reason) in related)
                relatedArray.Add(new JsonObject { ["tool"] = tool, ["reason"] = reason });
            hints["related"] = relatedArray;
        }

        return hints;
    }

    // Send one bridge command and wrap the bridge response as an MCP tool result.
    private static ToolEntry BridgeTool(
        ToolDefinition definition,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs,
        JsonObject? searchHints = null)
        => new(searchHints is not null ? definition.WithSearchHints(searchHints) : definition,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args);
            });

    private static ToolEntry BridgeTool(
        string name,
        string description,
        JsonObject schema,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs,
        string category = "core",
        string? title = null,
        JsonObject? annotations = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? summary = null,
        bool? readOnly = null,
        bool? mutating = null,
        bool? destructive = null,
        JsonObject? searchHints = null,
        JsonObject? outputSchema = null)
        => new(name, description, schema, category,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args);
            },
            title,
            annotations,
            outputSchema: outputSchema,
            aliases,
            tags,
            bridgeCommand: pipeCommand,
            summary,
            readOnly,
            mutating,
            destructive,
            searchHints);

    private static JsonNode BridgeResult(JsonObject response, JsonObject? args = null)
    {
        bool success = response["Success"]?.GetValue<bool>() ?? false;
        bool isError = ToolResultFormatter.ShouldTreatAsError(response, !success);
        return ToolResultFormatter.StructuredToolResult(response, args, isError: isError);
    }
}

internal static class ToolResultFormatter
{
    internal static bool ShouldTreatAsError(JsonObject response, bool defaultIsError)
    {
        if (defaultIsError)
        {
            return true;
        }

        string? command = response["Command"]?.GetValue<string>();
        if (!string.Equals(command, "warnings", StringComparison.OrdinalIgnoreCase)
            || response["Data"] is not JsonObject data)
        {
            return false;
        }

        int totalCount = data["totalCount"]?.GetValue<int>()
            ?? data["count"]?.GetValue<int>()
            ?? 0;

        return totalCount > 0;
    }

    internal static JsonNode StructuredToolResult(
        JsonObject response,
        JsonObject? args = null,
        bool isError = false,
        string? successText = null)
    {
        string text;
        if (isError)
        {
            // Show the human-readable error message, not the full JSON blob.
            string? errorCode = response["Error"]?["code"]?.GetValue<string>();
            string? errorMsg = response["Error"]?["message"]?.GetValue<string>();
            string? summary = response["Summary"]?.GetValue<string>();
            // If no actual error message exists (e.g. warnings flagged as error for attention),
            // use the diagnostics success text so row data is visible in the response.
            string? diagnosticsText = errorMsg is null ? CreateDiagnosticsSuccessText(response) : null;
            string baseText = diagnosticsText ?? errorMsg ?? summary ?? response.ToJsonString();
            string? hint = GetErrorHint(errorCode);
            text = hint is null ? baseText : baseText + " " + hint;
        }
        else if (WantsFullSuccessPayload(args))
        {
            text = response.ToJsonString();
        }
        else if (successText != null)
        {
            // Append list data even when the caller supplied explicit summary text.
            string? listText = CreateDataListText(response);
            text = string.IsNullOrWhiteSpace(listText) ? successText : successText + "\n" + listText;
        }
        else
        {
            text = CreateSuccessText(response);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = isError,
            ["structuredContent"] = response.DeepClone(),
        };
    }

    private static bool WantsFullSuccessPayload(JsonObject? args)
        => args?["verbose"]?.GetValue<bool?>() == true
            || args?["full"]?.GetValue<bool?>() == true;

    private static string? GetErrorHint(string? code) => code switch
    {
        "document_not_found"     => "Fix: use find_files to locate the correct path, or verify the file is part of the loaded solution.",
        "file_not_found"         => "Fix: use find_files or glob to locate the correct file path.",
        "project_not_found"      => "Fix: call list_projects to see all loaded projects and their names.",
        "solution_not_open"      => "Fix: open a solution first with open_solution, or use bind_solution if one is already loaded.",
        "invalid_arguments"      => "Fix: call tool_help with the tool name to see correct parameters and examples.",
        "invalid_json"           => "Fix: check the argument is valid JSON — no trailing commas, unescaped characters, or mismatched brackets.",
        "not_in_break_mode"      => "Fix: the debugger must be paused — use debug_break to pause, or set a breakpoint with set_breakpoint then debug_start.",
        "thread_not_found"       => "Fix: call debug_threads to list available thread IDs.",
        "dirty_diagnostics"      => "Fix: call errors or warnings to get current diagnostics before retrying.",
        "unsupported_operation"  => "Fix: call tool_help with the tool name to check prerequisites or whether a different tool applies.",
        "timeout"                => "Fix: the operation timed out — try again, or reduce scope (e.g. search a subdirectory instead of the full solution).",
        _                        => null,
    };

    private static string CreateSuccessText(JsonObject response)
    {
        string bindingNoticePrefix = CreateBindingNoticePrefix(response);

        string? diagnosticsText = CreateDiagnosticsSuccessText(response);
        if (!string.IsNullOrWhiteSpace(diagnosticsText))
        {
            return bindingNoticePrefix + diagnosticsText;
        }

        string? summary = response["Summary"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            string? command = response["Command"]?.GetValue<string>();
            string summaryText = string.IsNullOrWhiteSpace(command)
                ? summary
                : $"{command}: {summary}";
            string? listText = CreateDataListText(response);
            return bindingNoticePrefix + (string.IsNullOrWhiteSpace(listText)
                ? summaryText
                : summaryText + "\n" + listText);
        }

        string? commandName = response["Command"]?.GetValue<string>();
        string fallbackText = string.IsNullOrWhiteSpace(commandName)
            ? "Command completed successfully."
            : $"{commandName}: completed successfully.";
        string? fallbackListText = CreateDataListText(response);
        return bindingNoticePrefix + (string.IsNullOrWhiteSpace(fallbackListText)
            ? fallbackText
            : fallbackText + "\n" + fallbackListText);
    }

    // Extracts and renders list data from bridge Data for commands that return item collections.
    // Skips diagnostics commands which have their own rendering.
    private static string? CreateDataListText(JsonObject response)
    {
        string? command = response["Command"]?.GetValue<string>();
        if (command is "warnings" or "errors" or "diagnostics-snapshot")
            return null;

        // Check Data object first (bridge-piped tools), then fall back to top-level fields
        // (service-side tools like glob, python_list_envs that write directly to the payload).
        JsonObject searchTarget = response["Data"] as JsonObject ?? response;

        foreach (string field in new[] { "files", "matches", "results", "rows", "items", "symbols", "references", "projects", "interpreters", "modules", "frames", "locals", "branches", "tags" })
        {
            if (searchTarget[field] is JsonArray arr && arr.Count > 0)
                return RenderDataList(arr, maxItems: 100);
        }

        return null;
    }

    private static string RenderDataList(JsonArray arr, int maxItems)
    {
        List<string> lines = [];
        int rendered = 0;
        foreach (JsonNode? item in arr)
        {
            if (rendered >= maxItems)
            {
                lines.Add($"... ({arr.Count - maxItems} more)");
                break;
            }

            string entry = RenderDataListEntry(item);
            if (!string.IsNullOrWhiteSpace(entry))
            {
                lines.Add(entry);
                rendered++;
            }
        }

        return string.Join("\n", lines);
    }

    private static string RenderDataListEntry(JsonNode? item)
    {
        if (item is JsonValue v)
            return v.GetValue<string?>() ?? string.Empty;

        if (item is not JsonObject obj)
            return item?.ToString() ?? string.Empty;

        string? file = obj["file"]?.GetValue<string>() ?? obj["path"]?.GetValue<string>();
        string? lineVal = obj["line"]?.ToString();
        string? text = obj["text"]?.GetValue<string>()
            ?? obj["name"]?.GetValue<string>()
            ?? obj["message"]?.GetValue<string>()
            ?? obj["displayName"]?.GetValue<string>();
        string? kind = obj["kind"]?.GetValue<string>();

        if (file != null && lineVal != null && text != null)
            return $"{file}:{lineVal}: {text}";
        if (file != null && lineVal != null)
            return $"{file}:{lineVal}";
        if (text != null && kind != null)
            return $"[{kind}] {text}";
        if (text != null)
            return text;
        if (file != null)
            return file;

        return obj.ToJsonString();
    }

    private static string CreateBindingNoticePrefix(JsonObject response)
    {
        string? bindingNotice = response["BindingNotice"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(bindingNotice))
        {
            return string.Empty;
        }

        return $"{bindingNotice} ";
    }

    private static string? CreateDiagnosticsSuccessText(JsonObject response)
    {
        string? command = response["Command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command) || response["Data"] is not JsonObject data)
        {
            return null;
        }

        return command switch
        {
            "warnings" or "errors" => CreateWarningsOrErrorsSuccessText(command, response, data),
            "diagnostics-snapshot" => CreateDiagnosticsSnapshotSuccessText(response, data),
            _ => null,
        };
    }

    private static string? CreateWarningsOrErrorsSuccessText(string command, JsonObject response, JsonObject data)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        int returnedCount = data["count"]?.GetValue<int>() ?? 0;
        int totalCount = data["totalCount"]?.GetValue<int>() ?? returnedCount;
        bool truncated = data["truncated"]?.GetValue<bool>() ?? false;

        string countText = totalCount == returnedCount
            ? $"rows={returnedCount}"
            : $"rows={returnedCount}, totalCount={totalCount}";

        string truncatedText = truncated ? ", truncated=true" : string.Empty;
        string rowsText = RenderDiagnosticRowList(data["rows"] as JsonArray);
        string rowsSuffix = string.IsNullOrWhiteSpace(rowsText) ? string.Empty : "\n" + rowsText;

        if (!string.IsNullOrWhiteSpace(summary))
        {
            return $"{command}: {summary} {countText}{truncatedText}.{rowsSuffix}";
        }

        return $"{command}: completed successfully. {countText}{truncatedText}.{rowsSuffix}";
    }

    private static string RenderDiagnosticRowList(JsonArray? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> entries = rows
            .OfType<JsonObject>()
            .Select(static row => RenderDiagnosticRowEntry(row))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry));

        return string.Join("\n", entries);
    }

    private static string RenderDiagnosticRowEntry(JsonObject row)
    {
        string? code = row["code"]?.GetValue<string>();
        string? message = row["message"]?.GetValue<string>();
        string? file = row["file"]?.GetValue<string>();
        int? line = row["line"]?.GetValue<int?>();

        string location = string.IsNullOrWhiteSpace(file)
            ? string.Empty
            : $"{System.IO.Path.GetFileName(file)}{(line is int lineNumber ? $":{lineNumber}" : string.Empty)}";

        string codeStr = string.IsNullOrWhiteSpace(code) ? string.Empty : code!;

        if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(message))
            return $"{location}: {(string.IsNullOrWhiteSpace(codeStr) ? string.Empty : codeStr + " ")}{message}";
        if (!string.IsNullOrWhiteSpace(message))
            return $"{(string.IsNullOrWhiteSpace(codeStr) ? string.Empty : codeStr + " ")}{message}";
        if (!string.IsNullOrWhiteSpace(location))
            return string.IsNullOrWhiteSpace(codeStr) ? location : $"{location}: {codeStr}";
        return string.Empty;
    }

    private static string? CreateDiagnosticsSnapshotSuccessText(JsonObject response, JsonObject data)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        string prefix = string.IsNullOrWhiteSpace(summary)
            ? "diagnostics-snapshot: completed successfully."
            : $"diagnostics-snapshot: {summary}";

        JsonObject? warnings = data["warnings"] as JsonObject;
        JsonObject? errors = data["errors"] as JsonObject;

        int warningRows = warnings?["count"]?.GetValue<int>() ?? 0;
        int warningTotal = warnings?["totalCount"]?.GetValue<int>() ?? warningRows;
        bool warningTruncated = warnings?["truncated"]?.GetValue<bool>() ?? false;
        int errorRows = errors?["count"]?.GetValue<int>() ?? 0;
        int errorTotal = errors?["totalCount"]?.GetValue<int>() ?? errorRows;
        bool errorTruncated = errors?["truncated"]?.GetValue<bool>() ?? false;
        string warningPreview = CreateDiagnosticRowPreview(warnings?["rows"] as JsonArray, "warnings");
        string errorPreview = CreateDiagnosticRowPreview(errors?["rows"] as JsonArray, "errors");

        string warningText = warningTotal == warningRows
            ? $"warnings.rows={warningRows}"
            : $"warnings.rows={warningRows}, warnings.totalCount={warningTotal}";
        string errorText = errorTotal == errorRows
            ? $"errors.rows={errorRows}"
            : $"errors.rows={errorRows}, errors.totalCount={errorTotal}";

        string truncationText = string.Empty;
        if (warningTruncated || errorTruncated)
        {
            List<string> flags = [];
            if (warningTruncated)
            {
                flags.Add("warnings.truncated=true");
            }

            if (errorTruncated)
            {
                flags.Add("errors.truncated=true");
            }

            truncationText = $", {string.Join(", ", flags)}";
        }

        string previewText = string.Join(" ", new[] { warningPreview, errorPreview }.Where(static text => !string.IsNullOrWhiteSpace(text)));
        string previewSuffix = string.IsNullOrWhiteSpace(previewText)
            ? string.Empty
            : $" {previewText}";

        return $"{prefix} See warnings.rows and errors.rows for details; {warningText}; {errorText}{truncationText}.{previewSuffix}";
    }

    private static string CreateDiagnosticRowPreview(JsonArray? rows, string? label = null)
    {
        if (rows is null || rows.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> previews = rows
            .OfType<JsonObject>()
            .Take(3)
            .Select(static row => CreateDiagnosticRowPreviewEntry(row))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry));

        string preview = string.Join("; ", previews);
        if (string.IsNullOrWhiteSpace(preview))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(label)
            ? preview
            : $"Top {label}: {preview}.";
    }

    private static string CreateDiagnosticRowPreviewEntry(JsonObject row)
    {
        string? code = row["code"]?.GetValue<string>();
        string? file = row["file"]?.GetValue<string>();
        int? line = row["line"]?.GetValue<int?>();

        string location = string.IsNullOrWhiteSpace(file)
            ? string.Empty
            : $"{System.IO.Path.GetFileName(file)}{(line is int lineNumber ? $":{lineNumber}" : string.Empty)}";

        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(location))
        {
            return $"{code} at {location}";
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        return location;
    }
}
