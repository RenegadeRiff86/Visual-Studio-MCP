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

        // 1. Simple full file write (easiest for models - recommended)
        if (args.ContainsKey("file") && args.ContainsKey("content"))
        {
            string path = args["file"]!.GetValue<string>();
            string content = args["content"]!.GetValue<string>();
            return FromSimpleWrite(path, content);
        }

        // 2. Simple string replace (file + old_content + new_content)
        if (args.ContainsKey("file") && args.ContainsKey("old_content") && args.ContainsKey("new_content"))
        {
            string path = args["file"]!.GetValue<string>();
            string oldContent = args["old_content"]!.GetValue<string>();
            string newContent = args["new_content"]!.GetValue<string>();
            return FromSimpleReplace(path, oldContent, newContent);
        }

        // 3. Original advanced patch format (still fully supported)
        if (args.ContainsKey("diff"))
        {
            return FromPatchText(args["diff"]?.GetValue<string>());
        }

        throw new ApplyDiffValidationException(
            "apply_diff requires one of these input shapes:\n" +
            "  • \"diff\": \"*** Begin Patch ... *** End Patch\"   (advanced patch)\n" +
            "  • \"file\" + \"content\"                           (full file write - recommended)\n" +
            "  • \"file\" + \"old_content\" + \"new_content\"     (simple replace)\n\n" +
            "The 'file' + 'content' style works on any text file (not just code).");
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

    // === NEW MODEL-FRIENDLY HELPERS ===

    private static ApplyDiffRequest FromSimpleWrite(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ApplyDiffValidationException("apply_diff 'file' path cannot be empty.");

        if (content is null)
            throw new ApplyDiffValidationException("apply_diff 'content' cannot be null.");

        ApplyDiffFileOperation operation = new("write", path, null, 0, content.Length, content.Length);
        return new ApplyDiffRequest($"*** Begin Patch\n*** Write File: {path}\n{content}\n*** End Patch", [operation], content.Length);
    }

    private static ApplyDiffRequest FromSimpleReplace(string path, string oldContent, string newContent)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ApplyDiffValidationException("apply_diff 'file' path cannot be empty.");

        ApplyDiffFileOperation operation = new("replace", path, null, 0, newContent.Length, newContent.Length);
        string diffText = $"*** Begin Patch\n*** Replace in File: {path}\n- {oldContent}\n+ {newContent}\n*** End Patch";
        return new ApplyDiffRequest(diffText, [operation], newContent.Length);
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