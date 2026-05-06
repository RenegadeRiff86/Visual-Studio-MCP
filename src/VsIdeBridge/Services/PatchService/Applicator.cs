using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Diagnostics;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

internal sealed partial class PatchService
{
    private static ApplyFilePatchResult ApplyFilePatch(string path, string existingText, FilePatch patch)
    {
        if (patch.SearchBlocks.Count > 0)
        {
            return ApplySearchBlockPatch(path, existingText, patch);
        }

        string newline = DetectNewline(existingText);
        IReadOnlyList<string> existingLines = SplitLines(existingText, out bool hadFinalNewline);
        List<string> resultLines = [];
        int sourceIndex = 0;
        int firstChangedLine = 1;
        bool firstChangeCaptured = false;
        List<ChangedRange> changedRanges = [];
        List<int> deletedLineMarkers = [];
        int matchedLineCount = 0;
        int mutationLineCount = 0;

        foreach (Hunk hunk in GetOrderedHunks(patch.Hunks))
        {
            int targetIndex = Math.Max(0, hunk.OriginalStart - 1);
            ValidateNoHunkOverlap(path, targetIndex, sourceIndex);
            HunkLine? firstCheckLine = hunk.Lines.FirstOrDefault(l => l.Kind == ' ' || l.Kind == '-');
            targetIndex = AdjustTargetIndexFuzzy(existingLines, targetIndex, sourceIndex, firstCheckLine);
            CopyLinesToTarget(existingLines, resultLines, ref sourceIndex, targetIndex);

            int? hunkStartLine = null;
            int hunkAddedLineCount = 0;

            foreach (HunkLine line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case ' ':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "context");
                        matchedLineCount++;
                        resultLines.Add(existingLines[sourceIndex]);
                        sourceIndex++;
                        break;
                    case '-':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "deletion");
                        matchedLineCount++;
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        hunkStartLine ??= Math.Max(1, resultLines.Count + 1);
                        deletedLineMarkers.Add(Math.Max(1, resultLines.Count + 1));

                        sourceIndex++;
                        break;
                    case '+':
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        hunkStartLine ??= Math.Max(1, resultLines.Count + 1);
                        hunkAddedLineCount++;
                        resultLines.Add(line.Text);
                        break;
                    default:
                        throw new CommandErrorException(InvalidArgumentsCode,
                            $"Unsupported line prefix '{line.Kind}' in patch for {path}. Each line must start with ' ' (context), '-' (deletion), or '+' (addition).");
                }
            }

            AppendChangedRange(changedRanges, hunkStartLine, hunkAddedLineCount);
        }

        CopyLinesToTarget(existingLines, resultLines, ref sourceIndex, existingLines.Count);
        bool deleteFile = patch.NewPath == DevNullPath;
        string content = JoinLines(resultLines, newline, !deleteFile && (hadFinalNewline || patch.OldPath == DevNullPath));
        return new ApplyFilePatchResult
        {
            Content = content,
            FirstChangedLine = firstChangedLine,
            DeleteFile = deleteFile,
            ChangedRanges = changedRanges,
            DeletedLineMarkers = deletedLineMarkers,
            MatchedLineCount = matchedLineCount,
            MutationLineCount = mutationLineCount,
        };
    }

    private static ApplyFilePatchResult ApplySearchBlockPatch(string path, string existingText, FilePatch patch)
    {
        string newline = DetectNewline(existingText);
        IReadOnlyList<string> existingLines = SplitLines(existingText, out bool hadFinalNewline);
        List<string> resultLines = [];
        int sourceIndex = 0;
        int firstChangedLine = 1;
        bool firstChangeCaptured = false;
        List<ChangedRange> changedRanges = [];
        List<int> deletedLineMarkers = [];
        int matchedLineCount = 0;
        int mutationLineCount = 0;

        foreach (SearchBlock block in patch.SearchBlocks)
        {
            int targetIndex = FindSearchBlockStart(path, existingLines, sourceIndex, block);
            CopyLinesToTarget(existingLines, resultLines, ref sourceIndex, targetIndex);

            int? blockStartLine = null;
            int blockAddedLineCount = 0;
            bool isPureContext = block.Lines.Count > 0 && block.Lines.All(line => line.Kind == ' ');

            if (isPureContext)
            {
                for (int lineIndex = 0; lineIndex < block.Lines.Count; lineIndex++)
                    EnsureLineMatches(path, existingLines, targetIndex + lineIndex, block.Lines[lineIndex].Text, "context");

                matchedLineCount += block.Lines.Count;
                continue;
            }

            foreach (HunkLine line in block.Lines)
            {
                switch (line.Kind)
                {
                    case ' ':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "context");
                        matchedLineCount++;
                        resultLines.Add(existingLines[sourceIndex]);
                        sourceIndex++;
                        break;
                    case '-':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "deletion");
                        matchedLineCount++;
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        blockStartLine ??= Math.Max(1, resultLines.Count + 1);
                        deletedLineMarkers.Add(Math.Max(1, resultLines.Count + 1));
                        sourceIndex++;
                        break;
                    case '+':
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        blockStartLine ??= Math.Max(1, resultLines.Count + 1);
                        blockAddedLineCount++;
                        resultLines.Add(line.Text);
                        break;
                    default:
                        throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported editor patch line prefix '{line.Kind}' in patch for {path}.");
                }
            }

            AppendChangedRange(changedRanges, blockStartLine, blockAddedLineCount);
        }

        CopyLinesToTarget(existingLines, resultLines, ref sourceIndex, existingLines.Count);
        bool deleteFile = patch.NewPath == DevNullPath;
        string content = JoinLines(resultLines, newline, !deleteFile && (hadFinalNewline || patch.OldPath == DevNullPath));
        return new ApplyFilePatchResult
        {
            Content = content,
            FirstChangedLine = firstChangedLine,
            DeleteFile = deleteFile,
            ChangedRanges = changedRanges,
            DeletedLineMarkers = deletedLineMarkers,
            MatchedLineCount = matchedLineCount,
            MutationLineCount = mutationLineCount,
        };
    }

    private static IEnumerable<Hunk> GetOrderedHunks(IEnumerable<Hunk> hunks)
    {
        return [.. hunks
            .OrderBy(hunk => Math.Max(0, hunk.OriginalStart))
            .ThenBy(hunk => Math.Max(0, hunk.NewStart))
            .ThenBy(hunk => hunk.Lines.Count)];
    }

    private static void ValidateNoHunkOverlap(string path, int targetIndex, int sourceIndex)
    {
        if (targetIndex < sourceIndex)
        {
            throw new CommandErrorException(InvalidArgumentsCode,
                $"Patch hunks overlap at line {targetIndex + 1} for {path} (previous hunk consumed through line {sourceIndex}). " +
                "Fix: combine adjacent hunks (within ~3 lines) into a single hunk, or use editor patch format " +
                "(*** Begin Patch / *** Update File) which uses content matching instead of line numbers.");
        }
    }

    // Fuzzy position search: if the hunk's nominal start line doesn't match the first
    // context/deletion line, scan +-FuzzLines to find the real position.
    private static int AdjustTargetIndexFuzzy(
        IReadOnlyList<string> existingLines, int targetIndex, int sourceIndex, HunkLine? firstCheckLine)
    {
        if (firstCheckLine is null
            || targetIndex >= existingLines.Count
            || LinesMatchFuzzy(existingLines[targetIndex], firstCheckLine.Text))
            return targetIndex;

        const int FuzzLines = 10;
        for (int fuzz = 1; fuzz <= FuzzLines; fuzz++)
        {
            foreach (int candidate in new[] { targetIndex + fuzz, targetIndex - fuzz })
            {
                if (candidate >= sourceIndex && candidate < existingLines.Count
                    && LinesMatchFuzzy(existingLines[candidate], firstCheckLine.Text))
                    return candidate;
            }
        }
        return targetIndex;
    }

    private static void CopyLinesToTarget(
        IReadOnlyList<string> existingLines, List<string> resultLines, ref int sourceIndex, int targetIndex)
    {
        while (sourceIndex < targetIndex && sourceIndex < existingLines.Count)
        {
            resultLines.Add(existingLines[sourceIndex]);
            sourceIndex++;
        }
    }

    private static void AppendChangedRange(List<ChangedRange> changedRanges, int? startLine, int addedLineCount)
    {
        if (!startLine.HasValue)
            return;
        int endLine = addedLineCount > 0 ? startLine.Value + addedLineCount - 1 : startLine.Value;
        changedRanges.Add(new ChangedRange { StartLine = startLine.Value, EndLine = endLine });
    }

    private static int FindSearchBlockStart(string path, IReadOnlyList<string> existingLines, int sourceIndex, SearchBlock block)
    {
        string[] matchLines = [.. block.Lines
            .Where(line => line.Kind != '+')
            .Select(line => line.Text)];

        if (matchLines.Length == 0)
        {
            // No context or deletion lines: can't locate the insertion point.
            // Default to end-of-file so pure-addition blocks append rather than
            // silently inserting at an arbitrary mid-file position.
            return existingLines.Count;
        }

        int maxStart = existingLines.Count - matchLines.Length;

        int exactMatchCandidate = FindSequentialMatch(existingLines, sourceIndex, maxStart, matchLines, useFuzzyMatch: false);
        if (exactMatchCandidate >= 0)
        {
            return exactMatchCandidate;
        }

        // Second pass: fuzzy match to handle LLM escape artifacts.
        int fuzzyMatchCandidate = FindSequentialMatch(existingLines, sourceIndex, maxStart, matchLines, useFuzzyMatch: true);
        if (fuzzyMatchCandidate >= 0)
        {
            return fuzzyMatchCandidate;
        }

        // Pass 3: anchor-line matching. When exact and fuzzy sequential passes fail, pick the most
        // unique line in the context block as an anchor, then score every candidate position by how
        // many of its context lines match fuzzily. Accept the highest-scoring position provided it
        // covers at least 60 % of lines — or accept unconditionally when all context lines are
        // trivial (blank or single-character braces/brackets) and no better signal is available.
        (int bestCandidate, int bestScore) = FindBestAnchorMatch(existingLines, sourceIndex, maxStart, matchLines);
        if (bestCandidate >= 0 && (bestScore * 5 >= matchLines.Length * 3 || AllTrivialMatchLines(matchLines)))
        {
            return bestCandidate;
        }

        // Re-run Pass 3 to find the best partial match for a useful error message.
        (int errorBestCandidate, int errorBestScore) = FindBestAnchorMatch(existingLines, sourceIndex, maxStart, matchLines);

        string descriptor = string.IsNullOrWhiteSpace(block.Header)
            ? "editor patch block"
            : $"editor patch block '{block.Header}'";
        string firstMatchLine = matchLines.Length > 0 ? Truncate(matchLines[0], 60) : "(empty)";
        string bestMatchHint = errorBestCandidate >= 0
            ? $" The closest match was at line {errorBestCandidate + 1} but only {errorBestScore} of {matchLines.Length} context lines agreed."
            : " No candidate position matched any context line.";
        throw new CommandErrorException(
            InvalidArgumentsCode,
            $"apply_diff failed: the @@ context block was not found in {path}. " +
            $"The patch was looking for a block starting with \"{firstMatchLine}\".{bestMatchHint} " +
            "To fix this: (1) Call read_file on this file to see the current content. " +
            "(2) Copy the exact lines you want to change from the read_file output — do not retype or paraphrase them. " +
            "(3) Build a new @@ block using those exact lines as context, with - for deletions and + for additions. " +
            "(4) Resubmit apply_diff with the corrected patch." +
            BuildBackwardSearchHint(existingLines, sourceIndex, maxStart, matchLines),
            new
            {
                block = block.Header,
                sourceIndex = sourceIndex + 1,
                bestMatchLine = errorBestCandidate >= 0 ? errorBestCandidate + 1 : (int?)null,
                bestMatchScore = errorBestCandidate >= 0 ? errorBestScore : (int?)null,
                matchLines,
            });
    }

    private static int FindSequentialMatch(
        IReadOnlyList<string> existingLines,
        int sourceIndex,
        int maxStart,
        IReadOnlyList<string> matchLines,
        bool useFuzzyMatch)
    {
        for (int candidate = Math.Max(0, sourceIndex); candidate <= maxStart; candidate++)
        {
            bool matches = true;
            for (int offset = 0; offset < matchLines.Count; offset++)
            {
                bool linesMatch = useFuzzyMatch
                    ? LinesMatchFuzzy(existingLines[candidate + offset], matchLines[offset])
                    : string.Equals(existingLines[candidate + offset], matchLines[offset], StringComparison.Ordinal);
                if (!linesMatch)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return candidate;
            }
        }

        return -1;
    }

    private static string BuildBackwardSearchHint(
        IReadOnlyList<string> existingLines,
        int sourceIndex,
        int maxStart,
        IReadOnlyList<string> matchLines)
    {
        if (sourceIndex <= 0)
        {
            return string.Empty;
        }

        int backwardMaxStart = Math.Min(maxStart, sourceIndex - 1);
        if (backwardMaxStart < 0)
        {
            return string.Empty;
        }

        int consumedMatchLine = FindSequentialMatch(existingLines, 0, backwardMaxStart, matchLines, useFuzzyMatch: false);
        if (consumedMatchLine < 0)
        {
            consumedMatchLine = FindSequentialMatch(existingLines, 0, backwardMaxStart, matchLines, useFuzzyMatch: true);
        }

        if (consumedMatchLine < 0 || consumedMatchLine >= sourceIndex)
        {
            return string.Empty;
        }

        return $" Those same lines already appeared at line {consumedMatchLine + 1} and were consumed by an earlier @@ block in this patch. " +
               "Merge the related @@ blocks into one, or write the second block with context lines that come after the first block's changes.";
    }

    private static (int BestCandidate, int BestScore) FindBestAnchorMatch(
        IReadOnlyList<string> existingLines,
        int sourceIndex,
        int maxStart,
        string[] matchLines)
    {
        int anchorIdx = FindAnchorLineIndex(matchLines);
        int bestScore = 0;
        int bestCandidate = -1;
        for (int candidate = Math.Max(0, sourceIndex); candidate <= maxStart; candidate++)
        {
            if (!LinesMatchFuzzy(existingLines[candidate + anchorIdx], matchLines[anchorIdx]))
            {
                continue;
            }

            int score = CountMatchingLines(existingLines, candidate, matchLines);
            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        return (bestCandidate, bestScore);
    }

    /// <summary>
    /// Compares two lines with tolerance for JSON/C# escape artifacts that LLMs
    /// commonly introduce.  Tries exact match first, then falls back to a
    /// normalized comparison that strips one level of backslash-escaping and
    /// trims trailing whitespace.
    /// </summary>
    private static bool LinesMatchFuzzy(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }

        // Pass 2: normalize backslash escapes and trim trailing whitespace.
        string normalActual = NormalizeLine(actual);
        string normalExpected = NormalizeLine(expected);
        if (string.Equals(normalActual, normalExpected, StringComparison.Ordinal))
        {
            return true;
        }

        // Pass 3: blank lines match other blank/whitespace-only lines regardless of exact content.
        // NormalizeLine trims trailing whitespace, so empty after normalize = blank/whitespace line.
        if (normalActual.Trim().Length == 0 && normalExpected.Trim().Length == 0)
            return true;

        // Pass 4: ignore leading whitespace differences (tab/space confusion or
        // off-by-one indent counts — common when LLMs generate patch context).
        // Guard: only match when trimmed content is non-empty so we never
        // conflate non-blank lines against blank ones.
        return normalActual.TrimStart().Length > 0
            && string.Equals(
                normalActual.TrimStart(),
                normalExpected.TrimStart(),
                StringComparison.Ordinal);
    }

    /// <summary>Returns the index of the most unique (longest trimmed) line among matchLines, used as the anchor for Pass 3 matching.</summary>
    private static int FindAnchorLineIndex(string[] matchLines)
    {
        int bestLen = -1;
        int bestIdx = 0;
        for (int i = 0; i < matchLines.Length; i++)
        {
            int len = matchLines[i].TrimStart().Length;
            if (len > bestLen)
            {
                bestLen = len;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private static int CountMatchingLines(IReadOnlyList<string> existingLines, int candidate, string[] matchLines)
    {
        int count = 0;
        for (int offset = 0; offset < matchLines.Length; offset++)
        {
            if (LinesMatchFuzzy(existingLines[candidate + offset], matchLines[offset]))
                count++;
        }
        return count;
    }

    /// <summary>Returns true when every context line is trivial — blank or a single brace/bracket character.</summary>
    private static bool AllTrivialMatchLines(string[] matchLines)
    {
        foreach (string line in matchLines)
        {
            if (line.Trim().Length > 1)
                return false;
        }
        return true;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";

    private static string NormalizeLine(string line)
    {
        // Fast path: no backslashes at all -- just trim trailing whitespace.
        if (line.IndexOf('\\') < 0)
        {
            return line.TrimEnd();
        }

        // Strip one level of backslash-escaping (\\\" -> \", \\\\\\\\ -> \\\\, \\\\n -> \\n, etc.)
        System.Text.StringBuilder sb = new(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                char next = line[i + 1];
                if (next == 't')
                {
                    sb.Append('\t');
                    i++;
                    continue;
                }

                if (next == '"' || next == '\\' || next == 'n' || next == 'r')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
            }

            sb.Append(line[i]);
        }

        return sb.ToString().TrimEnd();
    }

    private static void EnsureLineMatches(string path, IReadOnlyList<string> existingLines, int index, string expected, string operation)
    {
        if (index >= existingLines.Count)
        {
            throw new CommandErrorException(InvalidArgumentsCode,
                $"apply_diff failed: the patch references line {index + 1} but {path} only has {existingLines.Count} lines. " +
                "Your hunk line numbers are off — likely because a prior hunk added or removed lines and the numbers were not adjusted. " +
                "Call read_file on this file, copy the exact lines you want to change, and resubmit the patch. " +
                "Alternatively, switch to editor patch format (*** Begin Patch / *** Update File / @@ / -old / +new / *** End Patch), which finds lines by content and never needs line numbers.");
        }

        if (!LinesMatchFuzzy(existingLines[index], expected))
        {
            const int ContextRadius = 3;
            int start = Math.Max(0, index - ContextRadius);
            int end = Math.Min(existingLines.Count - 1, index + ContextRadius);
            string context = string.Join("\n", Enumerable.Range(start, end - start + 1)
                .Select(i => $"  {i + 1,4}: {(i == index ? ">>>" : "   ")} {existingLines[i]}"));

            throw new CommandErrorException(
                InvalidArgumentsCode,
                $"apply_diff failed: the {operation} line in your patch does not match the file. " +
                $"Your patch says \"{Truncate(expected, 80)}\" — the file at line {index + 1} has \"{Truncate(existingLines[index], 80)}\". " +
                "This usually means line numbers shifted because a prior hunk in this patch added or removed lines. " +
                "Call read_file around line " + (index + 1) + " to see what is actually there, copy the exact text, and resubmit. " +
                "Alternatively, switch to editor patch format (*** Begin Patch / *** Update File / @@ / -old / +new / *** End Patch), which matches by content and never needs line numbers.",
                new { expected, actual = existingLines[index], line = index + 1, fileContext = context });
        }
    }
}

