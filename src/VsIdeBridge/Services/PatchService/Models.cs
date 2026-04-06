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
    private const string InvalidArgumentsCode = "invalid_arguments";
    private const string DevNullPath = "/dev/null";
    private const int EditorPatchHeaderPrefixLength = 2;
    private static readonly string[] HunkBoundaryPrefixes =
    [
        "@@ ",
        "--- ",
        "diff --git ",
        "index ",
        "Index: ",
        "new file mode ",
        "deleted file mode ",
        "old mode ",
        "new mode ",
        "similarity index ",
        "rename from ",
        "rename to ",
        "Binary files ",
        "GIT binary patch",
    ];

    private sealed class ChangedRange
    {
        public int StartLine { get; set; }

        public int EndLine { get; set; }
    }

    private sealed class FilePatch
    {
        public string OldPath { get; set; } = string.Empty;

        public string NewPath { get; set; } = string.Empty;

        public List<Hunk> Hunks { get; set; } = [];

        public List<SearchBlock> SearchBlocks { get; set; } = [];

        public string Format { get; set; } = "unified-diff";
    }

    private sealed class SearchBlock
    {
        public string Header { get; set; } = string.Empty;

        public List<HunkLine> Lines { get; set; } = [];
    }

    private sealed class Hunk
    {
        public int OriginalStart { get; set; }

        public int OriginalCount { get; set; }

        public int NewStart { get; set; }

        public int NewCount { get; set; }

        public List<HunkLine> Lines { get; set; } = [];
    }

    private sealed class HunkLine
    {
        public char Kind { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    private sealed class ApplyFilePatchResult
    {
        public string Content { get; set; } = string.Empty;

        public int FirstChangedLine { get; set; }

        public bool DeleteFile { get; set; }

        public List<ChangedRange> ChangedRanges { get; set; } = [];

        public List<int> DeletedLineMarkers { get; set; } = [];

        public int MatchedLineCount { get; set; }

        public int MutationLineCount { get; set; }
    }

    private sealed class PatchPaths
    {
        public string SourcePath { get; set; } = string.Empty;

        public string TargetPath { get; set; } = string.Empty;

        public bool IsNewFile { get; set; }

        public bool IsMove =>
            !string.IsNullOrWhiteSpace(SourcePath) &&
            !PathNormalization.AreEquivalent(SourcePath, TargetPath);
    }

    private sealed class PreparedPatchOperation
    {
        public FilePatch FilePatch { get; set; } = new FilePatch();

        public PatchPaths Paths { get; set; } = new PatchPaths();

        public ApplyFilePatchResult Result { get; set; } = new ApplyFilePatchResult();

        public string RequestedTargetContent { get; set; } = string.Empty;

        public bool AlreadySatisfied { get; set; }
    }
}

