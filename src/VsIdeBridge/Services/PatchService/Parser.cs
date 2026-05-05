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
    private static (List<FilePatch> FilePatches, string PatchFormat) ParseSupportedPatchFormat(string patchText)
    {
        if (LooksLikeEditorPatchEnvelope(patchText))
        {
            return (ParseEditorPatch(patchText), "editor-patch");
        }

        return (ParseUnifiedDiff(patchText), "unified-diff");
    }

    private static string BuildMissingPatchFormatMessage(string patchText)
    {
        if (LooksLikeEditorPatchEnvelope(patchText))
        {
            return "Patch has *** Begin Patch but no file entries. " +
                "Required structure: *** Begin Patch\\n*** Update File: <path>\\n@@\\n <context>\\n-old\\n+new\\n*** End Patch. " +
                "Each file needs *** Add File: <path>, *** Delete File: <path>, or *** Update File: <path>.";
        }

        return "Patch format not recognized. " +
            "Required: *** Begin Patch\\n*** Update File: <path>\\n@@\\n <context>\\n-old\\n+new\\n*** End Patch. " +
            "Use editor patch directives only; do not send unified diff headers like --- or +++.";
    }

    private static bool LooksLikeEditorPatchEnvelope(string patchText)
    {
        if (string.IsNullOrWhiteSpace(patchText))
        {
            return false;
        }

        string normalized = patchText.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
        return normalized.StartsWith("*** Begin Patch", StringComparison.Ordinal);
    }

    private static List<FilePatch> ParseEditorPatch(string patchText)
    {
        string normalized = patchText.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        string[] lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Editor patch is missing the *** Begin Patch header.");
        }

        List<FilePatch> patches = [];
        int lineIndex = 1;
        while (lineIndex < lines.Length)
        {
            string line = lines[lineIndex];
            if (string.Equals(line, "*** End of File", StringComparison.Ordinal))
            {
                // Accept Codex-style EOF sentinels as no-op markers so apply_patch envelopes
                // can be replayed without rewriting them into a second patch dialect.
                lineIndex++;
                continue;
            }

            if (string.Equals(line, "*** End Patch", StringComparison.Ordinal))
            {
                return patches;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                patches.Add(ParseEditorAddFile(lines, ref lineIndex));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                patches.Add(ParseEditorDeleteFile(lines, ref lineIndex));
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                patches.Add(ParseEditorUpdateFile(lines, ref lineIndex));
                continue;
            }

            throw new CommandErrorException(
                InvalidArgumentsCode,
                $"Unsupported editor patch directive: {line}. This tool expects editor patch format like '*** Update File: <path>' and does not accept unified diff headers such as '---' or '+++'.");
        }

        throw new CommandErrorException(InvalidArgumentsCode, "Editor patch is missing the *** End Patch footer.");
    }

    private static FilePatch ParseEditorAddFile(string[] lines, ref int lineIndex)
    {
        string path = ParseEditorPatchPath(lines[lineIndex], "*** Add File: ");
        lineIndex++;
        List<HunkLine> addedLines = [];
        while (lineIndex < lines.Length && !IsEditorPatchDirective(lines[lineIndex]))
        {
            string line = lines[lineIndex];
            if (line.Length == 0 || line[0] != '+')
            {
                throw new CommandErrorException(InvalidArgumentsCode, $"Added file entries must use '+' lines only: {line}");
            }

            addedLines.Add(new HunkLine { Kind = '+', Text = line.Length > 1 ? line.Substring(1) : string.Empty });
            lineIndex++;
        }

        if (addedLines.Count == 0)
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Added file patch did not contain any content: {path}");
        }

        return new FilePatch
        {
            OldPath = DevNullPath,
            NewPath = path,
            Hunks =
            [
                new Hunk
                {
                    OriginalStart = 1,
                    OriginalCount = 0,
                    NewStart = 1,
                    NewCount = addedLines.Count,
                    Lines = addedLines,
                },
            ],
            Format = "editor-patch",
        };
    }

    private static FilePatch ParseEditorDeleteFile(string[] lines, ref int lineIndex)
    {
        string path = ParseEditorPatchPath(lines[lineIndex], "*** Delete File: ");
        lineIndex++;
        return new FilePatch
        {
            OldPath = path,
            NewPath = DevNullPath,
            Format = "editor-patch",
        };
    }

    private static FilePatch ParseEditorUpdateFile(string[] lines, ref int lineIndex)
    {
        string oldPath = ParseEditorPatchPath(lines[lineIndex], "*** Update File: ");
        lineIndex++;
        string newPath = oldPath;
        if (lineIndex < lines.Length && lines[lineIndex].StartsWith("*** Move to: ", StringComparison.Ordinal))
        {
            newPath = ParseEditorPatchPath(lines[lineIndex], "*** Move to: ");
            lineIndex++;
        }

        List<SearchBlock> blocks = [];
        List<Hunk> hunks = [];
        SearchBlock? currentBlock = null;
        while (lineIndex < lines.Length && !IsEditorPatchDirective(lines[lineIndex]))
        {
            string line = lines[lineIndex];
            if (line == "@@" || line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentBlock?.Lines.Count > 0)
                {
                    blocks.Add(currentBlock);
                    currentBlock = null;
                }

                if (LooksLikeUnifiedHunkHeader(line))
                {
                    hunks.Add(ParseEditorUnifiedHunk(lines, ref lineIndex));
                    continue;
                }

                currentBlock = new SearchBlock { Header = line.Length > EditorPatchHeaderPrefixLength ? line.Substring(EditorPatchHeaderPrefixLength).Trim() : string.Empty };
                lineIndex++;
                continue;
            }

            if (line.Length == 0)
            {
                // An empty patch line is treated as a context line matching an empty file line.
                // This is the natural reading -- LLMs routinely emit blank separator lines in
                // patch context without any prefix character, and requiring " " (space + nothing)
                // is an invisible, error-prone requirement. Silently accept it.
                currentBlock ??= new SearchBlock();
                currentBlock.Lines.Add(new HunkLine { Kind = ' ', Text = string.Empty });
                lineIndex++;
                continue;
            }

            char prefix = line[0];
            if (prefix != ' ' && prefix != '+' && prefix != '-')
            {
                throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported editor patch line prefix '{prefix}'.");
            }

            currentBlock ??= new SearchBlock();
            currentBlock.Lines.Add(new HunkLine { Kind = prefix, Text = line.Length > 1 ? line.Substring(1) : string.Empty });
            lineIndex++;
        }

        if (currentBlock?.Lines.Count > 0)
        {
            blocks.Add(currentBlock);
        }

        if (blocks.Count > 0 && hunks.Count > 0)
        {
            throw new CommandErrorException(InvalidArgumentsCode,
                $"Patch for {oldPath} mixes editor search blocks and unified hunk headers. Use either plain @@ editor blocks or @@ -old,+new @@ hunk blocks in one file patch.");
        }

        return new FilePatch
        {
            OldPath = oldPath,
            NewPath = newPath,
            Hunks = hunks,
            SearchBlocks = blocks,
            Format = "editor-patch",
        };
    }

    private static bool IsEditorPatchDirective(string line)
    {
        return line.StartsWith("*** ", StringComparison.Ordinal);
    }

    private static Hunk ParseEditorUnifiedHunk(string[] lines, ref int lineIndex)
    {
        Hunk hunk = ParseHunkHeader(lines[lineIndex]);
        lineIndex++;
        while (lineIndex < lines.Length && !IsEditorPatchDirective(lines[lineIndex]))
        {
            string hunkLine = lines[lineIndex];
            if (IsHunkBoundaryLine(hunkLine))
            {
                break;
            }

            AddHunkLine(hunk, hunkLine);
            lineIndex++;
        }

        return hunk;
    }

    private static string ParseEditorPatchPath(string line, string prefix)
    {
        string path = NormalizePatchPath(line.Substring(prefix.Length));
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Editor patch directive is missing a path: {line}");
        }

        return path;
    }

    private static List<FilePatch> ParseUnifiedDiff(string patchText)
    {
        string normalized = patchText.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        string[] lines = normalized.Split('\n');
        List<FilePatch> patches = [];
        FilePatch? currentFile = null;
        int lineIndex = 0;

        while (lineIndex < lines.Length)
        {
            string line = lines[lineIndex];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                string oldPath = NormalizePatchPath(line.Substring(4));
                lineIndex++;
                if (lineIndex >= lines.Length || !lines[lineIndex].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    throw new CommandErrorException(InvalidArgumentsCode, "Unified diff is missing a +++ header.");
                }

                string newPath = NormalizePatchPath(lines[lineIndex].Substring(4));
                currentFile = new FilePatch
                {
                    OldPath = oldPath,
                    NewPath = newPath,
                    Hunks = [],
                };
                patches.Add(currentFile);
                lineIndex++;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentFile is null)
                {
                    throw new CommandErrorException(InvalidArgumentsCode, "Encountered a hunk before a file header.");
                }

                Hunk hunk = ParseHunkHeader(line);
                lineIndex++;
                while (lineIndex < lines.Length)
                {
                    string hunkLine = lines[lineIndex];
                    if (IsHunkBoundaryLine(hunkLine))
                    {
                        break;
                    }

                    AddHunkLine(hunk, hunkLine);
                    lineIndex++;
                }

                currentFile.Hunks.Add(hunk);
                continue;
            }

            lineIndex++;
        }

        return patches;
    }

    private static bool IsHunkBoundaryLine(string line)
    {
        foreach (var prefix in HunkBoundaryPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeUnifiedHunkHeader(string line)
    {
        return Regex.IsMatch(line, @"^@@ -\d+(,\d+)? \+\d+(,\d+)? @@");
    }

    private static void AddHunkLine(Hunk hunk, string hunkLine)
    {
        if (hunkLine == "\\ No newline at end of file")
        {
            return;
        }

        if (hunkLine.Length == 0)
        {
            // Blank line in hunk body = context line for an empty file line.
            // Treating it as a skip without advancing sourceIndex would shift
            // all subsequent matches off by one.
            hunk.Lines.Add(new HunkLine { Kind = ' ', Text = string.Empty });
            return;
        }

        char prefix = hunkLine[0];
        if (prefix != ' ' && prefix != '+' && prefix != '-')
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported hunk line prefix '{prefix}'.");
        }

        hunk.Lines.Add(new HunkLine
        {
            Kind = prefix,
            Text = hunkLine.Length > 1 ? hunkLine.Substring(1) : string.Empty,
        });
    }

    private static Hunk ParseHunkHeader(string line)
    {
        Match match = Regex.Match(line, @"^@@ -(?<oldStart>\d+)(,(?<oldCount>\d+))? \+(?<newStart>\d+)(,(?<newCount>\d+))? @@");
        if (!match.Success)
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Invalid unified diff hunk header: {line}");
        }

        return new Hunk
        {
            OriginalStart = int.Parse(match.Groups["oldStart"].Value),
            OriginalCount = ParseHunkCount(match.Groups["oldCount"].Value),
            NewStart = int.Parse(match.Groups["newStart"].Value),
            NewCount = ParseHunkCount(match.Groups["newCount"].Value),
            Lines = [],
        };
    }

    private static int ParseHunkCount(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? 1 : int.Parse(value);
    }

    private static string NormalizePatchPath(string value)
    {
        string trimmed = value.Trim();
        if (trimmed == DevNullPath)
        {
            return trimmed;
        }

        if ((trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal)) && trimmed.Length > EditorPatchHeaderPrefixLength)
        {
            return trimmed.Substring(EditorPatchHeaderPrefixLength);
        }

        return trimmed;
    }

    private static string DetectNewline(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static List<string> SplitLines(string text, out bool hadFinalNewline)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        hadFinalNewline = normalized.EndsWith("\n", StringComparison.Ordinal);
        if (hadFinalNewline)
        {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }

        return normalized.Length == 0 ? [] : [.. normalized.Split('\n')];
    }

    private static string JoinLines(IReadOnlyList<string> lines, string newline, bool includeTrailingNewline)
    {
        string content = string.Join(newline, lines);
        if (includeTrailingNewline)
        {
            content += newline;
        }

        return content;
    }
}

