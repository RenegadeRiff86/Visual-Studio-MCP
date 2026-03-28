using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class FailureContextService
{
    private const int MaxSymbolFiles = 3;
    private const int SymbolMaxDepth = 4;
    private const int MaxErrorSymbolRows = 8;
    private const int MaxRelevantSymbolsPerRow = 5;
    private const int ErrorListTimeoutMilliseconds = 1_500;

    public async Task<JObject> CaptureAsync(IdeCommandContext? context)
    {
        if (context is null)
        {
            return [];
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        JObject failureContext = new JObject();
        JObject? state = null;
        JObject? errorList = null;

        try
        {
            state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            failureContext["state"] = state;
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(nameof(FailureContextService), $"Failed to capture state: {ex.Message}");
        }

        try
        {
            JObject? openTabs = await context.Runtime.DocumentService.ListOpenTabsAsync(context.Dte).ConfigureAwait(true);
            failureContext["openTabs"] = openTabs;
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(nameof(FailureContextService), $"Failed to capture open tabs: {ex.Message}");
        }

        try
        {
            errorList = await context.Runtime.ErrorListService
                .GetErrorListAsync(context, waitForIntellisense: false, timeoutMilliseconds: ErrorListTimeoutMilliseconds, quickSnapshot: true)
                .ConfigureAwait(true);
            failureContext["errorList"] = errorList;
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(nameof(FailureContextService), $"Failed to capture error list: {ex.Message}");
        }

        Dictionary<string, JObject> outlineCache = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> symbolFiles = CollectSymbolFiles(state, errorList);
        JArray symbolContext = await BuildSymbolContextAsync(context, outlineCache, symbolFiles).ConfigureAwait(true);
        if (symbolContext.Count > 0)
            failureContext["symbolContext"] = symbolContext;

        JArray errorSymbolContext = BuildErrorSymbolContext(errorList, outlineCache);
        if (errorSymbolContext.Count > 0)
        {
            failureContext["errorSymbolContext"] = errorSymbolContext;
        }

        return failureContext;
    }

    private static async Task<JArray> BuildSymbolContextAsync(
        IdeCommandContext context,
        Dictionary<string, JObject> outlineCache,
        IReadOnlyList<string> symbolFiles)
    {
        JArray symbolContext = new JArray();
        foreach (string file in symbolFiles.Take(MaxSymbolFiles))
        {
            try
            {
                JObject outline = await GetOutlineAsync(context, outlineCache, file).ConfigureAwait(true);
                symbolContext.Add(new JObject { ["path"] = file, ["outline"] = outline });
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(FailureContextService), $"Failed to capture outline for '{file}': {ex.Message}");
            }
        }
        return symbolContext;
    }

    private static JArray BuildErrorSymbolContext(JObject? errorList, IReadOnlyDictionary<string, JObject> outlineCache)
    {
        JArray items = new JArray();
        if (errorList?["rows"] is not JArray rows)
        {
            return items;
        }

        foreach (JObject row in rows.OfType<JObject>().Take(MaxErrorSymbolRows))
        {
            string? file = row["file"]?.Value<string>();
            int line = row["line"]?.Value<int>() ?? 0;
            if (string.IsNullOrWhiteSpace(file) || line <= 0)
            {
                continue;
            }

            string normalizedFile = PathNormalization.NormalizeFilePath(file);
            if (!outlineCache.TryGetValue(normalizedFile, out JObject? outline))
            {
                continue;
            }

            JArray relevantSymbols = SelectRelevantSymbols(outline, line);
            if (relevantSymbols.Count == 0)
            {
                continue;
            }

            items.Add(new JObject
            {
                ["file"] = normalizedFile,
                ["line"] = line,
                ["column"] = row["column"] ?? 0,
                ["severity"] = row["severity"] ?? string.Empty,
                ["code"] = row["code"] ?? string.Empty,
                ["message"] = row["message"] ?? string.Empty,
                ["relevantSymbols"] = relevantSymbols,
            });
        }

        return items;
    }

    private static JArray SelectRelevantSymbols(JObject outline, int line)
    {
        if (outline["symbols"] is not JArray symbols)
        {
            return [];
        }

        List<JObject> containing = symbols
            .OfType<JObject>()
            .Where(symbol => ContainsLine(symbol, line))
            .OrderBy(symbol => (symbol["endLine"]?.Value<int>() ?? int.MaxValue) - (symbol["startLine"]?.Value<int>() ?? 0))
            .ThenBy(symbol => symbol["depth"]?.Value<int>() ?? int.MaxValue)
            .Take(MaxRelevantSymbolsPerRow)
            .ToList();

        if (containing.Count > 0)
        {
            return [.. containing.Select(CloneSymbol)];
        }

        IEnumerable<JObject> nearby = symbols
            .OfType<JObject>()
            .Select(symbol => new
            {
                Symbol = symbol,
                Distance = DistanceFromLine(symbol, line),
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Symbol["depth"]?.Value<int>() ?? int.MaxValue)
            .Take(MaxRelevantSymbolsPerRow)
            .Select(item => CloneSymbol(item.Symbol));

        return [.. nearby];
    }

    private static bool ContainsLine(JObject symbol, int line)
    {
        int startLine = symbol["startLine"]?.Value<int>() ?? 0;
        int endLine = symbol["endLine"]?.Value<int>() ?? startLine;
        return startLine > 0 && line >= startLine && line <= Math.Max(startLine, endLine);
    }

    private static int DistanceFromLine(JObject symbol, int line)
    {
        int startLine = symbol["startLine"]?.Value<int>() ?? int.MaxValue;
        int endLine = symbol["endLine"]?.Value<int>() ?? startLine;
        if (line < startLine)
        {
            return startLine - line;
        }

        if (line > endLine)
        {
            return line - endLine;
        }

        return 0;
    }

    private static JObject CloneSymbol(JObject symbol)
    {
        return new JObject
        {
            ["name"] = symbol["name"] ?? string.Empty,
            ["kind"] = symbol["kind"] ?? string.Empty,
            ["startLine"] = symbol["startLine"] ?? 0,
            ["endLine"] = symbol["endLine"] ?? 0,
            ["depth"] = symbol["depth"] ?? 0,
        };
    }

    private static async Task<JObject> GetOutlineAsync(
        IdeCommandContext context,
        IDictionary<string, JObject> outlineCache,
        string file)
    {
        if (outlineCache.TryGetValue(file, out JObject? cached))
        {
            return cached;
        }

        JObject outline = await context.Runtime.DocumentService
            .GetFileOutlineAsync(context.Dte, file, SymbolMaxDepth, kindFilter: null)
            .ConfigureAwait(true);
        outlineCache[file] = outline;
        return outline;
    }

    private static List<string> CollectSymbolFiles(JObject? state, JObject? errorList)
    {
        List<string> files = new List<string>();

        string? activeDocument = state?["activeDocument"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(activeDocument))
        {
            files.Add(PathNormalization.NormalizeFilePath(activeDocument));
        }

        if (errorList?["rows"] is JArray rows)
        {
            foreach (JObject row in rows.OfType<JObject>())
            {
                string? file = row["file"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(file))
                    continue;
                files.Add(PathNormalization.NormalizeFilePath(file));
            }
        }

        return [.. files
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
