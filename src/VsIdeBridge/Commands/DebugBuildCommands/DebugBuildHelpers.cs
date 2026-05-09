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
            Severity = args.GetString("severity") ?? defaultSeverity,
            Code = args.GetString("code"),
            Project = args.GetString("project"),
            Path = args.GetString("path"),
            File = args.GetString(FileArgument),
            Text = args.GetString("text"),
            GroupBy = args.GetString("group-by"),
            Max = GetDiagnosticsMax(args),
        };
    }

    private static int? GetDiagnosticsMax(CommandArguments args)
        => GetNullableInt32(args, ChunkSizeArgument, ChunkSizeJsonArgument, MaxArgument);

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
                advisories.Add($"Also found {count} {severity.ToLowerInvariant()}(s) — {callHint}.");
            }
        }
        return advisories;
    }
}
