using System.Text.RegularExpressions;

namespace VsIdeBridge.ServiceDomain;

/// <summary>
/// Severity levels for VS IDE Bridge extension log entries.
/// Ordinal values allow Level >= minLevel comparisons for severity filtering.
/// </summary>
public enum BridgeLogLevel { Info = 0, Warning = 1, Error = 2 }

/// <summary>
/// Keyword term lists and helpers shared by bridge log domain objects.
/// </summary>
public static class BridgeLogTerms
{
    public static readonly string[] ErrorTerms =
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

    public static readonly string[] WarningTerms =
    [
        "warning",
        "degraded",
        "fallback",
    ];

    public static readonly string[] LifecycleTerms =
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

    public static readonly string[] RequestTerms =
    [
        "request",
        "dispatch",
        "response",
        "tool complete",
        "tool cancelled",
        "stdout write",
    ];

    public static bool ContainsAny(string value, string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}

/// <summary>
/// A single parsed line from the VS IDE Bridge extension log.
/// Format: [yyyy-MM-dd HH:mm:ss] [LEVEL] [Source] message
/// Severity is taken directly from the parsed [LEVEL] bracket — no keyword guessing.
/// </summary>
public readonly record struct ExtensionLogEntry(
    int LineIndex,
    string? TimestampRaw,
    BridgeLogLevel Level,
    string? Source,
    string Message,
    string RawLine)
{
    /// <summary>Returns true when this entry meets or exceeds the minimum severity.</summary>
    public bool MatchesSeverity(BridgeLogLevel minLevel) => Level >= minLevel;

    /// <summary>Returns true when Source contains the filter string (case-insensitive).</summary>
    public bool MatchesSource(string filter) =>
        Source is not null && Source.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when the raw line contains the text (case-insensitive).</summary>
    public bool ContainsText(string text) =>
        RawLine.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A single parsed line from the VS IDE Bridge MCP server log.
/// Format: ISO_TIMESTAMP [pid:NNN] message
/// No explicit level tags — severity is inferred from message keywords via computed properties.
/// </summary>
public readonly record struct McpLogEntry(
    int LineIndex,
    string? TimestampRaw,
    string? ProcessId,
    string Message,
    string RawLine)
{
    public bool IsFailure  => BridgeLogTerms.ContainsAny(Message, BridgeLogTerms.ErrorTerms);
    public bool IsWarning  => BridgeLogTerms.ContainsAny(Message, BridgeLogTerms.WarningTerms);
    public bool IsLifecycle => BridgeLogTerms.ContainsAny(Message, BridgeLogTerms.LifecycleTerms);
    public bool IsRequest  => BridgeLogTerms.ContainsAny(Message, BridgeLogTerms.RequestTerms);

    /// <summary>Returns true when the raw line contains the text (case-insensitive).</summary>
    public bool ContainsText(string text) =>
        RawLine.Contains(text, StringComparison.OrdinalIgnoreCase);
}
