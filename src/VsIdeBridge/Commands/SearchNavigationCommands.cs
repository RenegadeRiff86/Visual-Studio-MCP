using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SearchNavigationCommands
{
    private const string CountKey = "count";
    private const string DocumentArgument = "document";
    private const string DocumentScope = "document";
    private const string InvalidJsonErrorCode = "invalid_json";
    private const string ProjectScope = "project";
    private const string SolutionScope = "solution";
    private const string OpenScope = "open";

    private static CommandExecutionResult CreateFoundResult(string itemLabel, JObject data, string detailsLocation = "Data.matches")
    {
        _ = detailsLocation;
        int count = data[CountKey]?.Value<int>() ?? 0;
        string resultSummary = FormatResultSummary(data, itemLabel, count);

        return new CommandExecutionResult(resultSummary, data);
    }

    private static string FormatResultSummary(JObject data, string itemLabel, int count)
    {
        if (count == 0)
        {
            return $"No {itemLabel} found.";
        }

        // Try to extract meaningful details from the matches
        if (data["matches"] is JArray matches && matches.Count > 0)
        {
            string[] resultNames =
            [.. matches
                .OfType<JObject>()
                .Take(10)
                .Select(m => ExtractResultName(m))
                .Where(name => !string.IsNullOrWhiteSpace(name))];

            if (resultNames.Length > 0)
            {
                string resultList = string.Join(", ", resultNames);
                return count switch
                {
                    1 => $"Found 1 {itemLabel.TrimEnd('(', ')')}. Result: {resultList}",
                    _ when resultNames.Length < count => $"Found {count} {itemLabel} (showing first {resultNames.Length}): {resultList}",
                    _ => $"Found {count} {itemLabel}: {resultList}"
                };
            }
        }

        return count switch
        {
            1 => $"Found 1 {itemLabel.TrimEnd('(', ')')}.",
            _ => $"Found {count} {itemLabel}."
        };
    }

    private static string ExtractResultName(JObject match)
    {
        // Try different property names that might contain the result identifier
        return match["name"]?.ToString()
            ?? match["path"]?.ToString()
            ?? match["file"]?.ToString()
            ?? match["symbol"]?.ToString()
            ?? match["text"]?.ToString()
            ?? string.Empty;
    }

}
