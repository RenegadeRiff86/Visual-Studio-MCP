using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace VsIdeBridge.Tooling.Documents;

public static class ReadJsonNames
{
    public const string Slices = "slices";
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
    public const string SamplePath = "samplePath";
    public const string SampleText = "sampleText";
    public const string ResolvedPath = "resolvedPath";
    public const string Text = "text";
    public const string RequestedStartLine = "requestedStartLine";
    public const string RequestedEndLine = "requestedEndLine";
    public const string ActualStartLine = "actualStartLine";
    public const string ActualEndLine = "actualEndLine";
    public const string LineCount = "lineCount";
    public const string RevealedInEditor = "revealedInEditor";
    public const string RevealNote = "revealNote";
    public const string RequestedStartLineSort = "requestedstartline";
    public const string RequestedEndLineSort = "requestedendline";
    public const string ActualStartLineSort = "actualstartline";
    public const string ActualEndLineSort = "actualendline";
    public const string LineCountSort = "linecount";
}

public sealed class ReadSlice
{
    private readonly JsonObject _original;

    private ReadSlice(JsonObject original, int index)
    {
        _original = (JsonObject)original.DeepClone();
        Index = index;
        ResolvedPath = FirstString(original, ReadJsonNames.ResolvedPath, "path", "file");
        FileName = Path.GetFileName(ResolvedPath.Replace('/', Path.DirectorySeparatorChar));
        Text = FirstString(original, ReadJsonNames.Text);
        RevealNote = FirstString(original, ReadJsonNames.RevealNote);
        RequestedStartLine = FirstInt(original, ReadJsonNames.RequestedStartLine);
        RequestedEndLine = FirstInt(original, ReadJsonNames.RequestedEndLine);
        ActualStartLine = FirstInt(original, ReadJsonNames.ActualStartLine, "startLine");
        ActualEndLine = FirstInt(original, ReadJsonNames.ActualEndLine, "endLine");
        LineCount = FirstInt(original, ReadJsonNames.LineCount);
        RevealedInEditor = FirstBool(original, ReadJsonNames.RevealedInEditor);
    }

    public int Index { get; }

    public string ResolvedPath { get; }

    public string FileName { get; }

    public string Text { get; }

    public string RevealNote { get; }

    public int? RequestedStartLine { get; }

    public int? RequestedEndLine { get; }

    public int? ActualStartLine { get; }

    public int? ActualEndLine { get; }

    public int? LineCount { get; }

    public bool? RevealedInEditor { get; }

    public static ReadSlice FromJsonObject(JsonObject source, int index = 0)
        => new(source, index);

    public JsonObject ToJsonObject()
        => (JsonObject)_original.DeepClone();

    public bool Matches(ReadQueryOptions options)
        => Contains(ResolvedPath, options.Path)
            && ContainsAny(options.Text, Text, ResolvedPath, FileName, RevealNote);

    public string GetSortText(string? sortBy)
    {
        return sortBy switch
        {
            "path" => ResolvedPath,
            "file" => ResolvedPath,
            "name" => FileName,
            "text" => Text,
            "revealnote" => RevealNote,
            _ => ResolvedPath,
        };
    }

    public double GetSortNumber(string? sortBy)
    {
        return sortBy switch
        {
            ReadJsonNames.RequestedStartLineSort => RequestedStartLine ?? double.MaxValue,
            ReadJsonNames.RequestedEndLineSort => RequestedEndLine ?? double.MaxValue,
            ReadJsonNames.ActualStartLineSort => ActualStartLine ?? double.MaxValue,
            ReadJsonNames.ActualEndLineSort => ActualEndLine ?? double.MaxValue,
            ReadJsonNames.LineCountSort => LineCount ?? double.MaxValue,
            "index" => Index,
            _ => double.MaxValue,
        };
    }

    private static bool Contains(string value, string? filter)
        => string.IsNullOrWhiteSpace(filter) || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string? filter, params string[] values)
        => string.IsNullOrWhiteSpace(filter)
            || values.Any(value => !string.IsNullOrWhiteSpace(value)
                && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static string FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            string value = NodeToString(obj[name]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static int? FirstInt(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            int? value = NodeToInt(obj[name]);
            if (value.HasValue)
            {
                return value.Value;
            }
        }

        return null;
    }

    private static bool? FirstBool(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj[name] is JsonValue value && value.TryGetValue(out bool boolValue))
            {
                return boolValue;
            }
        }

        return null;
    }

    private static string NodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? stringValue))
            {
                return stringValue ?? string.Empty;
            }

            if (value.TryGetValue(out int intValue))
            {
                return intValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue(out long longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue(out bool boolValue))
            {
                return boolValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        return node.ToString();
    }

    private static int? NodeToInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        if (value.TryGetValue(out long longValue))
        {
            return longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : null;
        }

        string text = NodeToString(value);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }
}

public sealed class ReadSliceCollection
{
    private ReadSliceCollection(IReadOnlyList<ReadSlice> slices, int sourceCount, int sourceTotalCount, bool sourceTruncated)
    {
        Slices = slices;
        SourceCount = sourceCount;
        SourceTotalCount = sourceTotalCount;
        SourceTruncated = sourceTruncated;
    }

    public IReadOnlyList<ReadSlice> Slices { get; }

    public int SourceCount { get; }

    public int SourceTotalCount { get; }

    public bool SourceTruncated { get; }

    public static bool TryFromJsonObject(JsonObject source, out ReadSliceCollection collection)
    {
        if (source[ReadJsonNames.Slices] is not JsonArray array)
        {
            collection = Empty;
            return false;
        }

        collection = FromJsonArray(array, source);
        return true;
    }

    public static ReadSliceCollection FromJsonObject(JsonObject source)
        => TryFromJsonObject(source, out ReadSliceCollection collection) ? collection : Empty;

    public static ReadSliceCollection FromJsonArray(JsonArray array, JsonObject? metadata = null)
    {
        List<ReadSlice> slices = [];
        for (int index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonObject obj)
            {
                slices.Add(ReadSlice.FromJsonObject(obj, index));
            }
        }

        int sourceCount = metadata?[ReadJsonNames.Count]?.GetValue<int?>() ?? slices.Count;
        int sourceTotalCount = metadata?[ReadJsonNames.TotalCount]?.GetValue<int?>() ?? sourceCount;
        bool sourceTruncated = metadata?[ReadJsonNames.Truncated]?.GetValue<bool?>() ?? false;
        return new ReadSliceCollection(slices, sourceCount, sourceTotalCount, sourceTruncated);
    }

    public static ReadSliceCollection Empty { get; } = new([], 0, 0, false);

    public ReadSliceCollection ApplyFilters(ReadQueryOptions options)
        => new([.. Slices.Where(slice => slice.Matches(options))], SourceCount, SourceTotalCount, SourceTruncated);

    public ReadSliceCollection Sort(ReadQueryOptions options)
    {
        string sortBy = options.NormalizedSortBy;
        bool numeric = sortBy is ReadJsonNames.RequestedStartLineSort
            or ReadJsonNames.RequestedEndLineSort
            or ReadJsonNames.ActualStartLineSort
            or ReadJsonNames.ActualEndLineSort
            or ReadJsonNames.LineCountSort
            or "index";
        IEnumerable<ReadSlice> sorted = numeric
            ? options.SortDescending
                ? Slices.OrderByDescending(slice => slice.GetSortNumber(sortBy)).ThenBy(slice => slice.Index)
                : Slices.OrderBy(slice => slice.GetSortNumber(sortBy)).ThenBy(slice => slice.Index)
            : options.SortDescending
                ? Slices.OrderByDescending(slice => slice.GetSortText(sortBy), StringComparer.OrdinalIgnoreCase).ThenBy(slice => slice.Index)
                : Slices.OrderBy(slice => slice.GetSortText(sortBy), StringComparer.OrdinalIgnoreCase).ThenBy(slice => slice.Index);

        return new ReadSliceCollection([.. sorted], SourceCount, SourceTotalCount, SourceTruncated);
    }

    public ReadPage Page(ReadQueryOptions options)
    {
        int filteredCount = Slices.Count;
        int requestedChunkSize = Math.Max(0, options.ChunkSize);
        int chunkIndex = Math.Max(0, options.ChunkIndex);
        int effectiveChunkSize = requestedChunkSize == 0 ? Math.Max(1, filteredCount) : requestedChunkSize;
        int chunkCount = filteredCount == 0 ? 0 : (int)Math.Ceiling(filteredCount / (double)effectiveChunkSize);
        bool outOfRange = filteredCount > 0 && chunkIndex >= chunkCount;
        int chunkStart = outOfRange ? filteredCount : Math.Min(filteredCount, chunkIndex * effectiveChunkSize);
        int chunkEnd = requestedChunkSize == 0
            ? filteredCount
            : Math.Min(filteredCount, chunkStart + effectiveChunkSize);

        IReadOnlyList<ReadSlice> rows = outOfRange
            ? []
            : [.. Slices.Skip(chunkStart).Take(chunkEnd - chunkStart)];

        return new ReadPage(
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

    public JsonObject ToJsonObject(ReadQueryOptions options, JsonObject source)
    {
        ReadSliceCollection filtered = ApplyFilters(options).Sort(options);
        ReadPage page = filtered.Page(options);
        JsonObject result = (JsonObject)source.DeepClone();
        result[ReadJsonNames.Slices] = page.ToJsonArray();
        result[ReadJsonNames.Count] = page.Slices.Count;
        result[ReadJsonNames.TotalCount] = SourceTotalCount;
        result[ReadJsonNames.FilteredCount] = page.FilteredCount;
        result[ReadJsonNames.ChunkIndex] = page.ChunkIndex;
        result[ReadJsonNames.ChunkSize] = page.ChunkSize;
        result[ReadJsonNames.ChunkCount] = page.ChunkCount;
        result[ReadJsonNames.ChunkStart] = page.ChunkStart;
        result[ReadJsonNames.ChunkEnd] = page.ChunkEnd;
        result[ReadJsonNames.HasMoreChunks] = page.HasMoreChunks;
        result[ReadJsonNames.Truncated] = page.IsTruncated(SourceTruncated);
        result[ReadJsonNames.ChunkOutOfRange] = page.ChunkOutOfRange;
        result[ReadJsonNames.SortBy] = options.NormalizedSortByForJson;
        result[ReadJsonNames.SortDirection] = options.SortDescending ? "desc" : "asc";
        if (!string.IsNullOrWhiteSpace(options.GroupBy))
        {
            result[ReadJsonNames.GroupBy] = options.NormalizedGroupByForJson;
            result[ReadJsonNames.Groups] = GroupBy(options.NormalizedGroupBy);
        }

        return result;
    }

    public JsonArray GroupBy(string? groupBy)
    {
        string normalized = ReadQueryOptions.NormalizeGroupBy(groupBy);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return [.. Slices.GroupBy(slice => GetGroupKey(slice, normalized), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new JsonObject
            {
                [ReadJsonNames.Key] = group.Key,
                [ReadJsonNames.Count] = group.Count(),
                [ReadJsonNames.SamplePath] = group.First().ResolvedPath,
                [ReadJsonNames.SampleText] = Truncate(group.First().Text, 160),
            })];
    }

    private static string GetGroupKey(ReadSlice slice, string groupBy)
        => groupBy switch
        {
            "path" => slice.ResolvedPath,
            "file" => slice.FileName,
            "revealed" => slice.RevealedInEditor?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            _ => string.Empty,
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
}

public sealed class ReadQueryOptions(
    int chunkSize,
    int chunkIndex,
    string? sortBy,
    bool sortDescending,
    string? path,
    string? text,
    string? groupBy)
{
    public int ChunkSize { get; } = chunkSize;

    public int ChunkIndex { get; } = chunkIndex;

    public string? SortBy { get; } = sortBy;

    public bool SortDescending { get; } = sortDescending;

    public string? Path { get; } = path;

    public string? Text { get; } = text;

    public string? GroupBy { get; } = groupBy;

    public string NormalizedSortBy => NormalizeSortBy(SortBy);

    public string NormalizedSortByForJson => NormalizedSortBy switch
    {
        ReadJsonNames.RequestedStartLineSort => ReadJsonNames.RequestedStartLine,
        ReadJsonNames.RequestedEndLineSort => ReadJsonNames.RequestedEndLine,
        ReadJsonNames.ActualStartLineSort => ReadJsonNames.ActualStartLine,
        ReadJsonNames.ActualEndLineSort => ReadJsonNames.ActualEndLine,
        ReadJsonNames.LineCountSort => ReadJsonNames.LineCount,
        "revealnote" => "revealNote",
        _ => NormalizedSortBy,
    };

    public string NormalizedGroupBy => NormalizeGroupBy(GroupBy);

    public string NormalizedGroupByForJson => NormalizedGroupBy;

    public static ReadQueryOptions FromJsonObject(JsonObject? args, int defaultChunkSize = 10)
        => new(
            ReadInt(args, "chunk_size", defaultChunkSize),
            ReadInt(args, "chunk_index", 0),
            ReadString(args, "sort_by"),
            string.Equals(ReadString(args, "sort_direction"), "desc", StringComparison.OrdinalIgnoreCase),
            ReadString(args, "path"),
            ReadString(args, "text"),
            ReadString(args, "group_by") ?? ReadString(args, "group-by"));

    public static string NormalizeSortBy(string? sortBy)
    {
        string normalized = NormalizeToken(sortBy);
        return normalized switch
        {
            "" => "path",
            "file" => "path",
            "resolvedpath" => "path",
            "requestedstart" => ReadJsonNames.RequestedStartLineSort,
            ReadJsonNames.RequestedStartLineSort => ReadJsonNames.RequestedStartLineSort,
            "requestedend" => ReadJsonNames.RequestedEndLineSort,
            ReadJsonNames.RequestedEndLineSort => ReadJsonNames.RequestedEndLineSort,
            "actualstart" => ReadJsonNames.ActualStartLineSort,
            ReadJsonNames.ActualStartLineSort => ReadJsonNames.ActualStartLineSort,
            "actualend" => ReadJsonNames.ActualEndLineSort,
            ReadJsonNames.ActualEndLineSort => ReadJsonNames.ActualEndLineSort,
            ReadJsonNames.LineCountSort => ReadJsonNames.LineCountSort,
            "lines" => ReadJsonNames.LineCountSort,
            "name" => "name",
            "text" => "text",
            "revealnote" => "revealnote",
            "index" => "index",
            _ => "path",
        };
    }

    public static string NormalizeGroupBy(string? groupBy)
    {
        string normalized = NormalizeToken(groupBy);
        return normalized switch
        {
            "file" => "file",
            "name" => "file",
            "path" => "path",
            "resolvedpath" => "path",
            "revealed" => "revealed",
            "revealedineditor" => "revealed",
            _ => string.Empty,
        };
    }

    private static string NormalizeToken(string? value)
        => (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static string? ReadString(JsonObject? args, string name)
        => args?[name]?.GetValue<string>();

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

public readonly struct ReadPage(
    IReadOnlyList<ReadSlice> slices,
    int filteredCount,
    int chunkIndex,
    int chunkSize,
    int chunkCount,
    int chunkStart,
    int chunkEnd,
    bool hasMoreChunks,
    bool chunkOutOfRange)
{
    public IReadOnlyList<ReadSlice> Slices { get; } = slices;

    public int FilteredCount { get; } = filteredCount;

    public int ChunkIndex { get; } = chunkIndex;

    public int ChunkSize { get; } = chunkSize;

    public int ChunkCount { get; } = chunkCount;

    public int ChunkStart { get; } = chunkStart;

    public int ChunkEnd { get; } = chunkEnd;

    public bool HasMoreChunks { get; } = hasMoreChunks;

    public bool ChunkOutOfRange { get; } = chunkOutOfRange;

    public JsonArray ToJsonArray()
        => [.. Slices.Select(slice => slice.ToJsonObject())];

    public bool IsTruncated(bool sourceTruncated)
        => sourceTruncated || HasMoreChunks || ChunkOutOfRange;
}
