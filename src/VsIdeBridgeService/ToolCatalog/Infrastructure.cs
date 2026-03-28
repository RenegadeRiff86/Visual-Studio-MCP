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

    // Send one bridge command and wrap the bridge response as an MCP tool result.
    private static ToolEntry BridgeTool(
        ToolDefinition definition,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs)
        => new(definition,
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
        bool? destructive = null)
        => new(name, description, schema, category,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args);
            },
            title,
            annotations,
            outputSchema: null,
            aliases,
            tags,
            bridgeCommand: pipeCommand,
            summary,
            readOnly,
            mutating,
            destructive);

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
        bool showFullPayload = isError || WantsFullSuccessPayload(args);
        string text = showFullPayload
            ? response.ToJsonString()
            : successText ?? CreateSuccessText(response);
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

    private static string CreateSuccessText(JsonObject response)
    {
        string? diagnosticsText = CreateDiagnosticsSuccessText(response);
        if (!string.IsNullOrWhiteSpace(diagnosticsText))
        {
            return diagnosticsText;
        }

        string? summary = response["Summary"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            string? command = response["Command"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(command)
                ? summary
                : $"{command}: {summary}";
        }

        string? commandName = response["Command"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(commandName)
            ? "Command completed successfully."
            : $"{commandName}: completed successfully.";
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
        string previewText = CreateDiagnosticRowPreview(data["rows"] as JsonArray);

        string countText = totalCount == returnedCount
            ? $"rows={returnedCount}"
            : $"rows={returnedCount}, totalCount={totalCount}";

        string truncatedText = truncated ? ", truncated=true" : string.Empty;
        string previewSuffix = string.IsNullOrWhiteSpace(previewText)
            ? string.Empty
            : $" Top rows: {previewText}.";

        if (!string.IsNullOrWhiteSpace(summary))
        {
            return $"{command}: {summary} See Data.rows for details; {countText}{truncatedText}.{previewSuffix}";
        }

        return $"{command}: completed successfully. See Data.rows for details; {countText}{truncatedText}.{previewSuffix}";
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
