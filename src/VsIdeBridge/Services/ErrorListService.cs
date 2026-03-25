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
    private readonly ReadinessService _readinessService = readinessService;
    private readonly BridgeUiSettingsService _uiSettings = uiSettings;

    public async Task<JObject> GetErrorListAsync(
        IdeCommandContext context,
        bool waitForIntellisense,
        int timeoutMilliseconds,
        bool quickSnapshot = false,
        ErrorListQuery? query = null,
        bool includeBuildOutputFallback = false,
        bool afterEdit = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (!quickSnapshot)
        {
            PublishBestPracticeRows(context.Dte, []);
        }

        if (waitForIntellisense && !quickSnapshot)
        {
            await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds, afterEdit).ConfigureAwait(true);
        }

        IReadOnlyList<JObject> rows;
        if (quickSnapshot)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
            EnsureErrorListWindow(context.Dte);
            try
            {
                rows = ReadRows(context.Dte);
            }
            catch (InvalidOperationException)
            {
                rows = [];
            }

            if (includeBuildOutputFallback && rows.Count == 0)
            {
                rows = ReadBuildOutputRows(context.Dte);
            }
        }
        else
        {
            rows = await WaitForRowsAsync(context, timeoutMilliseconds, intellisenseReady: waitForIntellisense).ConfigureAwait(true);
            if (includeBuildOutputFallback && rows.Count == 0)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
                rows = ReadBuildOutputRows(context.Dte);
            }
        }

        if (!includeBuildOutputFallback)
        {
            rows = ExcludeBuildOutputRows(rows);
        }

        if (!quickSnapshot)
        {
            var bestPracticeRows = await RefreshBestPracticeDiagnosticsAsync(context, rows).ConfigureAwait(true);
            if (bestPracticeRows.Count > 0)
            {
                rows = MergeRows(rows, bestPracticeRows);
            }
        }

        var filteredRows = ApplyQuery(rows, query).ToArray();
        var severityCounts = CreateSeverityCounts();
        foreach (var row in filteredRows)
        {
            severityCounts[(string)row[SeverityKey]!]++;
        }

        var totalSeverityCounts = CreateSeverityCounts();
        foreach (var row in rows)
        {
            totalSeverityCounts[(string)row[SeverityKey]!]++;
        }

        return new JObject
        {
            ["count"] = filteredRows.Length,
            ["totalCount"] = rows.Count,
            ["severityCounts"] = JObject.FromObject(severityCounts),
            ["totalSeverityCounts"] = JObject.FromObject(totalSeverityCounts),
            ["hasErrors"] = severityCounts["Error"] > 0,
            ["hasWarnings"] = severityCounts["Warning"] > 0,
            ["filter"] = query?.ToJson() ?? [],
            ["rows"] = new JArray(filteredRows),
            ["groups"] = BuildGroups(filteredRows, query?.GroupBy),
        };
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
        IReadOnlyList<JObject> bestPracticeRows = await Task.Run(
            () => AnalyzeBestPracticeFindings(bestPracticeCandidateFiles, bestPracticeProjectLookup),
            context.CancellationToken).ConfigureAwait(false);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        PublishBestPracticeRows(context.Dte, bestPracticeRows);
        return bestPracticeRows;
    }

    private static IReadOnlyDictionary<string, string> CreateBestPracticeProjectLookup(DTE2 dte, IReadOnlyList<string> files)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Dictionary<string, string> projectNamesByFile = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string? projectUniqueName = SolutionFileLocator.TryFindProjectUniqueName(dte, file);
            if (!string.IsNullOrWhiteSpace(projectUniqueName))
            {
                projectNamesByFile[file] = projectUniqueName!;
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

        return projectNamesByFile.TryGetValue(file, out string? projectUniqueName)
            ? projectUniqueName ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> GetBestPracticeCandidateFiles(DTE2 dte, IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        // First add files from error rows (if any).
        foreach (var path in rows
            .Select(row => row["file"]?.ToString())
            .OfType<string>()
            .Where(IsBestPracticeCandidateFile))
        {
            if (seen.Add(path))
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

            if (seen.Add(path))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static bool IsBestPracticeCandidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        foreach (var fragment in IgnoredBestPracticePathFragments)
        {
            if (fullPath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        var fileName = Path.GetFileName(fullPath);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        return !string.IsNullOrWhiteSpace(extension) && BestPracticeCodeExtensions.Contains(extension);
    }

    private static IReadOnlyList<JObject> AnalyzeBestPracticeFindings(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? projectNamesByFile = null,
        string? contentOverride = null)
    {
        List<JObject> findings = [];

        foreach (string file in files)
        {
            string content = contentOverride ?? SafeReadFile(file);
            int perFileFindings = 0;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            string projectUniqueName = TryGetBestPracticeProjectUniqueName(projectNamesByFile, file);
            IEnumerable<JObject> fileFindings = BestPracticeAnalyzer.AnalyzeFile(file, content);

            foreach (JObject finding in fileFindings)
            {
                if (!string.IsNullOrWhiteSpace(projectUniqueName))
                {
                    finding[ProjectKey] = projectUniqueName;
                }

                findings.Add(finding);
                perFileFindings++;
                if (perFileFindings >= MaxBestPracticeFindingsPerFile)
                {
                    break;
                }
            }
        }

        return [.. findings
            .GroupBy(CreateFindingIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

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

        return AnalyzeBestPracticeFindings([filePath], contentOverride: content);
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
