using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class ErrorListService
{
    private const int StableSampleCount = 3;
    private const int PopulationPollIntervalMilliseconds = 2000;

    private static readonly Regex ExplicitCodePattern = new(
        @"\b(?:LINK|LNK|MSB|VCR|E|C)\d+\b|\blnt-[a-z0-9-]+\b|\bInt-make\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ReadinessService _readinessService;

    public ErrorListService(ReadinessService readinessService)
    {
        _readinessService = readinessService;
    }

    public async Task<JObject> GetErrorListAsync(IdeCommandContext context, bool waitForIntellisense, int timeoutMilliseconds)
    {
        if (waitForIntellisense)
        {
            await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        }

        var rows = await WaitForRowsAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        var severityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Error"] = 0,
            ["Warning"] = 0,
            ["Message"] = 0,
        };
        foreach (var row in rows)
        {
            severityCounts[(string)row["severity"]!] += 1;
        }

        return new JObject
        {
            ["count"] = rows.Count,
            ["severityCounts"] = JObject.FromObject(severityCounts),
            ["hasErrors"] = severityCounts["Error"] > 0,
            ["hasWarnings"] = severityCounts["Warning"] > 0,
            ["rows"] = new JArray(rows),
        };
    }

    private async Task<IReadOnlyList<JObject>> WaitForRowsAsync(IdeCommandContext context, int timeoutMilliseconds)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var timeout = timeoutMilliseconds > 0 ? timeoutMilliseconds : 90000;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        var lastRows = Array.Empty<JObject>();
        int? lastCount = null;
        var stableSamples = 0;

        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<JObject>? rows = null;
            try
            {
                rows = ReadRows(context.Dte);
            }
            catch (InvalidOperationException)
            {
            }

            if (rows is not null)
            {
                if (rows.Count != lastCount)
                {
                    lastCount = rows.Count;
                    stableSamples = 1;
                }
                else
                {
                    stableSamples++;
                }

                lastRows = rows.ToArray();
                if (rows.Count > 0 && stableSamples >= StableSampleCount)
                {
                    return rows;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return lastRows;
            }

            await Task.Delay(PopulationPollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        }
    }

    private static IReadOnlyList<JObject> ReadRows(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var window = dte.Windows
            .Cast<Window>()
            .FirstOrDefault(candidate => string.Equals(candidate.Caption, "Error List", StringComparison.OrdinalIgnoreCase));
        if (window?.Object is not ErrorList errorList)
        {
            throw new InvalidOperationException("Error List window is not available.");
        }

        var items = errorList.ErrorItems;
        var rows = new List<JObject>(items.Count);
        for (var i = 1; i <= items.Count; i++)
        {
            var item = items.Item(i);
            var severity = MapSeverity(item.ErrorLevel);
            rows.Add(new JObject
            {
                ["severity"] = severity,
                ["code"] = InferCode(item.Description ?? string.Empty, item.Project ?? string.Empty, item.FileName ?? string.Empty, item.Line),
                ["message"] = item.Description ?? string.Empty,
                ["project"] = item.Project ?? string.Empty,
                ["file"] = item.FileName ?? string.Empty,
                ["line"] = item.Line,
                ["column"] = item.Column,
            });
        }

        return rows;
    }

    private static string MapSeverity(vsBuildErrorLevel level)
    {
        return level switch
        {
            vsBuildErrorLevel.vsBuildErrorLevelHigh => "Error",
            vsBuildErrorLevel.vsBuildErrorLevelMedium => "Warning",
            _ => "Message",
        };
    }

    private static string InferCode(string description, string project, string fileName, int line)
    {
        var explicitCode = ExtractExplicitCode(description);
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        if (description.IndexOf("identifier \"", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("\" is undefined", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "E0020";
        }

        if (description.IndexOf("can be made static", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "VCR003";
        }

        if (description.IndexOf("can be made const", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "VCR001";
        }

        if (description.IndexOf("Return value ignored", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("UnregisterWaitEx", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "C6031";
        }

        if (description.IndexOf("PCH warning:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Int-make";
        }

        if (description.IndexOf("doesn't deduce references", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("possibly unintended copy", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "lnt-accidental-copy";
        }

        if (description.IndexOf("cannot open file '", StringComparison.OrdinalIgnoreCase) >= 0 &&
            IsLinkerContext(project, fileName, line))
        {
            return "LNK1104";
        }

        return string.Empty;
    }

    private static string ExtractExplicitCode(string description)
    {
        var match = ExplicitCodePattern.Match(description);
        return match.Success ? NormalizeCode(match.Value) : string.Empty;
    }

    private static string NormalizeCode(string code)
    {
        if (code.StartsWith("LINK", StringComparison.OrdinalIgnoreCase) &&
            code.Length > 4 &&
            int.TryParse(code.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return "LNK" + code.Substring(4);
        }

        if (code.StartsWith("lnt-", StringComparison.OrdinalIgnoreCase))
        {
            return code.ToLowerInvariant();
        }

        return code.ToUpperInvariant();
    }

    private static bool IsLinkerContext(string project, string fileName, int line)
    {
        var normalizedFile = (fileName ?? string.Empty).Replace('/', '\\');
        if (normalizedFile.EndsWith("\\LINK", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(fileName) && line <= 0;
    }
}
