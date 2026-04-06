using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private static JArray BuildSmartContexts(
        IReadOnlyList<SearchHit> hits,
        int contextBefore,
        int contextAfter,
        int maxContexts)
    {
        int before = Math.Max(0, contextBefore);
        int after = Math.Max(0, contextAfter);
        int limit = Math.Max(1, maxContexts);
        List<(int Score, int FirstLine, JObject Context)> contexts = [];

        foreach (IGrouping<string, SearchHit> fileGroup in hits.GroupBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase))
        {
            string[] allLines = File.ReadAllLines(fileGroup.Key);
            List<(int StartLine, int EndLine, List<SearchHit> Hits)> windows =
                MergeHitsIntoWindows(fileGroup, allLines, before, after);

            foreach ((int StartLine, int EndLine, List<SearchHit> Hits) in windows)
            {
                int score = ScoreSmartContext(Hits);
                contexts.Add((score, Hits.Min(hit => hit.Line), new JObject
                {
                    ["path"] = fileGroup.Key,
                    ["project"] = Hits[0].ProjectUniqueName,
                    ["startLine"] = StartLine,
                    ["endLine"] = EndLine,
                    ["score"] = score,
                    ["hits"] = new JArray(Hits.Select(SerializeHit)),
                    ["text"] = RenderContextLines(allLines, StartLine, EndLine),
                }));
            }
        }

        return [.. contexts
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.FirstLine)
            .Take(limit)
            .Select(item => item.Context)];
    }

    private static List<(int StartLine, int EndLine, List<SearchHit> Hits)> MergeHitsIntoWindows(
        IEnumerable<SearchHit> fileHits,
        string[] allLines,
        int before,
        int after)
    {
        List<(int StartLine, int EndLine, List<SearchHit> Hits)> windows = [];
        foreach (SearchHit hit in fileHits.OrderBy(h => h.Line).ThenBy(h => h.Column))
        {
            int startLine = Math.Max(1, hit.Line - before);
            int endLine = Math.Min(allLines.Length, hit.Line + after);
            bool merged = false;

            for (int i = 0; i < windows.Count; i++)
            {
                (int StartLine, int EndLine, List<SearchHit> Hits) existing = windows[i];
                if (startLine <= existing.EndLine + 1)
                {
                    existing = (Math.Min(existing.StartLine, startLine), Math.Max(existing.EndLine, endLine), existing.Hits);
                    existing.Hits.Add(hit);
                    windows[i] = existing;
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                windows.Add((startLine, endLine, [hit]));
            }
        }

        return windows;
    }

    private static string RenderContextLines(string[] allLines, int startLine, int endLine)
    {
        StringBuilder builder = new();
        for (int lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            string lineText = allLines[lineNumber - 1];
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(lineNumber);
            builder.Append(": ");
            builder.Append(lineText);
        }

        return builder.ToString();
    }

    private static int ScoreSmartContext(IReadOnlyList<SearchHit> hits)
    {
        int score = 0;
        foreach (SearchHit hit in hits)
        {
            string preview = hit.Preview ?? string.Empty;
            score += Math.Max(20, hit.ScoreHint);

            foreach (string query in hit.SourceQueries.DefaultIfEmpty(string.Empty).Take(3))
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    continue;
                }

                string escaped = Regex.Escape(query);
                Regex declarationPattern = new($@"^\s*(class|struct|enum|namespace)\s+{escaped}\b", RegexOptions.IgnoreCase);
                Regex callablePattern = new($@"(\b{escaped}\s*\()|(::\s*{escaped}\b)", RegexOptions.IgnoreCase);
                Regex identifierPattern = new($@"\b{escaped}\b", RegexOptions.IgnoreCase);

                if (declarationPattern.IsMatch(preview))
                {
                    score += 120;
                }
                else if (callablePattern.IsMatch(preview))
                {
                    score += 80;
                }
                else if (identifierPattern.IsMatch(preview))
                {
                    score += 40;
                }
                else
                {
                    score += 10;
                }
            }
        }

        return score;
    }

    private static JObject SerializeHit(SearchHit hit)
    {
        return new JObject
        {
            ["path"] = hit.Path,
            ["project"] = hit.ProjectUniqueName,
            ["line"] = hit.Line,
            ["column"] = hit.Column,
            ["matchLength"] = hit.MatchLength,
            ["preview"] = hit.Preview,
            ["scoreHint"] = hit.ScoreHint,
            ["queries"] = new JArray(hit.SourceQueries),
        };
    }

    private static JObject SerializeCodeModelHit(CodeModelHit hit)
    {
        return new JObject
        {
            ["name"] = hit.Name,
            ["fullName"] = hit.FullName,
            ["kind"] = hit.Kind,
            ["signature"] = hit.Signature,
            ["path"] = hit.Path,
            ["project"] = hit.ProjectUniqueName,
            ["line"] = hit.Line,
            ["column"] = 1,
            ["endLine"] = hit.EndLine,
            ["matchKind"] = hit.MatchKind,
            ["scoreHint"] = hit.Score,
            ["preview"] = hit.Signature,
            ["source"] = "code-model",
        };
    }

    private static IReadOnlyList<SmartQueryTerm> ExtractSmartQueryTerms(string query)
    {
        List<SmartQueryTerm> terms = [];
        HashSet<string> seen = [];

        void AddTerm(string value, int weight, bool wholeWord)
        {
            string trimmed = value.Trim();
            if (trimmed.Length < 2 || !seen.Add(trimmed.ToLowerInvariant()))
            {
                return;
            }

            terms.Add(new SmartQueryTerm
            {
                Text = trimmed,
                Weight = weight,
                WholeWord = wholeWord,
            });
        }

        foreach (Match match in Regex.Matches(query, "\"([^\"]+)\""))
        {
            AddTerm(match.Groups[1].Value, 220, wholeWord: false);
        }

        foreach (Match match in Regex.Matches(query, @"[A-Za-z_][A-Za-z0-9_:/\\.\\-]*"))
        {
            string token = match.Value;
            bool looksLikeIdentifier = token.Contains("::", StringComparison.Ordinal) ||
                                       token.Contains("_", StringComparison.Ordinal) ||
                                       token.Contains(".", StringComparison.Ordinal) ||
                                       char.IsUpper(token[0]);

            if (looksLikeIdentifier)
            {
                AddTerm(token, 160, wholeWord: !token.Contains(".", StringComparison.Ordinal));
            }
        }

        HashSet<string> stopWords =
        [
            "where", "what", "when", "which", "that", "this", "with", "from", "into", "used", "using",
            "call", "calls", "show", "find", "open", "close", "line", "file", "files", "query", "context",
        ];

        foreach (Match match in Regex.Matches(query, "[A-Za-z][A-Za-z0-9_]{3,}"))
        {
            if (!stopWords.Contains(match.Value.ToLowerInvariant()))
            {
                AddTerm(match.Value, 80, wholeWord: true);
            }
        }

        if (terms.Count == 0)
        {
            AddTerm(query, 120, wholeWord: false);
        }

        return terms;
    }
}
