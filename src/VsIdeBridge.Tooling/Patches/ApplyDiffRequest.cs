using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace VsIdeBridge.Tooling.Patches;

public sealed class ApplyDiffValidationException(string message) : Exception(message);

public sealed class ApplyDiffRequest
{
    private const string BeginPatch = "*** Begin Patch";
    private const string EndPatch = "*** End Patch";
    private const string EndOfFile = "*** End of File";
    private const string AddFilePrefix = "*** Add File: ";
    private const string DeleteFilePrefix = "*** Delete File: ";
    private const string UpdateFilePrefix = "*** Update File: ";
    private const string MoveToPrefix = "*** Move to: ";

    private ApplyDiffRequest(string diff, IReadOnlyList<ApplyDiffFileOperation> operations, int mutationLineCount)
    {
        Diff = diff;
        Operations = operations;
        MutationLineCount = mutationLineCount;
    }

    public string Diff { get; }

    public IReadOnlyList<ApplyDiffFileOperation> Operations { get; }

    public int FileCount => Operations.Count;

    public int MutationLineCount { get; }

    public string EncodedDiff => Convert.ToBase64String(Encoding.UTF8.GetBytes(Diff));

    public static ApplyDiffRequest FromJsonObject(JsonObject? args)
    {
        if (args is null)
            throw new ApplyDiffValidationException("apply_diff requires arguments object.");

        if (args.ContainsKey("file") && args.ContainsKey("content"))
        {
            throw new ApplyDiffValidationException(
                "apply_diff does not accept 'file' + 'content' full-file replacement. Use write_file only when a full replacement is intentional, or use 'file' + 'old_content' + 'new_content' for a targeted replacement.");
        }

        if (args.ContainsKey("file") && args.ContainsKey("old_content") && args.ContainsKey("new_content"))
        {
            string path = args["file"]!.GetValue<string>();
            string oldContent = args["old_content"]!.GetValue<string>();
            string newContent = args["new_content"]!.GetValue<string>();
            return FromSimpleReplace(path, oldContent, newContent);
        }

        if (args.ContainsKey("diff"))
        {
            return FromPatchText(args["diff"]?.GetValue<string>());
        }

        throw new ApplyDiffValidationException(
            "apply_diff requires one of these input shapes:\n" +
            "  • \"diff\": \"*** Begin Patch ... *** End Patch\"   (advanced patch)\n" +
            "  • \"file\" + \"old_content\" + \"new_content\"     (targeted simple replace)");
    }

    public static ApplyDiffRequest FromPatchText(string? diff)
    {
        if (diff is null)
        {
            throw new ApplyDiffValidationException("apply_diff requires a non-empty 'diff' patch.");
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            throw new ApplyDiffValidationException("apply_diff requires a non-empty 'diff' patch.");
        }

        string normalized = diff.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        string[] lines = normalized.Split('\n');
        ValidateEnvelope(lines);

        List<ApplyDiffFileOperation> operations = [];
        int mutationLineCount = 0;
        int lineIndex = 1;
        while (lineIndex < lines.Length - 1)
        {
            string line = lines[lineIndex];
            if (line.Length == 0 || string.Equals(line, EndOfFile, StringComparison.Ordinal))
            {
                lineIndex++;
                continue;
            }

            if (line.StartsWith(AddFilePrefix, StringComparison.Ordinal))
            {
                ApplyDiffFileOperation operation = ParseAddFile(lines, ref lineIndex);
                operations.Add(operation);
                mutationLineCount += operation.MutationLineCount;
                continue;
            }

            if (line.StartsWith(DeleteFilePrefix, StringComparison.Ordinal))
            {
                operations.Add(ParseDeleteFile(lines, ref lineIndex));
                continue;
            }

            if (line.StartsWith(UpdateFilePrefix, StringComparison.Ordinal))
            {
                ApplyDiffFileOperation operation = ParseUpdateFile(lines, ref lineIndex);
                operations.Add(operation);
                mutationLineCount += operation.MutationLineCount;
                continue;
            }

            ThrowUnsupportedDirective(line);
        }

        if (operations.Count == 0)
        {
            throw new ApplyDiffValidationException("Patch has no file operations. Add at least one Add File, Delete File, or Update File directive.");
        }

        return new ApplyDiffRequest(normalized, operations, mutationLineCount);
    }

    public JsonObject ToJsonObject()
    {
        JsonArray files = [.. Operations.Select(operation => operation.ToJsonObject())];
        return new JsonObject
        {
            ["fileCount"] = FileCount,
            ["mutationLineCount"] = MutationLineCount,
            ["files"] = files,
        };
    }

    private static void ValidateEnvelope(string[] lines)
    {
        if (lines.Length < 2 || !string.Equals(lines[0], BeginPatch, StringComparison.Ordinal))
        {
            throw new ApplyDiffValidationException("Editor patch is missing the *** Begin Patch header.");
        }

        if (!string.Equals(LastLine(lines), EndPatch, StringComparison.Ordinal))
        {
            throw new ApplyDiffValidationException("Editor patch is missing the *** End Patch footer.");
        }
    }

    private static ApplyDiffFileOperation ParseAddFile(string[] lines, ref int lineIndex)
    {
        string path = ParsePath(lines[lineIndex], AddFilePrefix);
        lineIndex++;
        int addedLineCount = 0;
        while (lineIndex < lines.Length - 1 && !IsDirective(lines[lineIndex]))
        {
            string line = lines[lineIndex];
            if (line.Length == 0 || line[0] != '+')
            {
                throw new ApplyDiffValidationException($"Added file entries must use '+' lines only: {line}");
            }

            addedLineCount++;
            lineIndex++;
        }

        if (addedLineCount == 0)
        {
            throw new ApplyDiffValidationException($"Added file patch did not contain any content: {path}");
        }

        return new ApplyDiffFileOperation("add", path, null, addedLineCount, addedLineCount, 0);
    }

    private static ApplyDiffFileOperation ParseDeleteFile(string[] lines, ref int lineIndex)
    {
        string path = ParsePath(lines[lineIndex], DeleteFilePrefix);
        lineIndex++;
        return new ApplyDiffFileOperation("delete", path, null, 0, 0, 0);
    }

    private static ApplyDiffFileOperation ParseUpdateFile(string[] lines, ref int lineIndex)
    {
        string path = ParsePath(lines[lineIndex], UpdateFilePrefix);
        lineIndex++;
        string? moveTo = null;
        if (lineIndex < lines.Length - 1 && lines[lineIndex].StartsWith(MoveToPrefix, StringComparison.Ordinal))
        {
            moveTo = ParsePath(lines[lineIndex], MoveToPrefix);
            lineIndex++;
        }

        int hunkCount = 0;
        int mutationLineCount = 0;
        bool inHunk = false;
        while (lineIndex < lines.Length - 1 && !IsDirective(lines[lineIndex]))
        {
            string line = lines[lineIndex];
            if (line == "@@" || line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                hunkCount++;
                inHunk = true;
                lineIndex++;
                continue;
            }

            if (!inHunk)
            {
                throw new ApplyDiffValidationException($"Patch for {path} has content before an @@ hunk marker.");
            }

            if (line.Length == 0)
            {
                lineIndex++;
                continue;
            }

            char prefix = line[0];
            if (prefix != ' ' && prefix != '+' && prefix != '-')
            {
                throw new ApplyDiffValidationException($"Unsupported editor patch line prefix '{prefix}'.");
            }

            if (prefix is '+' or '-')
            {
                mutationLineCount++;
            }

            lineIndex++;
        }

        if (hunkCount == 0 && string.IsNullOrWhiteSpace(moveTo))
        {
            throw new ApplyDiffValidationException($"Patch for {path} has no @@ hunk blocks.");
        }

        if (mutationLineCount == 0 && string.IsNullOrWhiteSpace(moveTo))
        {
            throw new ApplyDiffValidationException($"Patch for {path} contains no additions or deletions.");
        }

        return new ApplyDiffFileOperation("update", path, moveTo, hunkCount, mutationLineCount, mutationLineCount);
    }

    private static string ParsePath(string line, string prefix)
    {
#if NET8_0_OR_GREATER
        string path = line[prefix.Length..].Trim();
#else
        string path = line.Substring(prefix.Length).Trim();
#endif
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ApplyDiffValidationException($"Patch directive is missing a path: {line}");
        }

        return path;
    }

    private static bool IsDirective(string line)
        => line.StartsWith("*** ", StringComparison.Ordinal);

    private static string LastLine(string[] lines)
    {
#if NET8_0_OR_GREATER
        return lines[^1];
#else
        int lastIndex = lines.Length - 1;
        return lines[lastIndex];
#endif
    }

    private static void ThrowUnsupportedDirective(string line)
    {
        if (line.StartsWith("--- ", StringComparison.Ordinal)
            || line.StartsWith("+++ ", StringComparison.Ordinal)
            || line.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            throw new ApplyDiffValidationException("apply_diff expects editor patch format, not unified diff headers like ---, +++, or diff --git.");
        }

        throw new ApplyDiffValidationException($"Unsupported editor patch directive: {line}.");
    }

    private static ApplyDiffRequest FromSimpleReplace(string path, string oldContent, string newContent)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ApplyDiffValidationException("apply_diff 'file' path cannot be empty.");
        }

        string[] oldLines = SplitPatchContentLines(oldContent);
        if (oldLines.Length == 0)
        {
            throw new ApplyDiffValidationException("apply_diff 'old_content' cannot be empty for a targeted replacement.");
        }

        string[] newLines = SplitPatchContentLines(newContent);
        int mutationLineCount = oldLines.Length + newLines.Length;
        ApplyDiffFileOperation operation = new("replace", path, null, 1, mutationLineCount, newLines.Length);

        StringBuilder diffBuilder = new();
        diffBuilder.Append("*** Begin Patch\n");
        diffBuilder.Append("*** Update File: ").Append(path).Append('\n');
        diffBuilder.Append("@@\n");
        AppendPatchLines(diffBuilder, '-', oldLines);
        AppendPatchLines(diffBuilder, '+', newLines);
        diffBuilder.Append("*** End Patch");
        string diffText = diffBuilder.ToString();
        return new ApplyDiffRequest(diffText, [operation], mutationLineCount);
    }

    private static string[] SplitPatchContentLines(string content)
    {
        string normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
#if NET8_0_OR_GREATER
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }
#else
        if (normalized.Length > 0 && normalized[normalized.Length - 1] == '\n')
        {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }
#endif

        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private static void AppendPatchLines(StringBuilder builder, char prefix, IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            builder.Append(prefix).Append(line).Append('\n');
        }
    }
}

public sealed class ApplyDiffFileOperation(
    string operation,
    string path,
    string? moveTo,
    int hunkCount,
    int mutationLineCount,
    int changedLineCount)
{
    public string Operation { get; } = operation;

    public string Path { get; } = path;

    public string? MoveTo { get; } = moveTo;

    public int HunkCount { get; } = hunkCount;

    public int MutationLineCount { get; } = mutationLineCount;

    public int ChangedLineCount { get; } = changedLineCount;

    public JsonObject ToJsonObject()
    {
        JsonObject obj = new()
        {
            ["operation"] = Operation,
            ["path"] = Path,
            ["hunkCount"] = HunkCount,
            ["mutationLineCount"] = MutationLineCount,
            ["changedLineCount"] = ChangedLineCount,
        };
        if (!string.IsNullOrWhiteSpace(MoveTo))
        {
            obj["moveTo"] = MoveTo;
        }

        return obj;
    }
}