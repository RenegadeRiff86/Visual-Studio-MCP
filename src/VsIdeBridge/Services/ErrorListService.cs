using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Diagnostics;
using static VsIdeBridge.Diagnostics.ErrorListConstants;

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService(VsIdeBridgePackage package, ReadinessService readinessService, BridgeUiSettingsService uiSettings)
{
    private readonly ErrorListProvider _bestPracticeProvider = new(package)
    {
        ProviderName = "VS IDE Bridge Best Practices",
    };
    private readonly VsIdeBridgePackage _package = package;
    private BestPracticeTableDataSource? _bestPracticeTableSource;
    private bool _bestPracticeTableSourceRegistered;
    private readonly object _bestPracticeCacheGate = new();
    private readonly Dictionary<string, BestPracticeFileCacheEntry> _bestPracticeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReadinessService _readinessService = readinessService;
    private readonly BridgeUiSettingsService _uiSettings = uiSettings;

    public async Task<JObject> GetErrorListAsync(
       IdeCommandContext context,
       bool waitForIntellisense,
       int timeoutMilliseconds,
       bool quickSnapshot = false,
       ErrorListQuery? query = null,
       bool includeBuildOutputFallback = false,
       bool afterEdit = false,
       bool forceRefresh = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        bool refreshBestPracticeDiagnostics = ShouldRefreshBestPracticeDiagnostics(query);
        if (!quickSnapshot && refreshBestPracticeDiagnostics)
        {
            PublishBestPracticeRows(context.Dte, []);
        }

        if (waitForIntellisense)
        {
            await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds, afterEdit).ConfigureAwait(true);
        }

        IReadOnlyList<JObject> rows;
        if (quickSnapshot)
        {
            EnsureErrorListWindow(context.Dte);
            if (!TryReadTableRows(out rows) || rows.Count == 0)
            {
                rows = await ReadDteRowsAsync(context, rows).ConfigureAwait(true);
            }
        }
        else
        {
            rows = await WaitForRowsAsync(context, timeoutMilliseconds, forceRefresh).ConfigureAwait(true);
        }

        if (includeBuildOutputFallback)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
            IReadOnlyList<JObject> buildOutputRows = ReadBuildOutputRows(context.Dte);
            if (rows.Count == 0)
            {
                rows = buildOutputRows;
            }
        }

        if (!includeBuildOutputFallback)
        {
            rows = ExcludeBuildOutputRows(rows);
        }

        if (!quickSnapshot && refreshBestPracticeDiagnostics)
        {
            IReadOnlyList<JObject> bestPracticeRows = await RefreshBestPracticeDiagnosticsAsync(context, rows).ConfigureAwait(true);
            if (bestPracticeRows.Count > 0)
            {
                rows = MergeRows(rows, bestPracticeRows);
            }
        }

        // Filter first (no Max applied inside ApplyQuery).
        JObject[] matchingRows = [.. ApplyQuery(rows, query)];

        // Sort the filtered set.
        IEnumerable<JObject> sortedRows = ApplySort(matchingRows, query?.SortBy, query?.SortDirection);

        // Chunk-based pagination: chunk_size + chunk_index take priority; Max is a legacy fallback.
        int chunkSize  = query?.ChunkSize ?? query?.Max ?? 0;
        int chunkIndex = Math.Max(0, query?.ChunkIndex ?? 0);

        JObject[] filteredRows;
        int       chunkCount;
        bool      hasMoreChunks;

        if (chunkSize > 0)
        {
            int skip      = chunkSize * chunkIndex;
            filteredRows  = [.. sortedRows.Skip(skip).Take(chunkSize)];
            chunkCount    = (int)Math.Ceiling((double)matchingRows.Length / chunkSize);
            hasMoreChunks = (chunkIndex + 1) < chunkCount;
        }
        else
        {
            filteredRows  = [.. sortedRows];
            chunkCount    = 1;
            hasMoreChunks = false;
        }

        Dictionary<string, int> severityCounts = CreateSeverityCounts();
        foreach (JObject row in filteredRows)
        {
            severityCounts[(string)row[SeverityKey]!]++;
        }

        Dictionary<string, int> totalSeverityCounts = CreateSeverityCounts();
        foreach (JObject row in rows)
        {
            totalSeverityCounts[(string)row[SeverityKey]!]++;
        }

        return new JObject
        {
            ["count"]              = filteredRows.Length,
            ["totalCount"]         = matchingRows.Length,
            ["severityCounts"]     = JObject.FromObject(severityCounts),
            ["totalSeverityCounts"] = JObject.FromObject(totalSeverityCounts),
            ["hasErrors"]          = severityCounts["Error"] > 0,
            ["hasWarnings"]        = severityCounts["Warning"] > 0,
            ["filter"]             = query?.ToJson() ?? [],
            ["chunkIndex"]         = chunkIndex,
            ["chunkSize"]          = chunkSize > 0 ? chunkSize : matchingRows.Length,
            ["chunkCount"]         = chunkCount,
            ["hasMoreChunks"]      = hasMoreChunks,
            ["rows"]               = new JArray(filteredRows),
            ["groups"]             = BuildGroups(matchingRows, query?.GroupBy),
        };
    }

    private static bool ShouldRefreshBestPracticeDiagnostics(ErrorListQuery? query)
    {
        string severity = NormalizeSeverity(query?.Severity);
        return string.IsNullOrWhiteSpace(severity)
            || string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<IReadOnlyList<JObject>> RefreshBestPracticeDiagnosticsAsync(IdeCommandContext context, IReadOnlyList<JObject>? rows = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        if (!_uiSettings.BestPracticeDiagnosticsEnabled)
        {
            PublishBestPracticeRows(context.Dte, []);
            return [];
        }

        IReadOnlyList<string> bestPracticeCandidateFiles = GetBestPracticeCandidateFiles(context.Dte, rows ?? []);
        IReadOnlyDictionary<string, string> bestPracticeProjectLookup = CreateBestPracticeProjectLookup(context.Dte, bestPracticeCandidateFiles);
        PruneBestPracticeCache(bestPracticeCandidateFiles);
        IReadOnlyList<JObject> bestPracticeRows = await Task.Run(
            () => AnalyzeBestPracticeFindings(bestPracticeCandidateFiles, bestPracticeProjectLookup, cancellationToken: context.CancellationToken),
            context.CancellationToken).ConfigureAwait(false);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        PublishBestPracticeRows(context.Dte, bestPracticeRows);
        return bestPracticeRows;
    }

    private static IReadOnlyDictionary<string, string> CreateBestPracticeProjectLookup(DTE2 dte, IReadOnlyList<string> files)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Dictionary<string, string> projectNamesByFile = [];
        foreach (string file in files)
        {
            string? projectUniqueName = SolutionFileLocator.TryFindProjectUniqueName(dte, file);
            if (!string.IsNullOrWhiteSpace(projectUniqueName))
            {
                projectNamesByFile[file.ToLowerInvariant()] = projectUniqueName!;
            }
        }

        return projectNamesByFile;
    }

    private static string TryGetBestPracticeProjectUniqueName(IReadOnlyDictionary<string, string>? projectNamesByFile, string file)
    {
        if (projectNamesByFile is null)
        {
            return string.Empty;
        }

        return projectNamesByFile.TryGetValue(file.ToLowerInvariant(), out string? projectUniqueName)
            ? projectUniqueName ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> GetBestPracticeCandidateFiles(DTE2 dte, IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        HashSet<string> seen = [];
        List<string> files = [];

        // First add files from error rows (if any).
        foreach (var path in rows
            .Select(row => row["file"]?.ToString())
            .OfType<string>()
            .Where(IsBestPracticeCandidateFile))
        {
            if (seen.Add(path.ToLowerInvariant()))
            {
                files.Add(path);
            }
        }

        // Then enumerate all solution project files.
        foreach (var (path, _) in SolutionFileLocator.EnumerateSolutionFiles(dte))
        {
            if (files.Count >= MaxBestPracticeFiles)
            {
                break;
            }

            if (!IsBestPracticeCandidateFile(path))
            {
                continue;
            }

            if (seen.Add(path.ToLowerInvariant()))
            {
                files.Add(path);
            }
        }

        foreach (string path in EnumerateRepoBestPracticeFiles(dte))
        {
            if (files.Count >= MaxBestPracticeFiles)
            {
                break;
            }

            if (seen.Add(path.ToLowerInvariant()))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static IEnumerable<string> EnumerateRepoBestPracticeFiles(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string solutionFullName = dte.Solution?.FullName ?? string.Empty;
        string? solutionRoot = string.IsNullOrWhiteSpace(solutionFullName)
            ? null
            : Path.GetDirectoryName(solutionFullName);

        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            yield break;
        }

        string scriptsDirectory = Path.Combine(solutionRoot, "scripts");
        if (!Directory.Exists(scriptsDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(scriptsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsBestPracticeCandidateFile(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsBestPracticeCandidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        foreach (var fragment in IgnoredBestPracticePathFragments)
        {
            if (fullPath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        string fileName = Path.GetFileName(fullPath);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(fullPath);
        return !string.IsNullOrWhiteSpace(extension) && BestPracticeCodeExtensions.Contains(extension);
    }

    private IReadOnlyList<JObject> AnalyzeBestPracticeFindings(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? projectNamesByFile = null,
        string? contentOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!BestPracticeStateManager.IsEnabled)
        {
            return [];
        }

        BestPracticeFileFindings[] fileFindings = new BestPracticeFileFindings[files.Count];
        Parallel.For(
            0,
            files.Count,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetBestPracticeAnalysisDegreeOfParallelism(),
            },
            index =>
            {
                string file = files[index];
                IReadOnlyList<JObject> findings = contentOverride is null
                    ? AnalyzeBestPracticeFileWithCache(file)
                    : AnalyzeBestPracticeFile(file, contentOverride);
                fileFindings[index] = new BestPracticeFileFindings(file, findings);
            });

        List<JObject> findings = [];
        foreach (BestPracticeFileFindings result in fileFindings)
        {
            string projectUniqueName = TryGetBestPracticeProjectUniqueName(projectNamesByFile, result.File);
            foreach (JObject finding in result.Findings)
            {
                JObject row = (JObject)finding.DeepClone();
                if (!string.IsNullOrWhiteSpace(projectUniqueName))
                {
                    row[ProjectKey] = projectUniqueName;
                }

                findings.Add(row);
            }
        }

        return [.. findings
            .GroupBy(CreateFindingIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private IReadOnlyList<JObject> AnalyzeBestPracticeFileWithCache(string file)
    {
        if (!TryGetFileStamp(file, out DateTime lastWriteTimeUtc, out long length))
        {
            return [];
        }

        lock (_bestPracticeCacheGate)
        {
            if (_bestPracticeCache.TryGetValue(file, out BestPracticeFileCacheEntry? cached) &&
                cached.LastWriteTimeUtc == lastWriteTimeUtc &&
                cached.Length == length)
            {
                return CloneFindings(cached.Findings);
            }
        }

        IReadOnlyList<JObject> findings = AnalyzeBestPracticeFile(file, SafeReadFile(file));
        lock (_bestPracticeCacheGate)
        {
            _bestPracticeCache[file] = new BestPracticeFileCacheEntry(lastWriteTimeUtc, length, CloneFindings(findings));
        }

        return findings;
    }

    private static IReadOnlyList<JObject> AnalyzeBestPracticeFile(string file, string content)
    {
        if (!BestPracticeStateManager.IsEnabled || string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        List<JObject> findings = [];
        int perFileFindings = 0;
        foreach (JObject finding in BestPracticeAnalyzer.AnalyzeFile(file, content))
        {
            findings.Add(finding);
            perFileFindings++;
            if (perFileFindings >= MaxBestPracticeFindingsPerFile)
            {
                break;
            }
        }

        return findings;
    }

    private void PruneBestPracticeCache(IReadOnlyList<string> files)
    {
        HashSet<string> activeFiles = new(files, StringComparer.OrdinalIgnoreCase);
        lock (_bestPracticeCacheGate)
        {
            foreach (string cachedFile in _bestPracticeCache.Keys.Where(file => !activeFiles.Contains(file)).ToArray())
            {
                _bestPracticeCache.Remove(cachedFile);
            }
        }
    }

    private static bool TryGetFileStamp(string file, out DateTime lastWriteTimeUtc, out long length)
    {
        try
        {
            FileInfo fileInfo = new(file);
            if (!fileInfo.Exists)
            {
                lastWriteTimeUtc = default;
                length = 0;
                return false;
            }

            lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            length = fileInfo.Length;
            return true;
        }
        catch
        {
            lastWriteTimeUtc = default;
            length = 0;
            return false;
        }
    }

    private static int GetBestPracticeAnalysisDegreeOfParallelism()
    {
        if (Environment.ProcessorCount <= 2)
        {
            return 1;
        }

        return Math.Min(Environment.ProcessorCount - 1, 8);
    }

    private static IReadOnlyList<JObject> CloneFindings(IReadOnlyList<JObject> findings)
        => [.. findings.Select(finding => (JObject)finding.DeepClone())];

    /// <summary>
    /// Pre-write analysis: scans content that is about to be written and returns best-practice
    /// warnings without publishing them to the Error List. Callers (PatchService, write-file)
    /// can include these in their response so the LLM sees issues immediately.
    /// </summary>
    internal static IReadOnlyList<JObject> AnalyzeContentBeforeWrite(string filePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || !IsBestPracticeCandidateFile(filePath))
        {
            return [];
        }

        return AnalyzeBestPracticeFile(filePath, content);
    }

    private sealed class BestPracticeFileCacheEntry(DateTime lastWriteTimeUtc, long length, IReadOnlyList<JObject> findings)
    {
        public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

        public long Length { get; } = length;

        public IReadOnlyList<JObject> Findings { get; } = findings;
    }

    private readonly struct BestPracticeFileFindings(string file, IReadOnlyList<JObject> findings)
    {
        public string File { get; } = file;

        public IReadOnlyList<JObject> Findings { get; } = findings;
    }

    private static string SafeReadFile(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath);
        }
        catch
        {
            return string.Empty;
        }
    }

}
