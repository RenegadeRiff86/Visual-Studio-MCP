using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService.SystemTools;

internal static partial class BridgeLogSummaryTool
{
    private const int DefaultTailLines = 300;
    private const int MaxTailLines = 2000;
    private const int DefaultMaxEvents = 80;
    private const int MaxEvents = 300;
    private const int RawLineLimit = 200;
    private const int MaxLineChars = 1000;
    private const int MaxMessageChars = 500;

    private static readonly string[] ErrorTerms =
    [
        "fatal",
        "exception",
        "failed",
        "failure",
        "timed out",
        "timeout",
        " error",
        "error:",
    ];

    private static readonly string[] WarningTerms =
    [
        "warning",
        "degraded",
        "fallback",
    ];

    private static readonly string[] LifecycleTerms =
    [
        "starting",
        "started",
        "stopped",
        "stdin closed",
        "shutdown",
        "exiting",
        "lease",
        "superseded",
        "parent process",
    ];

    private static readonly string[] RequestTerms =
    [
        "request",
        "dispatch",
        "response",
        "tool complete",
        "tool cancelled",
        "stdout write",
    ];

    public static Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection _)
    {
        string requestedLog = args?["log"]?.GetValue<string>() ?? "all";
        string selection = NormalizeLogSelection(id, requestedLog);
        int tailLines = Clamp(args?["tail_lines"]?.GetValue<int?>() ?? DefaultTailLines, 1, MaxTailLines);
        int maxEvents = Clamp(args?["max_events"]?.GetValue<int?>() ?? DefaultMaxEvents, 1, MaxEvents);
        string? textFilter = NormalizeOptionalString(args?["text"]?.GetValue<string>());
        bool includeRaw = args?["include_raw"]?.GetValue<bool?>() == true;

        string logDirectory = BridgeLogPaths.GetSharedLogDirectory();
        List<LogTarget> targets = ResolveTargets(logDirectory, selection);
        JsonArray files = [];
        LogCounters totals = new();
        int remainingEvents = maxEvents;

        foreach (LogTarget target in targets)
        {
            files.Add(BuildFileSummary(target, tailLines, textFilter, includeRaw, ref remainingEvents, totals));
        }

        string summary = $"Parsed {totals.EventCount} notable event(s) from {files.Count} bridge log file(s) " +
            $"({totals.ErrorCount} error, {totals.WarningCount} warning).";

        JsonObject payload = new()
        {
            ["success"] = true,
            ["logDirectory"] = logDirectory,
            ["requestedLog"] = requestedLog,
            ["normalizedLog"] = selection,
            ["tailLines"] = tailLines,
            ["maxEvents"] = maxEvents,
            ["textFilter"] = textFilter,
            ["includeRaw"] = includeRaw,
            ["eventCount"] = totals.EventCount,
            ["errorCount"] = totals.ErrorCount,
            ["warningCount"] = totals.WarningCount,
            ["lifecycleCount"] = totals.LifecycleCount,
            ["requestCount"] = totals.RequestCount,
            ["files"] = files,
        };

        return Task.FromResult(ToolResultFormatter.StructuredToolResult(payload, args, successText: summary));
    }

    private static JsonObject BuildFileSummary(
        LogTarget target,
        int tailLines,
        string? textFilter,
        bool includeRaw,
        ref int remainingEvents,
        LogCounters totals)
    {
        JsonObject result = new()
        {
            ["kind"] = target.Kind,
            ["path"] = target.Path,
            ["exists"] = File.Exists(target.Path),
        };

        if (!File.Exists(target.Path))
        {
            result["events"] = new JsonArray();
            result["eventCount"] = 0;
            result["tailLineCount"] = 0;
            return result;
        }

        try
        {
            FileInfo info = new(target.Path);
            result["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
            result["sizeBytes"] = info.Length;

            IReadOnlyList<string> tail = ReadTailLines(target.Path, tailLines);
            IReadOnlyList<string> filtered = textFilter is null
                ? tail
                : [.. tail.Where(line => line.Contains(textFilter, StringComparison.OrdinalIgnoreCase))];

            result["tailLineCount"] = tail.Count;
            result["filteredLineCount"] = filtered.Count;

            List<LogEvent> events = ExtractEvents(filtered, remainingEvents, out bool eventLimitReached);
            remainingEvents = Math.Max(0, remainingEvents - events.Count);

            JsonArray eventArray = [];
            LogCounters fileCounters = new();
            foreach (LogEvent logEvent in events)
            {
                eventArray.Add(ToJson(logEvent));
                fileCounters.Add(logEvent);
                totals.Add(logEvent);
            }

            result["events"] = eventArray;
            result["eventCount"] = fileCounters.EventCount;
            result["errorCount"] = fileCounters.ErrorCount;
            result["warningCount"] = fileCounters.WarningCount;
            result["lifecycleCount"] = fileCounters.LifecycleCount;
            result["requestCount"] = fileCounters.RequestCount;
            result["eventLimitReached"] = eventLimitReached;

            if (includeRaw)
            {
                JsonArray raw = [];
                foreach (string line in filtered.Take(RawLineLimit))
                {
                    raw.Add(Truncate(line, MaxLineChars));
                }

                result["rawTail"] = raw;
                result["rawLineLimit"] = RawLineLimit;
                result["rawTailTruncated"] = filtered.Count > RawLineLimit;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result["readError"] = ex.Message;
            result["events"] = new JsonArray();
            result["eventCount"] = 0;
        }

        return result;
    }

    private static List<LogTarget> ResolveTargets(string logDirectory, string selection)
    {
        List<LogTarget> targets = [];
        if (selection is "all" or "mcp")
        {
            targets.Add(new("mcp", BridgeLogPaths.GetMcpServerLogPath()));
        }

        if (selection is "all" or "extension")
        {
            targets.Add(new("extension", ResolveLatestExtensionLog(logDirectory)));
        }

        return targets;
    }

    private static string ResolveLatestExtensionLog(string logDirectory)
    {
        if (Directory.Exists(logDirectory))
        {
            string? latest = Directory.EnumerateFiles(logDirectory, "vs-ide-bridge-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(latest))
            {
                return latest;
            }
        }

        return BridgeLogPaths.GetVisualStudioExtensionLogPath();
    }

    private static string NormalizeLogSelection(JsonNode? id, string value)
    {
        string normalized = value.Trim().Replace('-', '_').ToLowerInvariant();
        return normalized switch
        {
            "" or "all" => "all",
            "mcp" or "mcp_server" => "mcp",
            "extension" or "vsix" or "visual_studio" or "visualstudio" => "extension",
            _ => throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                "Argument 'log' must be one of: all, mcp, extension, or vsix."),
        };
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int lineCount)
    {
        Queue<string> queue = new(lineCount);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);

        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == lineCount)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return [.. queue];
    }

    private static List<LogEvent> ExtractEvents(IReadOnlyList<string> lines, int maxEvents, out bool truncated)
    {
        List<LogEvent> events = [];
        truncated = false;

        for (int i = 0; i < lines.Count; i++)
        {
            LogLine parsed = ParseLine(i + 1, lines[i]);
            LogClassification? classification = Classify(parsed.Message);
            if (classification is null)
            {
                continue;
            }

            if (events.Count >= maxEvents)
            {
                truncated = true;
                continue;
            }

            events.Add(new LogEvent(
                parsed.LineIndex,
                parsed.Timestamp,
                parsed.ProcessId,
                classification.Value.Severity,
                classification.Value.Kind,
                parsed.Message,
                parsed.Line));
        }

        return events;
    }

    private static LogLine ParseLine(int lineIndex, string line)
    {
        Match match = LogLineRegex().Match(line);
        if (!match.Success)
        {
            return new(lineIndex, null, null, line.Trim(), Truncate(line, MaxLineChars));
        }

        string? timestamp = NormalizeOptionalString(match.Groups["timestamp"].Value);
        string? processId = NormalizeOptionalString(match.Groups["pid"].Value);
        string message = NormalizeOptionalString(match.Groups["message"].Value) ?? line.Trim();
        return new(lineIndex, timestamp, processId, message, Truncate(line, MaxLineChars));
    }

    private static LogClassification? Classify(string message)
    {
        string lower = message.ToLowerInvariant();
        if (ContainsAny(lower, ErrorTerms))
        {
            return new("error", "failure");
        }

        if (ContainsAny(lower, WarningTerms))
        {
            return new("warning", "warning");
        }

        if (ContainsAny(lower, LifecycleTerms))
        {
            return new("info", "lifecycle");
        }

        if (ContainsAny(lower, RequestTerms))
        {
            return new("info", "request");
        }

        return null;
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static JsonObject ToJson(LogEvent logEvent)
    {
        JsonObject json = new()
        {
            ["tailLine"] = logEvent.TailLine,
            ["severity"] = logEvent.Severity,
            ["kind"] = logEvent.Kind,
            ["message"] = Truncate(logEvent.Message, MaxMessageChars),
            ["line"] = logEvent.Line,
        };

        if (!string.IsNullOrWhiteSpace(logEvent.Timestamp))
        {
            json["timestamp"] = logEvent.Timestamp;
        }

        if (!string.IsNullOrWhiteSpace(logEvent.ProcessId))
        {
            json["processId"] = logEvent.ProcessId;
        }

        return json;
    }

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    [GeneratedRegex(@"^(?<timestamp>\d{4}-\d{2}-\d{2}T\S+)\s*(?:\[pid:(?<pid>\d+)\])?\s*(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LogLineRegex();

    private sealed class LogCounters
    {
        public int EventCount { get; private set; }

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        public int LifecycleCount { get; private set; }

        public int RequestCount { get; private set; }

        public void Add(LogEvent logEvent)
        {
            EventCount++;
            if (string.Equals(logEvent.Severity, "error", StringComparison.Ordinal))
            {
                ErrorCount++;
            }
            else if (string.Equals(logEvent.Severity, "warning", StringComparison.Ordinal))
            {
                WarningCount++;
            }

            if (string.Equals(logEvent.Kind, "lifecycle", StringComparison.Ordinal))
            {
                LifecycleCount++;
            }
            else if (string.Equals(logEvent.Kind, "request", StringComparison.Ordinal))
            {
                RequestCount++;
            }
        }
    }

    private readonly record struct LogTarget(string Kind, string Path);

    private readonly record struct LogLine(int LineIndex, string? Timestamp, string? ProcessId, string Message, string Line);

    private readonly record struct LogClassification(string Severity, string Kind);

    private readonly record struct LogEvent(
        int TailLine,
        string? Timestamp,
        string? ProcessId,
        string Severity,
        string Kind,
        string Message,
        string Line);
}
