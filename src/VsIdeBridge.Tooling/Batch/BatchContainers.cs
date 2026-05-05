using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace VsIdeBridge.Tooling.Batch;

public static class BatchJsonNames
{
    public const string BatchCount = "batchCount";
    public const string SuccessCount = "successCount";
    public const string FailureCount = "failureCount";
    public const string FilteredSuccessCount = "filteredSuccessCount";
    public const string FilteredFailureCount = "filteredFailureCount";
    public const string StoppedEarly = "stoppedEarly";
    public const string Results = "results";
    public const string Count = "count";
    public const string TotalCount = "totalCount";
    public const string FilteredCount = "filteredCount";
    public const string Truncated = "truncated";
    public const string ChunkOutOfRange = "chunkOutOfRange";
    public const string ChunkIndex = "chunkIndex";
    public const string ChunkSize = "chunkSize";
    public const string ChunkCount = "chunkCount";
    public const string ChunkStart = "chunkStart";
    public const string ChunkEnd = "chunkEnd";
    public const string HasMoreChunks = "hasMoreChunks";
    public const string SortBy = "sortBy";
    public const string SortDirection = "sortDirection";
    public const string GroupBy = "groupBy";
    public const string Groups = "groups";
    public const string Key = "key";
    public const string DataMode = "dataMode";
    public const string Index = "index";
    public const string Id = "id";
    public const string Command = "command";
    public const string Success = "success";
    public const string Summary = "summary";
    public const string Warnings = "warnings";
    public const string StepData = "data";
    public const string Error = "error";
}

public enum BatchDataMode
{
    Summary,
    Full,
    None,
}

public sealed class BatchStepResult
{
    private const int MaxSummaryStringLength = 500;
    private const int MaxObjectSummaryProperties = 24;

    private readonly JsonObject _original;

    private BatchStepResult(JsonObject original, int fallbackIndex)
    {
        _original = (JsonObject)original.DeepClone();
        Index = FirstInt(original, BatchJsonNames.Index) ?? fallbackIndex;
        Id = FirstString(original, BatchJsonNames.Id);
        Command = FirstString(original, BatchJsonNames.Command);
        Summary = FirstString(original, BatchJsonNames.Summary);
        Success = FirstBool(original, BatchJsonNames.Success) ?? false;
        WarningCount = original[BatchJsonNames.Warnings] is JsonArray warnings ? warnings.Count : 0;
        ErrorCode = original[BatchJsonNames.Error]?["code"]?.GetValue<string>() ?? string.Empty;
        ErrorMessage = original[BatchJsonNames.Error]?["message"]?.GetValue<string>() ?? string.Empty;
    }

    public int Index { get; }

    public string Id { get; }

    public string Command { get; }

    public string Summary { get; }

    public bool Success { get; }

    public int WarningCount { get; }

    public string ErrorCode { get; }

    public string ErrorMessage { get; }

    public static BatchStepResult FromJsonObject(JsonObject source, int fallbackIndex = 0)
        => new(source, fallbackIndex);

    public bool Matches(BatchQueryOptions options)
        => MatchesCommand(options.Command)
            && MatchesSuccess(options.Success)
            && ContainsAny(options.Text, Summary, Command, Id, ErrorCode, ErrorMessage);

    public JsonObject ToJsonObject(BatchDataMode dataMode)
    {
        JsonObject result = (JsonObject)_original.DeepClone();
        switch (dataMode)
        {
            case BatchDataMode.Full:
                break;
            case BatchDataMode.None:
                result[BatchJsonNames.StepData] = new JsonObject
                {
                    ["omitted"] = true,
                    [BatchJsonNames.DataMode] = "none",
                };
                break;
            default:
                result[BatchJsonNames.StepData] = SummarizeData(result[BatchJsonNames.StepData]);
                break;
        }

        return result;
    }

    public string GetSortText(string? sortBy)
        => sortBy switch
        {
            "id" => Id,
            "command" => Command,
            "summary" => Summary,
            "error" => ErrorCode,
            _ => Command,
        };

    public double GetSortNumber(string? sortBy)
        => sortBy switch
        {
            "index" => Index,
            "success" => Success ? 1 : 0,
            BatchJsonNames.Warnings => WarningCount,
            _ => double.MaxValue,
        };

    private bool MatchesCommand(string? command)
        => string.IsNullOrWhiteSpace(command) || Command.Contains(command, StringComparison.OrdinalIgnoreCase);

    private bool MatchesSuccess(bool? success)
        => !success.HasValue || Success == success.Value;

    private static bool ContainsAny(string? filter, params string[] values)
        => string.IsNullOrWhiteSpace(filter)
            || values.Any(value => !string.IsNullOrWhiteSpace(value)
                && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static JsonNode SummarizeData(JsonNode? node)
    {
        if (node is null)
        {
            return new JsonObject { ["isNull"] = true };
        }

        return SummarizeNode(node, depth: 0);
    }

    private static JsonNode SummarizeNode(JsonNode node, int depth)
    {
        if (node is JsonValue value)
        {
            return SummarizeValue(value);
        }

        if (node is JsonArray array)
        {
            JsonObject summary = new()
            {
                ["kind"] = "array",
                [BatchJsonNames.Count] = array.Count,
            };
            if (array.Count > 0)
            {
                summary["first"] = SummarizeNode(array[0]!, depth + 1);
                summary["last"] = SummarizeNode(array[array.Count - 1]!, depth + 1);
            }

            return summary;
        }

        if (node is JsonObject obj)
        {
            JsonObject summary = new()
            {
                ["kind"] = "object",
                ["propertyCount"] = obj.Count,
            };

            int copied = 0;
            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                if (copied >= MaxObjectSummaryProperties)
                {
                    summary["truncatedProperties"] = true;
                    break;
                }

                if (property.Value is null)
                {
                    summary[property.Key] = null;
                }
                else if (property.Value is JsonValue scalar)
                {
                    summary[property.Key] = SummarizeValue(scalar);
                }
                else if (property.Value is JsonArray propertyArray)
                {
                    summary[property.Key] = SummarizeArrayProperty(propertyArray, depth);
                }
                else if (depth < 1 && property.Value is JsonObject nested)
                {
                    summary[property.Key] = SummarizeNode(nested, depth + 1);
                }
                else
                {
                    summary[property.Key] = new JsonObject
                    {
                        ["kind"] = "object",
                        ["propertyCount"] = property.Value.AsObject().Count,
                    };
                }

                copied++;
            }

            return summary;
        }

        return node.DeepClone();
    }

    private static JsonObject SummarizeArrayProperty(JsonArray array, int depth)
    {
        JsonObject summary = new()
        {
            ["kind"] = "array",
            [BatchJsonNames.Count] = array.Count,
        };
        if (array.Count > 0)
        {
            summary["first"] = depth < 2 ? SummarizeNode(array[0]!, depth + 1) : NodeKind(array[0]);
            summary["last"] = depth < 2 ? SummarizeNode(array[array.Count - 1]!, depth + 1) : NodeKind(array[array.Count - 1]);
        }

        return summary;
    }

    private static JsonNode SummarizeValue(JsonValue value)
    {
        if (value.TryGetValue(out string? stringValue))
        {
            return Truncate(stringValue ?? string.Empty, MaxSummaryStringLength);
        }

        return value.DeepClone();
    }

    private static JsonObject NodeKind(JsonNode? node)
        => new()
        {
            ["kind"] = node switch
            {
                JsonArray => "array",
                JsonObject => "object",
                JsonValue => "value",
                _ => "null",
            },
        };

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

#if NET8_0_OR_GREATER
        return value[..maxLength];
#else
        return value.Substring(0, maxLength);
#endif
    }

    private static string FirstString(JsonObject obj, string name)
        => obj[name]?.GetValue<string>() ?? string.Empty;

    private static int? FirstInt(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node is JsonValue value && value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        return node is not null && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static bool? FirstBool(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue(out bool boolValue) ? boolValue : null;
}

public sealed class BatchResultCollection
{
    private BatchResultCollection(
        IReadOnlyList<BatchStepResult> rows,
        int batchCount,
        int successCount,
        int failureCount,
        bool stoppedEarly)
    {
        Rows = rows;
        BatchCount = batchCount;
        SuccessCount = successCount;
        FailureCount = failureCount;
        StoppedEarly = stoppedEarly;
    }

    public IReadOnlyList<BatchStepResult> Rows { get; }

    public int BatchCount { get; }

    public int SuccessCount { get; }

    public int FailureCount { get; }

    public bool StoppedEarly { get; }

    public static bool TryFromJsonObject(JsonObject source, out BatchResultCollection collection)
    {
        if (source[BatchJsonNames.Results] is not JsonArray array)
        {
            collection = Empty;
            return false;
        }

        collection = FromJsonArray(array, source);
        return true;
    }

    public static BatchResultCollection FromJsonObject(JsonObject source)
        => TryFromJsonObject(source, out BatchResultCollection collection) ? collection : Empty;

    public static BatchResultCollection FromJsonArray(JsonArray array, JsonObject? metadata = null)
    {
        List<BatchStepResult> rows = [];
        for (int index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonObject obj)
            {
                rows.Add(BatchStepResult.FromJsonObject(obj, index));
            }
        }

        return new BatchResultCollection(
            rows,
            ReadInt(metadata, BatchJsonNames.BatchCount, rows.Count),
            ReadInt(metadata, BatchJsonNames.SuccessCount, rows.Count(row => row.Success)),
            ReadInt(metadata, BatchJsonNames.FailureCount, rows.Count(row => !row.Success)),
            ReadBool(metadata, BatchJsonNames.StoppedEarly, false));
    }

    public static BatchResultCollection Empty { get; } = new([], 0, 0, 0, false);

    public BatchResultCollection ApplyFilters(BatchQueryOptions options)
        => new([.. Rows.Where(row => row.Matches(options))], BatchCount, SuccessCount, FailureCount, StoppedEarly);

    public BatchResultCollection Sort(BatchQueryOptions options)
    {
        string sortBy = options.NormalizedSortBy;
        bool numeric = sortBy is "index" or "success" or BatchJsonNames.Warnings;
        IEnumerable<BatchStepResult> sorted = numeric
            ? options.SortDescending
                ? Rows.OrderByDescending(row => row.GetSortNumber(sortBy)).ThenBy(row => row.Index)
                : Rows.OrderBy(row => row.GetSortNumber(sortBy)).ThenBy(row => row.Index)
            : options.SortDescending
                ? Rows.OrderByDescending(row => row.GetSortText(sortBy), StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Index)
                : Rows.OrderBy(row => row.GetSortText(sortBy), StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Index);

        return new BatchResultCollection([.. sorted], BatchCount, SuccessCount, FailureCount, StoppedEarly);
    }

    public BatchPage Page(BatchQueryOptions options)
    {
        int filteredCount = Rows.Count;
        int requestedChunkSize = Math.Max(0, options.ChunkSize);
        int chunkIndex = Math.Max(0, options.ChunkIndex);
        int effectiveChunkSize = requestedChunkSize == 0 ? Math.Max(1, filteredCount) : requestedChunkSize;
        int chunkCount = filteredCount == 0 ? 0 : (int)Math.Ceiling(filteredCount / (double)effectiveChunkSize);
        bool outOfRange = filteredCount > 0 && chunkIndex >= chunkCount;
        int chunkStart = outOfRange ? filteredCount : Math.Min(filteredCount, chunkIndex * effectiveChunkSize);
        int chunkEnd = requestedChunkSize == 0
            ? filteredCount
            : Math.Min(filteredCount, chunkStart + effectiveChunkSize);
        IReadOnlyList<BatchStepResult> rows = outOfRange
            ? []
            : [.. Rows.Skip(chunkStart).Take(chunkEnd - chunkStart)];

        return new BatchPage(
            rows,
            filteredCount,
            chunkIndex,
            requestedChunkSize,
            requestedChunkSize == 0 && filteredCount > 0 ? 1 : chunkCount,
            chunkStart,
            chunkEnd,
            !outOfRange && requestedChunkSize != 0 && chunkIndex < chunkCount - 1,
            outOfRange);
    }

    public JsonObject ToJsonObject(BatchQueryOptions options, JsonObject source)
    {
        BatchResultCollection filtered = ApplyFilters(options).Sort(options);
        BatchPage page = filtered.Page(options);
        JsonObject result = (JsonObject)source.DeepClone();
        result[BatchJsonNames.Results] = page.ToJsonArray(options.DataMode);
        result[BatchJsonNames.BatchCount] = BatchCount;
        result[BatchJsonNames.SuccessCount] = SuccessCount;
        result[BatchJsonNames.FailureCount] = FailureCount;
        result[BatchJsonNames.FilteredSuccessCount] = filtered.Rows.Count(row => row.Success);
        result[BatchJsonNames.FilteredFailureCount] = filtered.Rows.Count(row => !row.Success);
        result[BatchJsonNames.Count] = page.Rows.Count;
        result[BatchJsonNames.TotalCount] = BatchCount;
        result[BatchJsonNames.FilteredCount] = page.FilteredCount;
        result[BatchJsonNames.ChunkIndex] = page.ChunkIndex;
        result[BatchJsonNames.ChunkSize] = page.ChunkSize;
        result[BatchJsonNames.ChunkCount] = page.ChunkCount;
        result[BatchJsonNames.ChunkStart] = page.ChunkStart;
        result[BatchJsonNames.ChunkEnd] = page.ChunkEnd;
        result[BatchJsonNames.HasMoreChunks] = page.HasMoreChunks;
        result[BatchJsonNames.Truncated] = page.IsTruncated();
        result[BatchJsonNames.ChunkOutOfRange] = page.ChunkOutOfRange;
        result[BatchJsonNames.SortBy] = options.NormalizedSortBy;
        result[BatchJsonNames.SortDirection] = options.SortDescending ? "desc" : "asc";
        result[BatchJsonNames.DataMode] = options.DataMode.ToString().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(options.GroupBy))
        {
            result[BatchJsonNames.GroupBy] = options.NormalizedGroupBy;
            result[BatchJsonNames.Groups] = filtered.GroupBy(options.NormalizedGroupBy);
        }

        return result;
    }

    public JsonArray GroupBy(string? groupBy)
    {
        string normalized = BatchQueryOptions.NormalizeGroupBy(groupBy);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return [.. Rows.GroupBy(row => GetGroupKey(row, normalized), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new JsonObject
            {
                [BatchJsonNames.Key] = group.Key,
                [BatchJsonNames.Count] = group.Count(),
                [BatchJsonNames.SuccessCount] = group.Count(row => row.Success),
                [BatchJsonNames.FailureCount] = group.Count(row => !row.Success),
            })];
    }

    private static string GetGroupKey(BatchStepResult row, string groupBy)
        => groupBy switch
        {
            "command" => row.Command,
            "success" => row.Success.ToString(CultureInfo.InvariantCulture),
            "error" => string.IsNullOrWhiteSpace(row.ErrorCode) ? "none" : row.ErrorCode,
            _ => string.Empty,
        };

    private static int ReadInt(JsonObject? source, string name, int defaultValue)
    {
        JsonNode? node = source?[name];
        if (node is JsonValue value && value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        return node is not null && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;
    }

    private static bool ReadBool(JsonObject? source, string name, bool defaultValue)
        => source?[name] is JsonValue value && value.TryGetValue(out bool boolValue) ? boolValue : defaultValue;
}

public sealed class BatchQueryOptions(
    int chunkSize,
    int chunkIndex,
    string? sortBy,
    bool sortDescending,
    string? command,
    bool? success,
    string? text,
    string? groupBy,
    BatchDataMode dataMode)
{
    public int ChunkSize { get; } = chunkSize;

    public int ChunkIndex { get; } = chunkIndex;

    public string? SortBy { get; } = sortBy;

    public bool SortDescending { get; } = sortDescending;

    public string? Command { get; } = command;

    public bool? Success { get; } = success;

    public string? Text { get; } = text;

    public string? GroupBy { get; } = groupBy;

    public BatchDataMode DataMode { get; } = dataMode;

    public string NormalizedSortBy => NormalizeSortBy(SortBy);

    public string NormalizedGroupBy => NormalizeGroupBy(GroupBy);

    public static BatchQueryOptions FromJsonObject(JsonObject? args, int defaultChunkSize = 10)
        => new(
            ReadInt(args, "chunk_size", defaultChunkSize),
            ReadInt(args, "chunk_index", 0),
            ReadString(args, "sort_by"),
            string.Equals(ReadString(args, "sort_direction"), "desc", StringComparison.OrdinalIgnoreCase),
            ReadString(args, "command"),
            ReadBool(args, "success"),
            ReadString(args, "text"),
            ReadString(args, "group_by") ?? ReadString(args, "group-by"),
            ParseDataMode(ReadString(args, "data_mode")));

    public static string NormalizeSortBy(string? sortBy)
    {
        string normalized = NormalizeToken(sortBy);
        return normalized switch
        {
            "" => "index",
            "index" => "index",
            "id" => "id",
            "command" => "command",
            "success" => "success",
            "summary" => "summary",
            BatchJsonNames.Warnings => BatchJsonNames.Warnings,
            "warningcount" => BatchJsonNames.Warnings,
            "error" => "error",
            _ => "index",
        };
    }

    public static string NormalizeGroupBy(string? groupBy)
    {
        string normalized = NormalizeToken(groupBy);
        return normalized switch
        {
            "command" => "command",
            "success" => "success",
            "error" => "error",
            _ => string.Empty,
        };
    }

    private static BatchDataMode ParseDataMode(string? mode)
    {
        string normalized = NormalizeToken(mode);
        return normalized switch
        {
            "full" => BatchDataMode.Full,
            "none" => BatchDataMode.None,
            "summary" => BatchDataMode.Summary,
            "" => BatchDataMode.Summary,
            _ => BatchDataMode.Summary,
        };
    }

    private static string NormalizeToken(string? value)
        => (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static string? ReadString(JsonObject? args, string name)
        => args?[name]?.GetValue<string>();

    private static bool? ReadBool(JsonObject? args, string name)
    {
        JsonNode? node = args?[name];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out bool boolValue))
        {
            return boolValue;
        }

        return bool.TryParse(node.ToString(), out bool parsed) ? parsed : null;
    }

    private static int ReadInt(JsonObject? args, string name, int defaultValue)
    {
        JsonNode? node = args?[name];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value && value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;
    }
}

public readonly struct BatchPage(
    IReadOnlyList<BatchStepResult> rows,
    int filteredCount,
    int chunkIndex,
    int chunkSize,
    int chunkCount,
    int chunkStart,
    int chunkEnd,
    bool hasMoreChunks,
    bool chunkOutOfRange)
{
    public IReadOnlyList<BatchStepResult> Rows { get; } = rows;

    public int FilteredCount { get; } = filteredCount;

    public int ChunkIndex { get; } = chunkIndex;

    public int ChunkSize { get; } = chunkSize;

    public int ChunkCount { get; } = chunkCount;

    public int ChunkStart { get; } = chunkStart;

    public int ChunkEnd { get; } = chunkEnd;

    public bool HasMoreChunks { get; } = hasMoreChunks;

    public bool ChunkOutOfRange { get; } = chunkOutOfRange;

    public JsonArray ToJsonArray(BatchDataMode dataMode)
        => [.. Rows.Select(row => row.ToJsonObject(dataMode))];

    public bool IsTruncated()
        => HasMoreChunks || ChunkOutOfRange;
}
