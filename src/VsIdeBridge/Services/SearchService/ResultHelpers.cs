using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateSolutionFiles(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Solution?.IsOpen != true)
        {
            yield break;
        }

        foreach (Project project in dte.Solution.Projects)
        {
            if (project is null)
            {
                continue;
            }

            foreach ((string Path, string ProjectUniqueName) file in EnumerateProjectFiles(project))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateProjectFiles(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project is null)
        {
            yield break;
        }

        if (string.Equals(project.Kind, EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item is null)
                {
                    continue;
                }

                if (item.SubProject is not null)
                {
                    foreach ((string Path, string ProjectUniqueName) file in EnumerateProjectFiles(item.SubProject))
                    {
                        yield return file;
                    }
                }
            }

            yield break;
        }

        foreach (ProjectItem item in project.ProjectItems)
        {
            if (item is null)
            {
                continue;
            }

            foreach ((string Path, string ProjectUniqueName) file in EnumerateProjectItemFiles(item, project.UniqueName))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateProjectItemFiles(ProjectItem item, string projectUniqueName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (item is null)
        {
            yield break;
        }

        if (item.FileCount > 0)
        {
            for (short i = 1; i <= item.FileCount; i++)
            {
                string? fileName = item.FileNames[i];
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    yield return (PathNormalization.NormalizeFilePath(fileName), projectUniqueName);
                }
            }
        }

        if (item.ProjectItems is null)
        {
            yield break;
        }

        foreach (ProjectItem child in item.ProjectItems)
        {
            if (child is null)
            {
                continue;
            }

            foreach ((string Path, string ProjectUniqueName) file in EnumerateProjectItemFiles(child, projectUniqueName))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateOpenFiles(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (Document document in dte.Documents)
        {
            string? fullName = null;
            try
            {
                fullName = document.FullName;
            }
            catch (COMException ex)
            {
                TraceSearchFailure("EnumerateOpenDocumentTargets", ex);
            }

            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            string normalizedPath = PathNormalization.NormalizeFilePath(fullName);
            yield return (normalizedPath, document.ProjectItem?.ContainingProject?.UniqueName ?? string.Empty);
        }
    }

    private static (string Path, string ProjectUniqueName) TryGetActiveDocumentTarget(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Document? activeDocument = dte.ActiveDocument;
        if (activeDocument is null || string.IsNullOrWhiteSpace(activeDocument.FullName))
        {
            return (string.Empty, string.Empty);
        }

        return (PathNormalization.NormalizeFilePath(activeDocument.FullName), activeDocument.ProjectItem?.ContainingProject?.UniqueName ?? string.Empty);
    }

    private static string? NormalizeSearchPathFilter(DTE2 dte, string? pathFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (string.IsNullOrWhiteSpace(pathFilter))
        {
            return null;
        }

        string trimmedPath = pathFilter!.Trim();
        if (Path.IsPathRooted(trimmedPath))
        {
            return PathNormalization.NormalizeFilePath(trimmedPath);
        }

        string normalizedRelativePath = trimmedPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string? solutionDirectory = TryGetSolutionDirectory(dte);
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            string rootedCandidate = Path.GetFullPath(Path.Combine(solutionDirectory, normalizedRelativePath));
            if (File.Exists(rootedCandidate) || Directory.Exists(rootedCandidate))
            {
                return PathNormalization.NormalizeFilePath(rootedCandidate);
            }
        }

        return normalizedRelativePath;
    }

    private static bool MatchesPathFilter(string path, string? pathFilter)
    {
        if (string.IsNullOrWhiteSpace(pathFilter))
        {
            return true;
        }

        string normalizedPath = PathNormalization.NormalizeFilePath(path);
        if (!Path.IsPathRooted(pathFilter))
        {
            string normalizedFilter = pathFilter!.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalizedPath.IndexOf(normalizedFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        if (Directory.Exists(pathFilter))
        {
            string normalizedDirectory = pathFilter!.TrimEnd('\\');
            return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedDirectory + "\\", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(normalizedPath, pathFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetSolutionDirectory(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string? solutionFullName = dte.Solution?.FullName;
        return string.IsNullOrWhiteSpace(solutionFullName)
            ? null
            : Path.GetDirectoryName(solutionFullName);
    }

    private static (string Path, string ProjectUniqueName) TryResolveExplicitDocumentTarget(DTE2 dte, string? pathFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (string.IsNullOrWhiteSpace(pathFilter) || !Path.IsPathRooted(pathFilter) || !File.Exists(pathFilter))
        {
            return (string.Empty, string.Empty);
        }

        foreach ((string Path, string ProjectUniqueName) solutionFile in EnumerateSolutionFiles(dte))
        {
            if (string.Equals(solutionFile.Path, pathFilter, StringComparison.OrdinalIgnoreCase))
            {
                return solutionFile;
            }
        }

        foreach ((string Path, string ProjectUniqueName) openFile in EnumerateOpenFiles(dte))
        {
            if (string.Equals(openFile.Path, pathFilter, StringComparison.OrdinalIgnoreCase))
            {
                return openFile;
            }
        }

        return (pathFilter!, string.Empty);
    }

    private static string[] ReadSearchLines(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string normalizedPath = PathNormalization.NormalizeFilePath(path);
        foreach (Document document in dte.Documents)
        {
            try
            {
                if (!string.Equals(PathNormalization.NormalizeFilePath(document.FullName), normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (document.Object("TextDocument") is TextDocument textDocument)
                {
                    EditPoint editPoint = textDocument.StartPoint.CreateEditPoint();
                    string text = editPoint.GetText(textDocument.EndPoint);
                    return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                }
            }
            catch (COMException ex)
            {
                TraceSearchFailure("ReadSearchLines", ex);
            }
        }

        return File.ReadAllLines(normalizedPath);
    }

    private static void TraceSearchFailure(string operation, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"VsIdeBridge.SearchService {operation}: {ex}");
    }
}
