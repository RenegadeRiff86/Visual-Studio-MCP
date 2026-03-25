using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindResults;
using Microsoft.VisualStudio.Text;
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
    private async Task PopulateFindResultsAsync(
        IdeCommandContext context,
        IReadOnlyDictionary<string, List<FindResult>> groupedMatches,
        string query,
        int resultsWindow)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (await context.Package.GetServiceAsync(typeof(SVsFindResults)).ConfigureAwait(true) is not IFindResultsService service)
        {
            return;
        }

        string title = $"IDE Bridge Find Results {resultsWindow}";
        string description = $"Find all \"{query}\"";
        string identifier = $"VsIdeBridge.FindResults.{resultsWindow}";
        IFindResultsWindow2 window = service.StartSearch(title, description, identifier);
        foreach (KeyValuePair<string, List<FindResult>> resultGroup in groupedMatches)
        {
            window.AddResults(resultGroup.Key, resultGroup.Key, null, resultGroup.Value);
        }

        window.Summary = $"Matching lines: {groupedMatches.Sum(resultGroup => resultGroup.Value.Count)} Matching files: {groupedMatches.Count}";
        window.Complete();
    }

    private static Regex BuildRegex(string query, bool matchCase, bool wholeWord, bool useRegex)
    {
        string pattern = useRegex ? query : Regex.Escape(query);
        if (wholeWord)
        {
            pattern = $@"\b{pattern}\b";
        }

        RegexOptions options = RegexOptions.Compiled;
        if (!matchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options);
    }

    private async Task<(List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches)> SearchTextMatchesAsync(
        IdeCommandContext context,
        string query,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        string? projectUniqueName,
        string? pathFilter = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        string? normalizedPathFilter = NormalizeSearchPathFilter(context.Dte, pathFilter);

        (string Path, string ProjectUniqueName)[] allFiles = scope switch
        {
            "document" => new[] { await GetDocumentTargetAsync(context, normalizedPathFilter).ConfigureAwait(true) },
            "open" => [.. EnumerateOpenFiles(context.Dte)],
            "project" => [.. EnumerateSolutionFiles(context.Dte).Where(item => string.Equals(item.ProjectUniqueName, projectUniqueName, StringComparison.OrdinalIgnoreCase))],
            _ => [.. EnumerateSolutionFiles(context.Dte)],
        };

        (string Path, string ProjectUniqueName)[] files = string.IsNullOrWhiteSpace(normalizedPathFilter)
            ? allFiles
            : [.. allFiles.Where(file => MatchesPathFilter(file.Path, normalizedPathFilter))];

        Regex regex = BuildRegex(query, matchCase, wholeWord, useRegex);
        List<SearchHit> hits = [];
        Dictionary<string, List<FindResult>> groupedMatches = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string Path, string ProjectUniqueName) file in files)
        {
            if (!File.Exists(file.Path))
            {
                continue;
            }

            string[] lines = ReadSearchLines(context.Dte, file.Path);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                foreach (Match match in regex.Matches(line))
                {
                    hits.Add(new SearchHit
                    {
                        Path = file.Path,
                        ProjectUniqueName = file.ProjectUniqueName,
                        Line = lineIndex + 1,
                        Column = match.Index + 1,
                        MatchLength = match.Length,
                        Preview = line,
                        ScoreHint = 0,
                        SourceQueries = [query],
                    });

                    if (!groupedMatches.TryGetValue(file.Path, out List<FindResult>? results))
                    {
                        results = [];
                        groupedMatches[file.Path] = results;
                    }

                    results.Add(new FindResult(line, lineIndex, match.Index, new Span(match.Index, match.Length)));
                }
            }
        }

        return (hits, groupedMatches);
    }

    private async Task<(List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches)> SearchSmartQueryTermsAsync(
        IdeCommandContext context,
        string query,
        string scope,
        string? projectUniqueName)
    {
        IReadOnlyList<SmartQueryTerm> terms = ExtractSmartQueryTerms(query);
        Dictionary<string, SearchHit> hitMap = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<FindResult>> groupedMatches = new(StringComparer.OrdinalIgnoreCase);

        foreach (SmartQueryTerm term in terms)
        {
            (List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches) = await SearchTextMatchesAsync(
                context,
                term.Text,
                scope,
                matchCase: false,
                wholeWord: term.WholeWord,
                useRegex: false,
                projectUniqueName).ConfigureAwait(true);

            foreach (SearchHit hit in Matches)
            {
                string key = $"{hit.Path}|{hit.Line}|{hit.Column}";

                if (!hitMap.TryGetValue(key, out SearchHit? existing))
                {
                    existing = new SearchHit
                    {
                        Path = hit.Path,
                        ProjectUniqueName = hit.ProjectUniqueName,
                        Line = hit.Line,
                        Column = hit.Column,
                        MatchLength = hit.MatchLength,
                        Preview = hit.Preview,
                        ScoreHint = 0,
                    };
                    hitMap[key] = existing;
                }

                existing.ScoreHint += term.Weight;
                if (!existing.SourceQueries.Contains(term.Text, StringComparer.OrdinalIgnoreCase))
                {
                    existing.SourceQueries.Add(term.Text);
                }

                if (!groupedMatches.TryGetValue(hit.Path, out List<FindResult>? results))
                {
                    results = [];
                    groupedMatches[hit.Path] = results;
                }

                results.Add(new FindResult(hit.Preview, hit.Line - 1, hit.Column - 1, new Span(hit.Column - 1, hit.MatchLength)));
            }
        }

        return (hitMap.Values
            .OrderByDescending(hit => hit.ScoreHint)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Line)
            .ToList(), groupedMatches);
    }

    private static List<string> NormalizeQueries(IEnumerable<string> queries)
    {
        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? query in queries)
        {
            string? trimmed = query?.Trim();
            if (trimmed is not { Length: > 0 })
            {
                continue;
            }

            if (!seen.Add(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    private static void MergeSearchHits(Dictionary<string, SearchHit> mergedHits, IEnumerable<SearchHit> hits)
    {
        foreach (SearchHit hit in hits)
        {
            string key = GetSearchHitKey(hit);
            if (!mergedHits.TryGetValue(key, out SearchHit? existing))
            {
                mergedHits[key] = new SearchHit
                {
                    Path = hit.Path,
                    ProjectUniqueName = hit.ProjectUniqueName,
                    Line = hit.Line,
                    Column = hit.Column,
                    MatchLength = hit.MatchLength,
                    Preview = hit.Preview,
                    ScoreHint = hit.ScoreHint,
                    SourceQueries = [.. hit.SourceQueries],
                };
                continue;
            }

            existing.ScoreHint = Math.Max(existing.ScoreHint, hit.ScoreHint);
            foreach (string query in hit.SourceQueries)
            {
                if (!existing.SourceQueries.Contains(query, StringComparer.OrdinalIgnoreCase))
                {
                    existing.SourceQueries.Add(query);
                }
            }
        }
    }

    private static Dictionary<string, List<FindResult>> BuildGroupedMatchesFromHits(IEnumerable<SearchHit> hits)
    {
        Dictionary<string, List<FindResult>> groupedMatches = new(StringComparer.OrdinalIgnoreCase);
        foreach (SearchHit hit in hits)
        {
            if (!groupedMatches.TryGetValue(hit.Path, out List<FindResult>? results))
            {
                results = [];
                groupedMatches[hit.Path] = results;
            }

            results.Add(new FindResult(hit.Preview, hit.Line - 1, hit.Column - 1, new Span(hit.Column - 1, hit.MatchLength)));
        }

        return groupedMatches;
    }

    private static string GetSearchHitKey(SearchHit hit)
    {
        return $"{hit.Path}|{hit.Line}|{hit.Column}|{hit.MatchLength}";
    }
}
