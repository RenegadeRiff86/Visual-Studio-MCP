using System.Text.Json.Nodes;

using static VsIdeBridgeService.ArgBuilder;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static string? GetBridgeDiagnosticsMax(JsonObject? args)
        => WantsFullDiagnosticsPayload(args) ? OptionalText(args, Max) : null;

    private static DiagnosticPagingOptions CreateDiagnosticPagingOptions(JsonObject? args)
    {
        int chunkSize = GetOptionalNonNegativeInt(args, ChunkSize)
            ?? GetOptionalNonNegativeInt(args, Max)
            ?? DefaultCompactDiagnosticsRows;
        int chunkIndex = GetOptionalNonNegativeInt(args, ChunkIndex) ?? 0;
        string? sortBy = NormalizeDiagnosticSortField(OptionalString(args, SortBy));
        bool sortDescending = IsDescendingSort(OptionalString(args, SortDirection));

        return new DiagnosticPagingOptions(chunkSize, chunkIndex, sortBy, sortDescending);
    }

    private static int? GetOptionalNonNegativeInt(JsonObject? args, string name)
    {
        if (args?[name] is not JsonNode node)
        {
            return null;
        }

        if (node is JsonValue valueNode && valueNode.TryGetValue(out int value))
        {
            return Math.Max(0, value);
        }

        return int.TryParse(node.ToString(), out value)
            ? Math.Max(0, value)
            : null;
    }

    private static string? NormalizeDiagnosticSortField(string? sortBy)
    {
        string? normalized = sortBy?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "severity" => "severity",
            "code" => "code",
            "project" => "project",
            "file" or "path" => "file",
            "line" => "line",
            "column" => "column",
            "message" => "message",
            "source" => "source",
            "tool" => "tool",
            _ => null,
        };
    }

    private static bool IsDescendingSort(string? sortDirection)
        => string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sortDirection, "descending", StringComparison.OrdinalIgnoreCase);

    private static void CompactDiagnosticRows(JsonObject obj, JsonArray rows, DiagnosticPagingOptions paging)
    {
        List<JsonObject> sortedRows = SortDiagnosticRows(rows, paging);
        int sourceCount = obj["count"]?.GetValue<int?>() ?? sortedRows.Count;
        int sourceTotalCount = obj["totalCount"]?.GetValue<int?>() ?? sourceCount;
        int filteredCount = Math.Max(sortedRows.Count, Math.Max(sourceCount, sourceTotalCount));
        bool sourceTruncated = obj["truncated"]?.GetValue<bool?>() == true || filteredCount > sortedRows.Count;
        int chunkCount = CalculateChunkCount(filteredCount, paging.ChunkSize);
        int chunkStart = CalculateChunkStart(paging.ChunkIndex, paging.ChunkSize, filteredCount);
        int availableStart = Math.Min(chunkStart, sortedRows.Count);
        int availableEnd = paging.ChunkSize == 0
            ? sortedRows.Count
            : Math.Min(availableStart + paging.ChunkSize, sortedRows.Count);
        bool chunkOutOfRange = paging.ChunkSize > 0 && paging.ChunkIndex >= chunkCount && filteredCount > 0;

        JsonArray compactRows = [];
        if (!chunkOutOfRange)
        {
            for (int i = availableStart; i < availableEnd; i++)
            {
                compactRows.Add(sortedRows[i].DeepClone());
            }
        }

        obj["rows"] = compactRows;
        obj["count"] = compactRows.Count;
        obj["totalCount"] = filteredCount;
        obj["filteredCount"] = filteredCount;
        obj["chunkIndex"] = paging.ChunkIndex;
        obj["chunkSize"] = paging.ChunkSize;
        obj["chunkCount"] = chunkCount;
        obj["chunkStart"] = chunkOutOfRange ? filteredCount : Math.Min(chunkStart, filteredCount);
        obj["chunkEnd"] = chunkOutOfRange ? filteredCount : Math.Min(chunkStart + compactRows.Count, filteredCount);
        obj["hasMoreChunks"] = paging.ChunkSize > 0 && paging.ChunkIndex + 1 < chunkCount;
        obj["truncated"] = sourceTruncated || chunkCount > 1 || chunkOutOfRange;

        if (chunkOutOfRange)
        {
            obj["chunkOutOfRange"] = true;
        }

        if (!string.IsNullOrWhiteSpace(paging.SortBy))
        {
            obj["sortBy"] = paging.SortBy;
            obj["sortDirection"] = paging.SortDescending ? "desc" : "asc";
        }
    }

    private static int CalculateChunkCount(int filteredCount, int chunkSize)
    {
        if (filteredCount == 0)
        {
            return 0;
        }

        return chunkSize == 0
            ? 1
            : (int)Math.Ceiling(filteredCount / (double)chunkSize);
    }

    private static int CalculateChunkStart(int chunkIndex, int chunkSize, int filteredCount)
    {
        if (chunkSize == 0 || filteredCount == 0)
        {
            return 0;
        }

        try
        {
            return Math.Min(checked(chunkIndex * chunkSize), filteredCount);
        }
        catch (OverflowException)
        {
            return filteredCount;
        }
    }

    private static List<JsonObject> SortDiagnosticRows(JsonArray rows, DiagnosticPagingOptions paging)
    {
        List<JsonObject> diagnosticRows = [.. rows.OfType<JsonObject>()];
        if (string.IsNullOrWhiteSpace(paging.SortBy))
        {
            return diagnosticRows;
        }

        return paging.SortBy is Line or Column
            ? SortDiagnosticRowsByNumber(diagnosticRows, paging)
            : SortDiagnosticRowsByText(diagnosticRows, paging);
    }

    private static List<JsonObject> SortDiagnosticRowsByNumber(List<JsonObject> rows, DiagnosticPagingOptions paging)
    {
        return paging.SortDescending
            ? [.. rows
                .OrderByDescending(row => GetDiagnosticSortNumber(row, paging.SortBy!))
                .ThenBy(row => GetDiagnosticSortText(row, "file"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => GetDiagnosticSortText(row, "message"), StringComparer.OrdinalIgnoreCase)]
            : [.. rows
                .OrderBy(row => GetDiagnosticSortNumber(row, paging.SortBy!))
                .ThenBy(row => GetDiagnosticSortText(row, "file"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => GetDiagnosticSortText(row, "message"), StringComparer.OrdinalIgnoreCase)];
    }

    private static List<JsonObject> SortDiagnosticRowsByText(List<JsonObject> rows, DiagnosticPagingOptions paging)
    {
        return paging.SortDescending
            ? [.. rows
                .OrderByDescending(row => GetDiagnosticSortText(row, paging.SortBy!), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => GetDiagnosticSortText(row, "file"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => GetDiagnosticSortNumber(row, "line"))]
            : [.. rows
                .OrderBy(row => GetDiagnosticSortText(row, paging.SortBy!), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => GetDiagnosticSortText(row, "file"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => GetDiagnosticSortNumber(row, "line"))];
    }

    private static string GetDiagnosticSortText(JsonObject row, string propertyName)
    {
        JsonNode? node = propertyName switch
        {
            "file" => row["file"] ?? row["path"],
            _ => row[propertyName],
        };

        if (node is JsonValue valueNode && valueNode.TryGetValue(out string? text))
        {
            return text ?? string.Empty;
        }

        return node?.ToString() ?? string.Empty;
    }

    private static int GetDiagnosticSortNumber(JsonObject row, string propertyName)
    {
        if (row[propertyName] is JsonValue valueNode)
        {
            if (valueNode.TryGetValue(out int value))
            {
                return value;
            }

            if (int.TryParse(valueNode.ToString(), out value))
            {
                return value;
            }
        }

        return int.MaxValue;
    }

    private sealed record DiagnosticPagingOptions(int ChunkSize, int ChunkIndex, string? SortBy, bool SortDescending);
}
