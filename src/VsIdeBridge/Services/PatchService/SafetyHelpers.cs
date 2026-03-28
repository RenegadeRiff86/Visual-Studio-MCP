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
    private static JArray CreateChangedRangesArray(IEnumerable<ChangedRange> ranges)
    {
        return [.. ranges.Select(range => new JObject
        {
            ["startLine"] = range.StartLine,
            ["endLine"] = range.EndLine,
        })];
    }

    private static void EnsurePatchHasMeaningfulOperations(FilePatch patch, PatchPaths paths)
    {
        int hunkCount = patch.Hunks.Count + patch.SearchBlocks.Count;
        if (hunkCount == 0)
        {
            if (paths.IsMove || paths.IsNewFile || patch.NewPath == DevNullPath)
            {
                return;
            }

            throw new CommandErrorException(
                InvalidArgumentsCode,
                $"Patch for {paths.TargetPath} has no hunks or search blocks -- it parsed as empty. " +
                "Check your patch format: unified diff needs @@ hunk headers, editor patch needs @@ separators between context/change lines.");
        }

        if (CountPatchMutationLines(patch) > 0 || paths.IsMove)
        {
            return;
        }

        throw new CommandErrorException(
            InvalidArgumentsCode,
            $"Patch for {paths.TargetPath} contains only context lines (' ' prefix) with no additions ('+') or deletions ('-'). " +
            "Every patch must change at least one line.");
    }

    private static int CountPatchMutationLines(FilePatch patch)
    {
        return patch.Hunks.SelectMany(hunk => hunk.Lines)
            .Concat(patch.SearchBlocks.SelectMany(block => block.Lines))
            .Count(line => line.Kind == '+' || line.Kind == '-');
    }

    private static bool IsCurrentContentAlreadyPatched(string path, string currentContent, FilePatch patch)
    {
        try
        {
            ApplyFilePatchResult reverseResult = ApplyFilePatch(path, currentContent, CreateReversePatch(patch));
            return reverseResult.MutationLineCount > 0 && reverseResult.MatchedLineCount > 0;
        }
        catch (CommandErrorException)
        {
            return false;
        }
    }

    private static FilePatch CreateReversePatch(FilePatch patch)
    {
        return new FilePatch
        {
            OldPath = patch.NewPath,
            NewPath = patch.OldPath,
            Hunks = [.. patch.Hunks.Select(CreateReverseHunk)],
            SearchBlocks = [.. patch.SearchBlocks.Select(CreateReverseSearchBlock)],
            Format = patch.Format,
        };
    }

    private static Hunk CreateReverseHunk(Hunk hunk)
    {
        return new Hunk
        {
            OriginalStart = hunk.NewStart,
            OriginalCount = hunk.NewCount,
            NewStart = hunk.OriginalStart,
            NewCount = hunk.OriginalCount,
            Lines = [.. hunk.Lines.Select(CreateReverseHunkLine)],
        };
    }

    private static SearchBlock CreateReverseSearchBlock(SearchBlock block)
    {
        return new SearchBlock
        {
            Header = block.Header,
            Lines = [.. block.Lines.Select(CreateReverseHunkLine)],
        };
    }

    private static HunkLine CreateReverseHunkLine(HunkLine line)
    {
        return new HunkLine
        {
            Kind = line.Kind switch
            {
                '+' => '-',
                '-' => '+',
                _ => line.Kind,
            },
            Text = line.Text,
        };
    }

    private static bool IsPatchContentMismatch(CommandErrorException ex)
        => string.Equals(ex.Code, InvalidArgumentsCode, StringComparison.Ordinal)
            && ex.Message.Contains("mismatch", StringComparison.OrdinalIgnoreCase);

    private static ApplyFilePatchResult CreateAlreadySatisfiedResult(string content, ApplyFilePatchResult reverseResult)
        => new()
        {
            Content = content,
            FirstChangedLine = reverseResult.FirstChangedLine,
            DeleteFile = false,
            ChangedRanges = reverseResult.ChangedRanges,
            DeletedLineMarkers = [],
            MatchedLineCount = reverseResult.MatchedLineCount,
            MutationLineCount = reverseResult.MutationLineCount,
        };

    private static void EnsureSafeToModifyOpenDocument(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string normalizedPath = PathNormalization.NormalizeFilePath(path);
        Document? openDocument = TryFindOpenDocumentByPath(dte, normalizedPath);

        if (openDocument is null)
        {
            return;
        }

        if (!openDocument.Saved)
        {
            throw new CommandErrorException("unsupported_operation",
                $"Cannot patch {normalizedPath} -- it has unsaved changes in the VS editor. Fix: call save_document first, then retry the patch.");
        }
    }

    private static void CloseOpenDocumentIfPresent(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string normalizedPath = PathNormalization.NormalizeFilePath(path);
        Document? openDocument = TryFindOpenDocumentByPath(dte, normalizedPath);

        if (openDocument is null)
        {
            return;
        }

        if (!openDocument.Saved)
        {
            throw new CommandErrorException("unsupported_operation",
                $"Cannot close {normalizedPath} -- it has unsaved changes. Fix: call save_document first, then retry.");
        }

        openDocument.Close(vsSaveChanges.vsSaveChangesNo);
    }

    private static Document? TryFindOpenDocumentByPath(DTE2 dte, string normalizedPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (Document document in dte.Documents)
        {
            string fullName = document.FullName;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            if (PathNormalization.AreEquivalent(fullName, normalizedPath))
            {
                return document;
            }
        }

        return null;
    }
}

