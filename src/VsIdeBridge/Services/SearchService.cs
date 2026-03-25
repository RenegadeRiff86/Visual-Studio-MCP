using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindResults;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private const string FunctionKind = "function";
    private const string InterfaceKind = "interface";
    private const string PathFilterPropertyName = "pathFilter";

    private sealed class SearchHit
    {
        public string Path { get; set; } = string.Empty;

        public string ProjectUniqueName { get; set; } = string.Empty;

        public int Line { get; set; }

        public int Column { get; set; }

        public int MatchLength { get; set; }

        public string Preview { get; set; } = string.Empty;

        public int ScoreHint { get; set; }

        public List<string> SourceQueries { get; set; } = [];
    }

    private sealed class CodeModelHit
    {
        public string Path { get; set; } = string.Empty;

        public string ProjectUniqueName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string Signature { get; set; } = string.Empty;

        public int Line { get; set; }

        public int EndLine { get; set; }

        public int Score { get; set; }

        public string MatchKind { get; set; } = string.Empty;
    }

    private sealed class SmartQueryTerm
    {
        public string Text { get; set; } = string.Empty;

        public int Weight { get; set; }

        public bool WholeWord { get; set; }
    }

    private static readonly HashSet<vsCMElement> s_codeModelKinds =
    [
        vsCMElement.vsCMElementFunction,
        vsCMElement.vsCMElementClass,
        vsCMElement.vsCMElementStruct,
        vsCMElement.vsCMElementEnum,
        vsCMElement.vsCMElementNamespace,
        vsCMElement.vsCMElementInterface,
        vsCMElement.vsCMElementProperty,
        vsCMElement.vsCMElementVariable,
    ];

    public async Task<JObject> FindFilesAsync(
        IdeCommandContext context,
        string query,
        string? pathFilter,
        IReadOnlyCollection<string> extensions,
        int maxResults,
        bool includeNonProject)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        Dictionary<string, SolutionFileLocator.Match> merged = new(StringComparer.OrdinalIgnoreCase);

        foreach (SolutionFileLocator.Match fileMatch in SolutionFileLocator.FindMatches(context.Dte, query, pathFilter, extensions))
        {
            merged[fileMatch.Path] = fileMatch;
        }

        if (includeNonProject)
        {
            foreach (SolutionFileLocator.Match fileMatch in SolutionFileLocator.FindDiskMatches(context.Dte, query, pathFilter, extensions, Math.Max(100, maxResults * 2)))
            {
                if (!merged.TryGetValue(fileMatch.Path, out SolutionFileLocator.Match existing) || fileMatch.Score > existing.Score)
                {
                    merged[fileMatch.Path] = fileMatch;
                }
            }
        }

        SolutionFileLocator.Match[] items = merged.Values
            .OrderByDescending(fileMatch => fileMatch.Score)
            .ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxResults))
            .ToArray();

        JArray matches = new(items.Select(item => new JObject
        {
            ["path"] = item.Path,
            ["name"] = Path.GetFileName(item.Path),
            ["project"] = item.ProjectUniqueName,
            ["score"] = item.Score,
            ["source"] = item.Source,
        }));

        return new JObject
        {
            ["query"] = query,
            [PathFilterPropertyName] = pathFilter ?? string.Empty,
            ["extensions"] = new JArray(extensions.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)),
            ["includeNonProject"] = includeNonProject,
            ["count"] = matches.Count,
            ["matches"] = matches,
        };
    }

    public async Task<JObject> FindTextAsync(
        IdeCommandContext context,
        string query,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        int resultsWindow,
        string? projectUniqueName,
        string? pathFilter = null)
    {
        (List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches) = await SearchTextMatchesAsync(
            context, query, scope, matchCase, wholeWord, useRegex, projectUniqueName, pathFilter).ConfigureAwait(true);

        await PopulateFindResultsAsync(context, GroupedMatches, query, resultsWindow).ConfigureAwait(true);

        return new JObject
        {
            ["query"] = query,
            ["scope"] = scope,
            [PathFilterPropertyName] = pathFilter ?? string.Empty,
            ["count"] = Matches.Count,
            ["resultsWindow"] = resultsWindow,
            ["matches"] = new JArray(Matches.Select(SerializeHit)),
        };
    }

    public async Task<JObject> FindTextBatchAsync(
        IdeCommandContext context,
        IReadOnlyList<string> queries,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        int resultsWindow,
        string? projectUniqueName,
        string? pathFilter,
        int maxQueriesPerChunk)
    {
        List<string> normalizedQueries = NormalizeQueries(queries);
        if (normalizedQueries.Count == 0)
        {
            throw new ArgumentException("At least one non-empty query is required.", nameof(queries));
        }

        int chunkSize = Math.Max(1, maxQueriesPerChunk);
        Dictionary<string, SearchHit> mergedHits = new(StringComparer.OrdinalIgnoreCase);
        JArray queryResults = new();
        JArray chunks = new();
        int totalMatchCount = await ExecuteBatchChunksAsync(
            context, normalizedQueries, scope, matchCase, wholeWord, useRegex,
            projectUniqueName, pathFilter, chunkSize, mergedHits, queryResults, chunks).ConfigureAwait(true);

        SearchHit[] orderedMergedHits = mergedHits.Values
            .OrderBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Line)
            .ThenBy(hit => hit.Column)
            .ToArray();
        Dictionary<string, List<FindResult>> groupedMatches = BuildGroupedMatchesFromHits(orderedMergedHits);
        string summaryQuery = normalizedQueries.Count == 1
            ? normalizedQueries[0]
            : $"{normalizedQueries.Count} batched queries";
        await PopulateFindResultsAsync(context, groupedMatches, summaryQuery, resultsWindow).ConfigureAwait(true);

        return new JObject
        {
            ["queries"] = new JArray(normalizedQueries),
            ["scope"] = scope,
            [PathFilterPropertyName] = pathFilter ?? string.Empty,
            ["queryCount"] = normalizedQueries.Count,
            ["chunkCount"] = chunks.Count,
            ["maxQueriesPerChunk"] = chunkSize,
            ["count"] = orderedMergedHits.Length,
            ["totalMatchCount"] = totalMatchCount,
            ["resultsWindow"] = resultsWindow,
            ["chunks"] = chunks,
            ["queryResults"] = queryResults,
            ["matches"] = new JArray(orderedMergedHits.Select(SerializeHit)),
        };
    }

    private async Task<int> ExecuteBatchChunksAsync(
        IdeCommandContext context,
        IReadOnlyList<string> normalizedQueries,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        string? projectUniqueName,
        string? pathFilter,
        int chunkSize,
        Dictionary<string, SearchHit> mergedHits,
        JArray queryResults,
        JArray chunks)
    {
        int totalMatchCount = 0;
        for (int start = 0; start < normalizedQueries.Count; start += chunkSize)
        {
            string[] chunkQueries = normalizedQueries.Skip(start).Take(chunkSize).ToArray();
            int chunkMatchCount = 0;
            foreach (string query in chunkQueries)
            {
                (List<SearchHit> matches, _) = await SearchTextMatchesAsync(
                    context, query, scope, matchCase, wholeWord, useRegex,
                    projectUniqueName, pathFilter).ConfigureAwait(true);
                totalMatchCount += matches.Count;
                chunkMatchCount += matches.Count;
                MergeSearchHits(mergedHits, matches);
                queryResults.Add(new JObject
                {
                    ["query"] = query,
                    ["count"] = matches.Count,
                    ["matches"] = new JArray(matches.Select(SerializeHit)),
                });
            }

            chunks.Add(new JObject
            {
                ["index"] = (start / chunkSize) + 1,
                ["queryCount"] = chunkQueries.Length,
                ["queries"] = new JArray(chunkQueries),
                ["matchCount"] = chunkMatchCount,
            });
        }

        return totalMatchCount;
    }

    public async Task<JObject> SearchSymbolsAsync(
        IdeCommandContext context,
        string name,
        string kind,
        string scope,
        bool matchCase,
        string? projectUniqueName,
        string? pathFilter,
        int max)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        CodeModelHit[] codeModelHits = SearchCodeModelSymbols(
            context.Dte, name, kind, scope, matchCase, projectUniqueName, pathFilter)
            .Take(Math.Max(1, max))
            .ToArray();

        if (codeModelHits.Length > 0)
        {
            return new JObject
            {
                ["query"] = name,
                ["kind"] = kind,
                ["scope"] = scope,
                ["project"] = projectUniqueName ?? string.Empty,
                [PathFilterPropertyName] = pathFilter ?? string.Empty,
                ["count"] = codeModelHits.Length,
                ["totalMatchCount"] = codeModelHits.Length,
                ["source"] = "code-model",
                ["matches"] = new JArray(codeModelHits.Select(SerializeCodeModelHit)),
            };
        }

        (string pattern, string resolvedKind) = BuildSymbolTextPattern(name, kind);

        (List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches) = await SearchTextMatchesAsync(
            context, pattern, scope, matchCase, wholeWord: false, useRegex: true,
            projectUniqueName, pathFilter).ConfigureAwait(true);

        JObject[] hits = Matches
            .Take(max)
            .Select(hit =>
            {
                string inferredKind = resolvedKind == "all" ? InferSymbolKind(hit.Preview, name) : resolvedKind;
                JObject obj = SerializeHit(hit);
                obj["inferredKind"] = inferredKind;
                return obj;
            })
            .ToArray();

        return new JObject
        {
            ["query"] = name,
            ["kind"] = kind,
            ["scope"] = scope,
            ["project"] = projectUniqueName ?? string.Empty,
            [PathFilterPropertyName] = pathFilter ?? string.Empty,
            ["count"] = hits.Length,
            ["totalMatchCount"] = Matches.Count,
            ["source"] = "text",
            ["matches"] = new JArray(hits),
        };
    }

    private static (string Pattern, string ResolvedKind) BuildSymbolTextPattern(string name, string kind)
    {
        string escaped = Regex.Escape(name);
        switch (kind.ToLowerInvariant())
        {
            case FunctionKind: return ($@"\b{escaped}\s*\(", FunctionKind);
            case "class": return ($@"\bclass\s+{escaped}\b", "class");
            case "struct": return ($@"\bstruct\s+{escaped}\b", "struct");
            case "enum": return ($@"\benum(?:\s+class)?\s+{escaped}\b", "enum");
            case "namespace": return ($@"\bnamespace\s+{escaped}\b", "namespace");
            case InterfaceKind: return ($@"\binterface\s+{escaped}\b", InterfaceKind);
            case "member": return ($@"\b{escaped}\b", "member");
            case "type": return ($@"\b{escaped}\b", "type");
            default: return ($@"\b{escaped}\b", "all"); // "all" — whole-word match; kind inferred per-hit
        }
    }

    private static string InferSymbolKind(string lineText, string name)
    {
        string trimmed = lineText.TrimStart();
        if (Regex.IsMatch(trimmed, $@"\bclass\s+{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return "class";
        if (Regex.IsMatch(trimmed, $@"\bstruct\s+{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return "struct";
        if (Regex.IsMatch(trimmed, $@"\benum(?:\s+class)?\s+{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return "enum";
        if (Regex.IsMatch(trimmed, $@"\bnamespace\s+{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return "namespace";
        if (Regex.IsMatch(trimmed, $@"\binterface\s+{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return InterfaceKind;
        if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(name)}\s*\(", RegexOptions.IgnoreCase)) return FunctionKind;
        return "unknown";
    }

    public async Task<JObject> GetSmartContextForQueryAsync(
        IdeCommandContext context,
        string query,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        string? projectUniqueName,
        int maxContexts,
        int contextBefore,
        int contextAfter,
        bool populateResultsWindow,
        int resultsWindow)
    {
        (List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches) = useRegex
            ? await SearchTextMatchesAsync(context, query, scope, matchCase, wholeWord, true, projectUniqueName).ConfigureAwait(true)
            : await SearchSmartQueryTermsAsync(context, query, scope, projectUniqueName).ConfigureAwait(true);

        IReadOnlyList<string> searchTerms = useRegex
            ? [query]
            : [.. ExtractSmartQueryTerms(query).Select(term => term.Text).Distinct(StringComparer.OrdinalIgnoreCase)];

        if (populateResultsWindow)
        {
            await PopulateFindResultsAsync(context, GroupedMatches, query, resultsWindow).ConfigureAwait(true);
        }

        JArray contexts = BuildSmartContexts(Matches, contextBefore, contextAfter, maxContexts);

        return new JObject
        {
            ["query"] = query,
            ["scope"] = scope,
            ["searchTerms"] = new JArray(searchTerms),
            ["totalMatchCount"] = Matches.Count,
            ["contextCount"] = contexts.Count,
            ["populateResultsWindow"] = populateResultsWindow,
            ["resultsWindow"] = resultsWindow,
            ["contexts"] = contexts,
        };
    }
}
