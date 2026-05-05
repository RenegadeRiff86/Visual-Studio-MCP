using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static VsIdeBridge.Diagnostics.ErrorListPatterns;

namespace VsIdeBridge.Diagnostics;

internal static partial class BestPracticeAnalyzerHelpers
{
    private const int TwoCharacterDelimiterLength = 2;
    private const int EllipsisLength = 3;
    private const int MarkupCommentStartOffset = EllipsisLength;

    internal static int CountBracedBlockLines(string[] lines, int startIndex)
    {
        int depth = 0;
        bool foundOpen = false;
        for (int i = startIndex; i < lines.Length; i++)
        {
            (depth, foundOpen) = ScanLineForBraces(lines[i], depth, foundOpen);
            if (foundOpen && depth <= 0)
            {
                return i - startIndex + 1;
            }
        }

        return lines.Length - startIndex;
    }

    internal static bool HasNearbyOpeningBrace(string[] lines, int startLine, int lookAheadLines)
    {
        int maxLineExclusive = Math.Min(lines.Length, startLine + lookAheadLines);
        for (int i = startLine - 1; i < maxLineExclusive; i++)
        {
            if (lines[i].Contains('{'))
            {
                return true;
            }
        }

        return false;
    }

    internal static int CountPythonFunctionLines(string[] lines, int startIndex)
    {
        if (startIndex >= lines.Length)
        {
            return 0;
        }

        string defLine = lines[startIndex];
        int baseIndent = defLine.Length - defLine.TrimStart().Length;
        int count = 1;
        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                count++;
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            if (indent <= baseIndent)
            {
                break;
            }

            count++;
        }

        return count;
    }

    internal static bool IsGeneratedRegexDeclaration(string[] lines, int startLine)
    {
        int lineIndex = startLine - 1;
        if (lineIndex < 0 || lineIndex >= lines.Length)
        {
            return false;
        }

        string declarationLine = lines[lineIndex];
        if (declarationLine.IndexOf("partial Regex", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        for (int i = lineIndex - 1; i >= 0; i--)
        {
            string candidateLine = lines[i].Trim();
            if (string.IsNullOrEmpty(candidateLine))
            {
                continue;
            }

            return candidateLine.Contains("GeneratedRegex(");
        }

        return false;
    }

    internal static string ExtractBracedBlock(string[] lines, int startIndex)
    {
        int depth = 0;
        bool foundOpen = false;
        List<string> blockLines = [];
        for (int i = startIndex; i < lines.Length; i++)
        {
            blockLines.Add(lines[i]);
            (depth, foundOpen) = ScanLineForBraces(lines[i], depth, foundOpen);
            if (foundOpen && depth <= 0)
            {
                break;
            }
        }

        return string.Join("\n", blockLines);
    }

    internal static string[] GetRelativeDirectorySegments(string directoryPath)
    {
        string[] pathSegments = directoryPath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int srcIndex = Array.FindLastIndex(pathSegments, static segment =>
            string.Equals(segment, "src", StringComparison.OrdinalIgnoreCase));
        if (srcIndex < 0 || srcIndex >= pathSegments.Length - 1)
        {
            return [];
        }

        int relativeLength = pathSegments.Length - srcIndex - 1;
        string[] relativeSegments = new string[relativeLength];
        Array.Copy(pathSegments, srcIndex + 1, relativeSegments, 0, relativeLength);
        return relativeSegments;
    }

    internal static string[] GetDeclaredPartialTypeNames(string content)
    {
        List<string> partialTypeNames = [];
        foreach (Match match in PartialTypeDeclarationPattern().Matches(content))
        {
            string typeName = match.Groups["name"].Value.TrimStart('@');
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                partialTypeNames.Add(typeName);
            }
        }

        return [.. partialTypeNames.Distinct(StringComparer.Ordinal)];
    }

    internal static string[] TrimTypeGroupSegments(string[] directorySegments, string[] partialTypeNames)
    {
        if (partialTypeNames.Length == 0)
        {
            return directorySegments;
        }

        for (int index = directorySegments.Length - 1; index >= 0; index--)
        {
            if (partialTypeNames.Contains(directorySegments[index], StringComparer.Ordinal))
            {
                string[] trimmedSegments = new string[index];
                Array.Copy(directorySegments, 0, trimmedSegments, 0, index);
                return trimmedSegments;
            }
        }

        return directorySegments;
    }

    internal static bool NamespaceMatchesFolderStructure(string[] directorySegments, string[] namespaceSegments)
    {
        if (directorySegments.Length == 0)
        {
            return true;
        }

        string[] expandedDir = [..directorySegments.SelectMany(static seg => seg.Split('.'))];
        if (expandedDir.Length > namespaceSegments.Length)
        {
            return false;
        }

        int namespaceOffset = namespaceSegments.Length - expandedDir.Length;
        for (int index = 0; index < expandedDir.Length; index++)
        {
            if (!string.Equals(expandedDir[index], namespaceSegments[index + namespaceOffset], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsInsideStringLiteral(string content, int index)
    {
        int lineStart = content.LastIndexOf('\n', index > 0 ? index - 1 : 0) + 1;
        int quoteCount = 0;
        for (int i = lineStart; i < index; i++)
        {
            if (content[i] == '"' && (i == lineStart || content[i - 1] != '\\'))
            {
                quoteCount++;
            }
        }

        return quoteCount % TwoCharacterDelimiterLength == 1;
    }

    internal static bool IsInsideLineComment(string content, int index)
        => IsInsideComment(content, index, CodeLanguage.CSharp);

    internal static bool IsInsideComment(string content, int index, CodeLanguage language)
    {
        if (index <= 0 || string.IsNullOrEmpty(content))
        {
            return false;
        }

        int limit = Math.Min(index, content.Length);
        CommentSyntax syntax = GetCommentSyntax(language);
        CommentScanState state = new();

        for (int i = 0; i < limit; i++)
        {
            char ch = content[i];
            char next = i + 1 < content.Length ? content[i + 1] : '\0';

            if (ScanActiveComment(content, ref i, ch, next, ref state)
                || ScanActiveString(ref i, ch, ref state)
                || TryStartString(ch, syntax, ref state))
            {
                continue;
            }

            TryStartComment(content, ref i, ch, next, syntax, ref state);
        }

        return state.IsInsideComment;
    }

    private static CommentSyntax GetCommentSyntax(CodeLanguage language)
        => new(
            slashLine: language is CodeLanguage.CSharp or CodeLanguage.Cpp or CodeLanguage.FSharp or CodeLanguage.Unknown,
            slashBlock: language is CodeLanguage.CSharp or CodeLanguage.Cpp or CodeLanguage.Unknown,
            hashLine: language is CodeLanguage.Python or CodeLanguage.PowerShell or CodeLanguage.Unknown,
            vbLine: language == CodeLanguage.VisualBasic,
            fsharpBlock: language is CodeLanguage.FSharp or CodeLanguage.Unknown,
            markupBlock: language == CodeLanguage.Unknown,
            semicolonLine: language == CodeLanguage.Unknown,
            singleQuotedStrings: language is CodeLanguage.CSharp or CodeLanguage.Cpp or CodeLanguage.FSharp or CodeLanguage.Python or CodeLanguage.PowerShell or CodeLanguage.Unknown);

    private static bool ScanActiveComment(string content, ref int index, char ch, char next, ref CommentScanState state)
    {
        if (state.InLineComment)
        {
            if (ch == '\r' || ch == '\n')
            {
                state.InLineComment = false;
            }

            return true;
        }

        if (state.InSlashBlockComment)
        {
            if (ch == '*' && next == '/')
            {
                state.InSlashBlockComment = false;
                index++;
            }

            return true;
        }

        if (state.FSharpBlockDepth > 0)
        {
            ScanActiveFSharpBlock(ref index, ch, next, ref state);
            return true;
        }

        if (state.InMarkupComment)
        {
            if (IsMarkupCommentEnd(content, index, ch, next))
            {
                state.InMarkupComment = false;
                index += TwoCharacterDelimiterLength;
            }

            return true;
        }

        return false;
    }

    private static void ScanActiveFSharpBlock(ref int index, char ch, char next, ref CommentScanState state)
    {
        if (ch == '(' && next == '*')
        {
            state.FSharpBlockDepth++;
            index++;
            return;
        }

        if (ch == '*' && next == ')')
        {
            state.FSharpBlockDepth--;
            index++;
        }
    }

    private static bool ScanActiveString(ref int index, char ch, ref CommentScanState state)
    {
        if (state.InDoubleString)
        {
            if (ch == '\\')
            {
                index++;
                return true;
            }

            if (ch == '"')
            {
                state.InDoubleString = false;
            }

            return true;
        }

        if (!state.InSingleString)
        {
            return false;
        }

        if (ch == '\\')
        {
            index++;
            return true;
        }

        if (ch == '\'')
        {
            state.InSingleString = false;
        }

        return true;
    }

    private static bool TryStartString(char ch, CommentSyntax syntax, ref CommentScanState state)
    {
        if (ch == '"')
        {
            state.InDoubleString = true;
            return true;
        }

        if (syntax.SingleQuotedStrings && ch == '\'')
        {
            state.InSingleString = true;
            return true;
        }

        return false;
    }

    private static void TryStartComment(string content, ref int index, char ch, char next, CommentSyntax syntax, ref CommentScanState state)
    {
        if (syntax.SlashLine && ch == '/' && next == '/')
        {
            state.InLineComment = true;
            index++;
            return;
        }

        if (syntax.SlashBlock && ch == '/' && next == '*')
        {
            state.InSlashBlockComment = true;
            index++;
            return;
        }

        if (syntax.HashLine && ch == '#')
        {
            state.InLineComment = true;
            return;
        }

        if (syntax.VbLine && ch == '\'')
        {
            state.InLineComment = true;
            return;
        }

        if (syntax.FSharpBlock && ch == '(' && next == '*')
        {
            state.FSharpBlockDepth = 1;
            index++;
            return;
        }

        if (syntax.MarkupBlock && IsMarkupCommentStart(content, index, ch, next))
        {
            state.InMarkupComment = true;
            index += MarkupCommentStartOffset;
            return;
        }

        if (syntax.SemicolonLine && ch == ';')
        {
            state.InLineComment = true;
        }
    }

    private static bool IsMarkupCommentStart(string content, int index, char ch, char next)
        => ch == '<'
            && next == '!'
            && index + MarkupCommentStartOffset < content.Length
            && content[index + TwoCharacterDelimiterLength] == '-'
            && content[index + MarkupCommentStartOffset] == '-';

    private static bool IsMarkupCommentEnd(string content, int index, char ch, char next)
        => ch == '-'
            && next == '-'
            && index + TwoCharacterDelimiterLength < content.Length
            && content[index + TwoCharacterDelimiterLength] == '>';

    private readonly struct CommentSyntax(bool slashLine, bool slashBlock, bool hashLine, bool vbLine, bool fsharpBlock, bool markupBlock, bool semicolonLine, bool singleQuotedStrings)
    {
        public bool SlashLine { get; } = slashLine;
        public bool SlashBlock { get; } = slashBlock;
        public bool HashLine { get; } = hashLine;
        public bool VbLine { get; } = vbLine;
        public bool FSharpBlock { get; } = fsharpBlock;
        public bool MarkupBlock { get; } = markupBlock;
        public bool SemicolonLine { get; } = semicolonLine;
        public bool SingleQuotedStrings { get; } = singleQuotedStrings;
    }

    private struct CommentScanState
    {
        public bool InDoubleString { get; set; }
        public bool InSingleString { get; set; }
        public bool InLineComment { get; set; }
        public bool InSlashBlockComment { get; set; }
        public bool InMarkupComment { get; set; }
        public int FSharpBlockDepth { get; set; }
        public readonly bool IsInsideComment => InLineComment || InSlashBlockComment || FSharpBlockDepth > 0 || InMarkupComment;
    }

    internal static bool IsTrulyUsefulComment(string comment)
    {
        string lower = comment.ToLowerInvariant();
#if NET5_0_OR_GREATER
        if (comment.TrimStart().StartsWith('<'))
#else
        if (comment.TrimStart().StartsWith("<", StringComparison.Ordinal))
#endif
        {
            return true;
        }

#if NET5_0_OR_GREATER
        if (TrulyUsefulCommentRegex().IsMatch(comment.Trim()))
#else
        if (Regex.IsMatch(comment.Trim(), @"^(First|Second|Third|Fourth|Pass \d+|Step \d+|Phase \d+)\b", RegexOptions.IgnoreCase))
#endif
        {
            return true;
        }

        return comment.StartsWith("TODO", StringComparison.OrdinalIgnoreCase)
            || comment.StartsWith("FIXME", StringComparison.OrdinalIgnoreCase)
            || comment.StartsWith("HACK", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("why ")
            || lower.Contains("because")
            || lower.Contains("reason:")
            || lower.Contains("note:")
            || lower.Contains("intentional")
            || lower.Contains("non-obvious")
            || lower.Contains("handles")
            || lower.Contains("avoids")
            || comment.Length <= 15;
    }

    internal static bool IsUnnecessaryComment(string comment)
    {
        string lower = comment.ToLowerInvariant().TrimEnd('.', '!', '?', ':');
        string[] redundantPhrases =
        [
            "this method", "this function", "this class", "this variable",
            "this line", "increments", "decrements", "sets the", "gets the",
            "initializes the", "checks if",
            "loops through", "iterates over", "adds one to", "subtracts one",
            "as an ai", "large language model",
            "this ensures that", "this approach ensures", "it is recommended",
            "best practice", "following best practices", "in summary",
            "please note that", "note that this", "important to note",
            "the above code", "the following code", "this code does the following",
            "the purpose of this", "simply", "basically",
        ];

        return redundantPhrases.Any(phrase => lower.Contains(phrase));
    }

    internal static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    internal static string GetLineAt(string content, int index)
    {
        int start = index > 0 ? content.LastIndexOf('\n', index - 1) + 1 : 0;
        int end = content.IndexOf('\n', index);
#if NET5_0_OR_GREATER
        return end < 0 ? content[start..] : content[start..end];
#else
        return end < 0 ? content.Substring(start) : content.Substring(start, end - start);
#endif
    }

    internal static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

#if NET5_0_OR_GREATER
        return string.Concat(text.AsSpan(0, maxLength - EllipsisLength), "...");
#else
        return text.Substring(0, maxLength - EllipsisLength) + "...";
#endif
    }

    internal static int CountStructuralBraceDelta(string line)
    {
        (int depth, _) = ScanLineForBraces(line, 0, foundOpen: false);
        return depth;
    }

    internal static bool TryGetCommentOnlyCatchBlockInfo(
        string[] lines,
        string content,
        int matchIndex,
        out int startLine,
        out string message)
    {
        startLine = GetLineNumber(content, matchIndex);
        string block = ExtractBracedBlock(lines, startLine - 1);
        int openBraceIndex = block.IndexOf('{');
        int closeBraceIndex = block.LastIndexOf('}');
        if (openBraceIndex < 0 || closeBraceIndex <= openBraceIndex)
        {
            message = string.Empty;
            return false;
        }

        string body = block.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);
        bool hadComment = body.Contains("//", StringComparison.Ordinal) || body.Contains("/*", StringComparison.Ordinal);
        string stripped = CommentTextRegex().Replace(body, string.Empty);
        if (!string.IsNullOrWhiteSpace(stripped))
        {
            message = string.Empty;
            return false;
        }

        message = hadComment
            ? "Catch block only contains comments and still swallows the exception. Write the exception to the log with useful context, or rethrow it."
            : "Empty catch block swallows exceptions silently. Log the exception with context or rethrow it.";
        return true;
    }

#if NET8_0_OR_GREATER
    [GeneratedRegex(@"//.*?$|/\*.*?\*/", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CommentTextRegex();
#else
    private static readonly Regex CommentTextRegexInstance = new(@"//.*?$|/\*.*?\*/", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private static Regex CommentTextRegex() => CommentTextRegexInstance;
#endif

    private static (int Depth, bool FoundOpen) ScanLineForBraces(string line, int depth, bool foundOpen)
    {
        int i = 0;
        while (i < line.Length)
        {
            char ch = line[i];

            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;

            // Double-quoted string literal — skip until closing unescaped '"'.
            if (ch == '"')
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\') { i += 2; continue; }
                    if (line[i] == '"')  { i++; break; }
                    i++;
                }
                continue;
            }

            // Single-quoted char literal — skip the literal character and closing '\''.
            if (ch == '\'')
            {
                i++;
                if (i < line.Length && line[i] == '\\') i++; // skip escape char
                if (i < line.Length) i++;                     // skip the literal character
                if (i < line.Length && line[i] == '\'') i++; // skip closing quote
                continue;
            }

            // Normal code — count braces.
            if (ch == '{') { depth++; foundOpen = true; }
            else if (ch == '}') { depth--; }

            i++;
        }

        return (depth, foundOpen);
    }

#if NET5_0_OR_GREATER
    [System.Text.RegularExpressions.GeneratedRegex(@"^(First|Second|Third|Fourth|Pass \d+|Step \d+|Phase \d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrulyUsefulCommentRegex();
#endif
}
