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

    private const string KindExtension = "extension";
    private const string KindMcp = "mcp";

    // Ordinal values enable Level >= minLevel comparisons for severity filtering.
    private enum BridgeLogLevel { Info = 0, Warning = 1, Error = 2 }

    // Typed domain object for extension log lines: [timestamp] [LEVEL] [Source] message
    private readonly record struct ExtensionLogEntry(
        int LineIndex,
        string? TimestampRaw,
        BridgeLogLevel Level,
        string? Source,
        string Message,
        string RawLine)
    {
        public bool MatchesSeverity(BridgeLogLevel minLevel) => Level >= minLevel;

        public bool MatchesSource(string filter) =>
            Source is not null && Source.Contains(filter, StringComparison.OrdinalIgnoreCase);

        public bool ContainsText(string text) =>
            RawLine.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // Typed domain object for MCP server log lines: ISO_TIMESTAMP [pid:NNN] message
    // No explicit level tags — severity is inferred from message keywords.
    private readonly record struct McpLogEntry(
        int LineIndex,
        string? TimestampRaw,
        string? ProcessId,
        string Message,
        string RawLine)
    {
        public bool IsFailure => ContainsAny(Message, ErrorTerms);
        public bool IsWarning => ContainsAny(Message, WarningTerms);
        public bool IsLifecycle => ContainsAny(Message, LifecycleTerms);
        public bool IsRequest => ContainsAny(Message, RequestTerms);

        public bool ContainsText(string text) =>
            RawLine.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public static Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection _)
    {
        string requestedLog = args?["log"]?.GetValue<string>() ?? "all";
        string selection = NormalizeLogSelection(id, requestedLog);
        int tailLines = Clamp(args?["tail_lines"]?.GetValue<int?>() ?? DefaultTailLines, 1, MaxTailLines);
        int maxEvents = Clamp(args?["max_events"]?.GetValue<int?>() ?? DefaultMaxEvents, 1, MaxEvents);
        string? textFilter = NormalizeOptionalString(args?["text"]?.GetValue<string>());
        bool includeRaw = args?["include_raw"]?.GetValue<bool?>() == true;
        string? severityArg = NormalizeOptionalString(args?["severity"]?.GetValue<string>());
        string? sourceFilter = NormalizeOptionalString(args?["source"]?.GetValue<string>());
        BridgeLogLevel minSeverity = ParseSeverityFilter(id, severityArg);

        string logDirectory = BridgeLogPaths.GetSharedLogDirectory();
        List<LogTarget> targets = ResolveTargets(logDirectory, selection);
        JsonArray files = [];
        LogCounters totals = new();
        int remainingEvents = maxEvents;

        foreach (LogTarget target in targets)
        {
            files.Add(BuildFileSummary(
                target, tailLines, textFilter, sourceFilter, minSeverity,
                includeRaw, ref remainingEvents, totals));
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
            ["severityFilter"] = severityArg ?? "all",
            ["sourceFilter"] = sourceFilter,
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
        string? sourceFilter,
        BridgeLogLevel minSeverity,
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

            // Apply raw text pre-filter before structured parsing
            IReadOnlyList<string> preFiltered = textFilter is null
                ? tail
                : [.. tail.Where(line => line.Contains(textFilter, StringComparison.OrdinalIgnoreCase))];

            result["tailLineCount"] = tail.Count;
            result["filteredLineCount"] = preFiltered.Count;

            JsonArray eventArray = [];
            LogCounters fileCounters = new();

            if (string.Equals(target.Kind, KindExtension, StringComparison.Ordinal))
            {
                BuildExtensionFileSummary(
                    preFiltered, sourceFilter, minSeverity, ref remainingEvents,
                    eventArray, fileCounters, totals, out bool extLimitReached);
                result["eventLimitReached"] = extLimitReached;
            }
            else
            {
                BuildMcpFileSummary(
                    preFiltered, ref remainingEvents,
                    eventArray, fileCounters, totals, out bool mcpLimitReached);
                result["eventLimitReached"] = mcpLimitReached;
            }

            result["events"] = eventArray;
            result["eventCount"] = fileCounters.EventCount;
            result["errorCount"] = fileCounters.ErrorCount;
            result["warningCount"] = fileCounters.WarningCount;
            result["lifecycleCount"] = fileCounters.LifecycleCount;
            result["requestCount"] = fileCounters.RequestCount;

            if (includeRaw)
            {
                JsonArray raw = [];
                foreach (string line in preFiltered.Take(RawLineLimit))
                {
                    raw.Add(Truncate(line, MaxLineChars));
                }

                result["rawTail"] = raw;
                result["rawLineLimit"] = RawLineLimit;
                result["rawTailTruncated"] = preFiltered.Count > RawLineLimit;
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

    private static void BuildExtensionFileSummary(
        IReadOnlyList<string> lines,
        string? sourceFilter,
        BridgeLogLevel minSeverity,
        ref int remainingEvents,
        JsonArray eventArray,
        LogCounters fileCounters,
        LogCounters totals,
        out bool truncated)
    {
        truncated = false;
        for (int i = 0; i < lines.Count; i++)
        {
            ExtensionLogEntry entry = ParseExtensionLine(i + 1, lines[i]);

            // Severity comes from the parsed [LEVEL] bracket — no keyword guessing.
            if (!entry.MatchesSeverity(minSeverity))
            {
                continue;
            }

            if (sourceFilter is not null && !entry.MatchesSource(sourceFilter))
            {
                continue;
            }

            // For info-level entries, only surface lines with lifecycle or request signals.
            if (entry.Level == BridgeLogLevel.Info)
            {
                string lower = entry.Message.ToLowerInvariant();
                if (!ContainsAny(lower, LifecycleTerms) && !ContainsAny(lower, RequestTerms))
                {
                    continue;
                }
            }

            if (eventArray.Count >= remainingEvents)
            {
                truncated = true;
                continue;
            }

            string severity = entry.Level switch
            {
                BridgeLogLevel.Error => "error",
                BridgeLogLevel.Warning => "warning",
                _ => "info",
            };

            string kind = entry.Level switch
            {
                BridgeLogLevel.Error => "failure",
                BridgeLogLevel.Warning => "warning",
                _ => ContainsAny(entry.Message.ToLowerInvariant(), LifecycleTerms) ? "lifecycle" : "request",
            };

            JsonObject json = new()
            {
                ["tailLine"] = entry.LineIndex,
                ["severity"] = severity,
                ["kind"] = kind,
                ["message"] = Truncate(entry.Message, MaxMessageChars),
                ["line"] = Truncate(entry.RawLine, MaxLineChars),
            };

            if (!string.IsNullOrWhiteSpace(entry.TimestampRaw))
            {
                json["timestamp"] = entry.TimestampRaw;
            }

            if (!string.IsNullOrWhiteSpace(entry.Source))
            {
                json["source"] = entry.Source;
            }

            eventArray.Add(json);
            fileCounters.AddExtension(entry.Level);
            totals.AddExtension(entry.Level);
        }

        remainingEvents = Math.Max(0, remainingEvents - eventArray.Count);
    }

    private static void BuildMcpFileSummary(
        IReadOnlyList<string> lines,
        ref int remainingEvents,
        JsonArray eventArray,
        LogCounters fileCounters,
        LogCounters totals,
        out bool truncated)
    {
        truncated = false;
        for (int i = 0; i < lines.Count; i++)
        {
            McpLogEntry entry = ParseMcpLine(i + 1, lines[i]);

            // Severity is keyword-inferred since MCP log has no explicit level tags.
            string severity;
            string kind;

            if (entry.IsFailure)
            {
                severity = "error";
                kind = "failure";
            }
            else if (entry.IsWarning)
            {
                severity = "warning";
                kind = "warning";
            }
            else if (entry.IsLifecycle)
            {
                severity = "info";
                kind = "lifecycle";
            }
            else if (entry.IsRequest)
            {
                severity = "info";
                kind = "request";
            }
            else
            {
                continue;
            }

            if (eventArray.Count >= remainingEvents)
            {
                truncated = true;
                continue;
            }

            JsonObject json = new()
            {
                ["tailLine"] = entry.LineIndex,
                ["severity"] = severity,
                ["kind"] = kind,
                ["message"] = Truncate(entry.Message, MaxMessageChars),
                ["line"] = Truncate(entry.RawLine, MaxLineChars),
            };

            if (!string.IsNullOrWhiteSpace(entry.TimestampRaw))
            {
                json["timestamp"] = entry.TimestampRaw;
            }

            if (!string.IsNullOrWhiteSpace(entry.ProcessId))
            {
                json["processId"] = entry.ProcessId;
            }

            eventArray.Add(json);
            fileCounters.AddMcp(severity, kind);
            totals.AddMcp(severity, kind);
        }

        remainingEvents = Math.Max(0, remainingEvents - eventArray.Count);
    }

    private static List<LogTarget> ResolveTargets(string logDirectory, string selection)
    {
        List<LogTarget> targets = [];
        if (selection is "all" or KindMcp)
        {
            targets.Add(new(KindMcp, BridgeLogPaths.GetMcpServerLogPath()));
        }

        if (selection is "all" or KindExtension)
        {
            targets.Add(new(KindExtension, ResolveLatestExtensionLog(logDirectory)));
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
            "mcp" or "mcp_server" => KindMcp,
            "extension" or "vsix" or "visual_studio" or "visualstudio" => KindExtension,
            _ => throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                "Argument 'log' must be one of: all, mcp, extension, or vsix."),
        };
    }

    private static BridgeLogLevel ParseSeverityFilter(JsonNode? id, string? value)
    {
        if (value is null)
        {
            return BridgeLogLevel.Info;
        }

        return value.ToLowerInvariant() switch
        {
            "all" or "info" => BridgeLogLevel.Info,
            "warning" or "warn" => BridgeLogLevel.Warning,
            "error" => BridgeLogLevel.Error,
            _ => throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                "Argument 'severity' must be one of: all, warning, or error."),
        };
    }

    private static BridgeLogLevel ParseBracketLevel(string levelText) =>
        levelText.ToUpperInvariant() switch
        {
            "ERROR" or "FATAL" or "CRITICAL" => BridgeLogLevel.Error,
            "WARNING" or "WARN" => BridgeLogLevel.Warning,
            _ => BridgeLogLevel.Info,
        };

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

    private static ExtensionLogEntry ParseExtensionLine(int lineIndex, string line)
    {
        Match match = ExtensionLogLineRegex().Match(line);
        if (!match.Success)
        {
            return new(lineIndex, null, BridgeLogLevel.Info, null, line.Trim(), Truncate(line, MaxLineChars));
        }

        string? timestamp = NormalizeOptionalString(match.Groups["timestamp"].Value);
        BridgeLogLevel level = ParseBracketLevel(match.Groups["level"].Value);
        string? source = NormalizeOptionalString(match.Groups["source"].Value);
        string message = NormalizeOptionalString(match.Groups["message"].Value) ?? line.Trim();
        return new(lineIndex, timestamp, level, source, message, Truncate(line, MaxLineChars));
    }

    private static McpLogEntry ParseMcpLine(int lineIndex, string line)
    {
        Match match = McpLogLineRegex().Match(line);
        if (!match.Success)
        {
            return new(lineIndex, null, null, line.Trim(), Truncate(line, MaxLineChars));
        }

        string? timestamp = NormalizeOptionalString(match.Groups["timestamp"].Value);
        string? processId = NormalizeOptionalString(match.Groups["pid"].Value);
        string message = NormalizeOptionalString(match.Groups["message"].Value) ?? line.Trim();
        return new(lineIndex, timestamp, processId, message, Truncate(line, MaxLineChars));
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    // Extension log format: [yyyy-MM-dd HH:mm:ss] [LEVEL] [Source] message
    [GeneratedRegex(@"^\[(?<timestamp>[^\]]+)\]\s+\[(?<level>[^\]]+)\]\s+\[(?<source>[^\]]+)\]\s+(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionLogLineRegex();

    // MCP server log format: ISO_TIMESTAMP [pid:NNN] message
    [GeneratedRegex(@"^(?<timestamp>\d{4}-\d{2}-\d{2}T\S+)\s*(?:\[pid:(?<pid>\d+)\])?\s*(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex McpLogLineRegex();

    private sealed class LogCounters
    {
        public int EventCount { get; private set; }

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        public int LifecycleCount { get; private set; }

        public int RequestCount { get; private set; }

        public void AddExtension(BridgeLogLevel level)
        {
            EventCount++;
            if (level == BridgeLogLevel.Error)
            {
                ErrorCount++;
            }
            else if (level == BridgeLogLevel.Warning)
            {
                WarningCount++;
            }
        }

        public void AddMcp(string severity, string kind)
        {
            EventCount++;
            if (string.Equals(severity, "error", StringComparison.Ordinal))
            {
                ErrorCount++;
            }
            else if (string.Equals(severity, "warning", StringComparison.Ordinal))
            {
                WarningCount++;
            }

            if (string.Equals(kind, "lifecycle", StringComparison.Ordinal))
            {
                LifecycleCount++;
            }
            else if (string.Equals(kind, "request", StringComparison.Ordinal))
            {
                RequestCount++;
            }
        }
    }

    private readonly record struct LogTarget(string Kind, string Path);
}
