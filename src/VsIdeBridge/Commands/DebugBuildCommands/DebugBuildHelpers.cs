using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    private static ErrorListQuery CreateErrorListQuery(CommandArguments args, string? defaultSeverity = null)
    {
        return new ErrorListQuery
        {
            Severity      = args.GetString("severity") ?? defaultSeverity,
            Code          = args.GetString("code"),
            Project       = args.GetString("project"),
            Path          = args.GetString("path"),
            File          = args.GetString(FileArgument),
            Text          = args.GetString("text"),
            // Try CLI form (group-by) first, then MCP JSON form (group_by).
            GroupBy       = args.GetString("group-by") ?? args.GetString(GroupByJsonArgument),
            Max           = GetDiagnosticsMax(args),
            ChunkSize     = GetNullableInt32(args, ChunkSizeArgument, ChunkSizeJsonArgument),
            ChunkIndex    = GetNullableInt32(args, ChunkIndexArgument, ChunkIndexJsonArgument),
            SortBy        = args.GetString(SortByArgument)        ?? args.GetString(SortByJsonArgument),
            SortDirection = args.GetString(SortDirectionArgument) ?? args.GetString(SortDirectionJsonArgument),
        };
    }

    // Max is the legacy per-request limit (--max); ChunkSize is now separate.
    private static int? GetDiagnosticsMax(CommandArguments args)
        => args.GetNullableInt32(MaxArgument);

    private static int? GetNullableInt32(CommandArguments args, params string[] names)
    {
        foreach (string name in names)
        {
            int? value = args.GetNullableInt32(name);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static bool GetDiagnosticsForceRefresh(CommandArguments args)
        => args.GetBoolean("refresh", false);

    private static JObject FilterRowsBySeverity(JArray allRows, string severity, int? max)
    {
        IEnumerable<JToken> filtered = allRows
            .Where(r => string.Equals((string?)r["severity"], severity, StringComparison.OrdinalIgnoreCase));
        if (max is > 0)
            filtered = filtered.Take(max.Value);

        JToken[] commandResult = [.. filtered];
        return new JObject
        {
            ["count"] = commandResult.Length,
            ["rows"] = new JArray(commandResult.Select(r => r.DeepClone())),
        };
    }

    private static Task<JObject> GetSeverityDiagnosticsAsync(
        IdeCommandContext context,
        bool waitForIntellisense,
        int timeoutMilliseconds,
        bool quickSnapshot,
        string severity,
        int? max)
    {
        return GetDiagnosticsWithFallbackAsync(
            context,
            waitForIntellisense,
            timeoutMilliseconds,
            quickSnapshot,
            new ErrorListQuery
            {
                Severity = severity,
                Max = max,
            });
    }

    /// <summary>
    /// Builds a compact one-line summary that always shows counts for all three severity
    /// levels regardless of which severity was filtered. Models read Summary first, so
    /// embedding the full picture here prevents them from declaring victory on 0 errors
    /// while warnings or messages remain.
    /// Example: "0 errors � 1 warning � 2 messages � fix all before building."
    /// </summary>
    internal static string BuildDiagnosticsCountSummary(JObject result)
    {
        JToken? total = result["totalSeverityCounts"];
        int errors   = total?["Error"]?.Value<int>()   ?? 0;
        int warnings = total?["Warning"]?.Value<int>() ?? 0;
        int messages = total?["Message"]?.Value<int>() ?? 0;
        string counts = $"{errors} error(s) � {warnings} warning(s) � {messages} message(s)";
        return errors == 0 && warnings == 0 && messages == 0
            ? $"{counts} � clean."
            : $"{counts} � fix all before building.";
    }

    /// <summary>
    /// Produces bridge-level advisory warnings pointing to severity categories that were
    /// NOT the primary focus of this call, so the model is reminded to check them.
    /// </summary>
    private static JArray BuildDiagnosticsCrossReferenceAdvisories(JObject result, params string[] otherSeverities)
    {
        JToken? counts = result["totalSeverityCounts"];
        if (counts is null)
            return [];

        JArray advisories = [];
        foreach (string severity in otherSeverities)
        {
            int count = counts[severity]?.Value<int>() ?? 0;
            if (count > 0)
            {
                string callHint = severity switch
                {
                    "Error" => "run errors",
                    "Warning" => "run warnings",
                    "Message" => "run warnings with severity=Message",
                    _ => $"run {severity.ToLowerInvariant()}",
                };
                advisories.Add($"Also found {count} {severity.ToLowerInvariant()}(s) � {callHint}.");
            }
        }
        return advisories;
    }
}
