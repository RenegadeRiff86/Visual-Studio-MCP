using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private const string ProjectNotFoundCode = "project_not_found";
    private const string SolutionNotOpenCode = "solution_not_open";
    private const string UnsupportedProjectTypeCode = "unsupported_project_type";
    private const string UniqueNamePropertyName = "uniqueName";
    private const string FrameworkOrigin = "framework";
    private static readonly string[] PreferredOutputExtensions = [".vsix", ".exe", ".dll", ".winmd"];

    // Data class for extracted project item info (no COM dependencies)
    private sealed class ProjectItemData(
        string name,
        string[] paths,
        bool isFolder,
        string kind,
        string? itemType,
        string? subType,
        bool isProjectFile = false)
    {
        public string Name { get; } = name;
        public string[] Paths { get; } = paths;
        public bool IsFolder { get; } = isFolder;
        public string Kind { get; } = kind;
        public string? ItemType { get; } = itemType;
        public string? SubType { get; } = subType;
        public bool IsProjectFile { get; } = isProjectFile;
    }

    private static void EnsureSolutionOpen(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (dte.Solution?.IsOpen != true)
            throw new CommandErrorException(SolutionNotOpenCode, "No solution is open in Visual Studio. Call open_solution with a .sln or .slnx path to open one, or call bind_solution if a solution is already loaded in a different VS instance.");
    }

    private static Project? FindProject(DTE2 dte, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project p in EnumerateAllProjects(dte))
        {
            if (string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.UniqueName, query, StringComparison.OrdinalIgnoreCase) ||
                (p.FullName is { Length: > 0 } && PathNormalization.AreEquivalent(p.FullName, query)))
            {
                return p;
            }
        }
        return null;
    }

    private static CommandErrorException CreateProjectNotFound(string projectQuery)
        => new(ProjectNotFoundCode, $"Project '{projectQuery}' was not found in the solution. Call list_projects to see all project names, then retry with the exact name or path.");

    private static IEnumerable<Project> EnumerateAllProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project p in dte.Solution.Projects)
        {
            foreach (Project proj in EnumerateProjectTree(p))
            {
                yield return proj;
            }
        }
    }

    private static IEnumerable<Project> EnumerateProjectTree(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject is { } sub)
                {
                    foreach (Project p in EnumerateProjectTree(sub))
                    {
                        yield return p;
                    }
                }
            }
        }
        else
        {
            yield return project;
        }
    }

    private static IReadOnlyList<string> GetStartupProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (dte.Solution.SolutionBuild.StartupProjects is object[] arr)
                return [.. arr.OfType<string>()];
        }
        catch (COMException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SolutionProjectCommands), "Failed to read startup projects; returning an empty list", ex);
        }
        return [];
    }

    private static JObject ProjectToJson(Project p, IReadOnlyCollection<string> startupProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string uniqueName = p.UniqueName;
        string? fullName = null;
        try
        {
            fullName = p.FullName;
        }
        catch (COMException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SolutionProjectCommands), $"Failed to resolve project path for '{p.Name}' because FullName threw COM", ex);
        }
        catch (NotImplementedException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SolutionProjectCommands), $"Failed to resolve project path for '{p.Name}' because FullName is not implemented", ex);
        }
        catch (NotSupportedException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SolutionProjectCommands), $"Failed to resolve project path for '{p.Name}' because FullName is not supported", ex);
        }

        return new JObject
        {
            ["name"] = p.Name,
            [UniqueNamePropertyName] = uniqueName,
            ["path"] = fullName ?? string.Empty,
            ["kind"] = p.Kind,
            ["isStartup"] = startupProjects.Any(s =>
                string.Equals(s, uniqueName, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static IEnumerable<ProjectItem> EnumerateProjectItems(ProjectItems? items)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (items is null)
        {
            yield break;
        }

        foreach (ProjectItem item in items)
        {
            yield return item;

            foreach (ProjectItem child in EnumerateProjectItems(item.ProjectItems))
            {
                yield return child;
            }
        }
    }

    private static string[] GetProjectItemPaths(ProjectItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (item.FileCount <= 0)
        {
            return [];
        }

        List<string> paths = [];
        for (int index = 1; index <= item.FileCount; index++)
        {
            try
            {
                string candidate = item.FileNames[(short)index];
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    paths.Add(PathNormalization.NormalizeFilePath(candidate));
                }
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Unable to read project item path '{item.Name}' index {index}: {ex.Message}");
            }
        }

        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // Extract project item data into plain C# types (must run on UI thread)
    private static IEnumerable<ProjectItemData> ExtractProjectItems(Project project, string? pathFilter = null, string solutionDirectory = "")
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string? projectFilePath = TryGetProjectFullName(project);
        if (!string.IsNullOrWhiteSpace(projectFilePath))
        {
            string[] projectFilePaths = [PathNormalization.NormalizeFilePath(projectFilePath)];
            if (string.IsNullOrEmpty(pathFilter) || MatchesPathFilter(projectFilePaths, pathFilter, solutionDirectory))
            {
                yield return new ProjectItemData(
                    Path.GetFileName(projectFilePath),
                    projectFilePaths,
                    isFolder: false,
                    project.Kind ?? string.Empty,
                    "ProjectFile",
                    subType: null,
                    isProjectFile: true);
            }
        }

        foreach (ProjectItem item in EnumerateProjectItems(project.ProjectItems))
        {
            string[] paths = GetProjectItemPaths(item);

            // Apply path filter early to avoid extracting unneeded items
            if (!string.IsNullOrEmpty(pathFilter) && !MatchesPathFilter(paths, pathFilter, solutionDirectory))
            {
                continue;
            }

            string kind = item.Kind ?? string.Empty;
            string? itemType = TryGetPropertyString(item.Properties, "ItemType");
            string? subType = TryGetPropertyString(item.Properties, "SubType");
            yield return new ProjectItemData(item.Name, paths, item.FileCount == 0, kind, itemType, subType);
        }
    }

    private static string? TryGetProjectFullName(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string fullName = project.FullName;
            return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
        }
        catch (COMException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SolutionProjectCommands), $"Failed to read project file path for '{project.Name}'", ex);
            return null;
        }
    }

    private static JToken? ToJsonToken(object? value)
    {
        return value switch
        {
            null => JValue.CreateNull(),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => JToken.FromObject(value),
            DateTime dateTime => dateTime.ToString("O"),
            Array array => new JArray(array.Cast<object?>().Select(ToJsonToken)),
            _ => value.ToString() is { Length: > 0 } text ? text : null,
        };
    }

    private static string[] SplitRequestedNames(string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return [];
        }

        return [.. names!
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool MatchesPathFilter(IEnumerable<string> paths, string? pathFilter, string solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(pathFilter))
        {
            return true;
        }

        string[] normalizedPaths = [..paths];
        if (normalizedPaths.Length == 0)
        {
            return false;
        }

        string filter = (pathFilter ?? string.Empty).Replace('/', '\\').Trim();
        if (Path.IsPathRooted(filter))
        {
            string normalizedFilter = PathNormalization.NormalizeFilePath(filter);
            return normalizedPaths.Any(path =>
                PathNormalization.AreEquivalent(path, normalizedFilter) ||
                path.StartsWith(normalizedFilter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        string rootedFilter = PathNormalization.NormalizeFilePath(Path.Combine(solutionDirectory, filter));
        if (normalizedPaths.Any(path =>
            PathNormalization.AreEquivalent(path, rootedFilter) ||
            path.StartsWith(rootedFilter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string normalizedFragment = filter.Trim('\\');
        return normalizedPaths.Any(path =>
            path.IndexOf(normalizedFragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static JObject ProjectItemToJson(ProjectItem item, string[] paths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = item.Name,
            ["path"] = paths.FirstOrDefault(),
            ["paths"] = new JArray(paths),
            ["kind"] = item.Kind ?? string.Empty,
            ["itemType"] = TryGetPropertyString(item.Properties, "ItemType") ?? string.Empty,
            ["subType"] = TryGetPropertyString(item.Properties, "SubType") ?? string.Empty,
            ["fileCount"] = item.FileCount,
        };
    }

    private static JObject ProjectItemToJsonFromData(ProjectItemData data)
    {
        string? primaryPath = data.Paths.FirstOrDefault();
        return new JObject
        {
            ["name"] = data.Name,
            ["path"] = primaryPath,
            ["paths"] = new JArray(data.Paths),
            ["isFolder"] = data.IsFolder,
            ["isProjectFile"] = data.IsProjectFile,
            ["extension"] = data.IsFolder ? string.Empty : Path.GetExtension(primaryPath ?? string.Empty).ToLowerInvariant(),
            ["kind"] = data.Kind,
            ["itemType"] = data.ItemType ?? string.Empty,
            ["subType"] = data.SubType ?? string.Empty,
            ["fileCount"] = data.IsFolder ? 0 : data.Paths.Length,
        };
    }

    private static JObject ProjectSummaryToJson(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = project.Name,
            [UniqueNamePropertyName] = project.UniqueName,
            ["path"] = project.FullName,
            ["kind"] = project.Kind,
        };
    }

}
