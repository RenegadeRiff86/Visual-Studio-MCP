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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private const string ProjectNotFoundCode = "project_not_found";
    private const string SolutionNotOpenCode = "solution_not_open";
    private const string UnsupportedProjectTypeCode = "unsupported_project_type";
    private const string UniqueNamePropertyName = "uniqueName";
    private const string FrameworkOrigin = "framework";
    private static readonly string[] PreferredOutputExtensions = [".vsix", ".exe", ".dll", ".winmd"];

    private static void EnsureSolutionOpen(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (dte.Solution?.IsOpen != true)
            throw new CommandErrorException(SolutionNotOpenCode, "No solution is open.");
    }

    private static Project? FindProject(DTE2 dte, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var p in EnumerateAllProjects(dte))
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
        => new(ProjectNotFoundCode, $"Project not found: {projectQuery}");

    private static IEnumerable<Project> EnumerateAllProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project p in dte.Solution.Projects)
        {
            foreach (var proj in EnumerateProjectTree(p))
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
                    foreach (var p in EnumerateProjectTree(sub))
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
        catch (System.Runtime.InteropServices.COMException)
        {
            // StartupProjects throws when no startup project is configured; return empty list.
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
        catch (COMException)
        {
            // Unloaded/external projects can throw COM failures when resolving FullName.
        }
        catch (NotImplementedException)
        {
            // Unloaded/external projects can report that FullName is not implemented.
        }
        catch (NotSupportedException)
        {
            // Unloaded/external projects throw E_NOTIMPL from GetFileName(); treat path as empty.
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

            foreach (var child in EnumerateProjectItems(item.ProjectItems))
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

        List<string> paths = new List<string>();
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

    private static JToken? TryGetPropertyValue(Properties? properties, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (properties is null)
        {
            return null;
        }

        try
        {
            return ToJsonToken(properties.Item(name)?.Value);
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Project property '{name}' is unavailable: {ex.Message}");
            return null;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Project property '{name}' could not be read: {ex.Message}");
            return null;
        }
        catch (NotImplementedException ex)
        {
            Debug.WriteLine($"Project property '{name}' is not implemented by this project type: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Project property '{name}' is not supported by this project type: {ex.Message}");
            return null;
        }
    }

    private static JToken? TryGetNormalizedPropertyValue(Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return NormalizeProjectPropertyValue(project, name, TryGetPropertyValue(project.Properties, name));
    }

    private static string? TryGetPropertyString(Properties? properties, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        JToken? value = TryGetPropertyValue(properties, name);
        return value?.Type == JTokenType.Null
            ? null
            : value?.ToString();
    }

    private static IReadOnlyList<(string Name, JToken Value)> EnumerateProjectProperties(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Properties? properties = project.Properties;
        if (properties is null)
        {
            return [];
        }

        List<(string Name, JToken Value)> values = new List<(string Name, JToken Value)>();

        foreach (Property property in properties)
        {
            string? name = null;
            try
            {
                name = property.Name;
                JToken? value = NormalizeProjectPropertyValue(project, name, ToJsonToken(property.Value));
                if (!string.IsNullOrWhiteSpace(name) && value is not null)
                {
                    values.Add((name, value));
                }
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}': {ex.Message}");
            }
            catch (NotImplementedException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}' because it is not implemented by this project type: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}' because it is not supported by this project type: {ex.Message}");
            }
        }

        return values;
    }

    private static JToken? NormalizeProjectPropertyValue(Project project, string name, JToken? value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (value is null)
        {
            return null;
        }

        return string.Equals(name, "TargetFramework", StringComparison.OrdinalIgnoreCase)
            ? NormalizeTargetFrameworkValue(project, value)
            : value;
    }

    private static JToken NormalizeTargetFrameworkValue(Project project, JToken value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (value.Type == JTokenType.String)
        {
            string current = value.ToString();
            if (!string.IsNullOrWhiteSpace(current) && !int.TryParse(current, out _))
            {
                return value;
            }
        }

        string? targetFrameworks = TryGetPropertyString(project.Properties, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return targetFrameworks;
        }

        string? moniker = TryGetPropertyString(project.Properties, "TargetFrameworkMoniker")
            ?? TryGetPropertyString(project.Properties, "TargetFrameworkMonikers");

        string? friendlyTargetFramework = TryConvertFrameworkMonikerToTfm(
            moniker,
            TryGetPropertyString(project.Properties, "TargetPlatformIdentifier"),
            TryGetPropertyString(project.Properties, "TargetPlatformVersion"));

        return string.IsNullOrWhiteSpace(friendlyTargetFramework)
            ? value
            : new JValue(friendlyTargetFramework);
    }

    private static string? TryConvertFrameworkMonikerToTfm(string? moniker, string? targetPlatformIdentifier, string? targetPlatformVersion)
    {
        if (string.IsNullOrWhiteSpace(moniker))
        {
            return null;
        }

        string monikerText = moniker!;
        string? primaryMoniker = monikerText
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(static value => value.Length > 0);

        if (string.IsNullOrWhiteSpace(primaryMoniker))
        {
            return null;
        }

        const string NetCoreAppPrefix = ".NETCoreApp,Version=v";
        const string NetStandardPrefix = ".NETStandard,Version=v";
        const string NetFrameworkPrefix = ".NETFramework,Version=v";

        string? baseTfm = primaryMoniker switch
        {
            var value when value.StartsWith(NetCoreAppPrefix, StringComparison.OrdinalIgnoreCase)
                => "net" + value.Substring(NetCoreAppPrefix.Length),
            var value when value.StartsWith(NetStandardPrefix, StringComparison.OrdinalIgnoreCase)
                => "netstandard" + value.Substring(NetStandardPrefix.Length),
            var value when value.StartsWith(NetFrameworkPrefix, StringComparison.OrdinalIgnoreCase)
                => "net" + value.Substring(NetFrameworkPrefix.Length).Replace(".", string.Empty),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(baseTfm))
        {
            return null;
        }

        if (!string.Equals(targetPlatformIdentifier, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return baseTfm;
        }

        return string.IsNullOrWhiteSpace(targetPlatformVersion)
            ? baseTfm + "-windows"
            : baseTfm + "-windows" + targetPlatformVersion;
    }

    private static IEnumerable<Configuration> EnumerateProjectConfigurations(ConfigurationManager? configurationManager)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (configurationManager is null)
        {
            yield break;
        }

        foreach (Configuration configuration in configurationManager)
        {
            yield return configuration;
        }
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

        string[] normalizedPaths = paths.ToArray();
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

    private static string? GetConfigurationMoniker(Configuration? configuration)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (configuration is null)
        {
            return null;
        }

        return $"{configuration.ConfigurationName}|{configuration.PlatformName}";
    }

    private static JObject ProjectConfigurationToJson(Configuration configuration, Configuration? activeConfiguration)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string moniker = GetConfigurationMoniker(configuration) ?? string.Empty;
        return new JObject
        {
            ["name"] = moniker,
            ["configurationName"] = configuration.ConfigurationName,
            ["platformName"] = configuration.PlatformName,
            ["isActive"] = string.Equals(moniker, GetConfigurationMoniker(activeConfiguration), StringComparison.OrdinalIgnoreCase),
        };
    }

    private static ConfigurationManager? TryGetConfigurationManager(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.ConfigurationManager;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Project '{project.Name}' does not expose configurations: {ex.Message}");
            return null;
        }
    }

    private static Configuration? TryGetActiveProjectConfiguration(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ConfigurationManager? configurationManager = TryGetConfigurationManager(project);
        if (configurationManager is null)
        {
            return null;
        }

        try
        {
            return configurationManager.ActiveConfiguration;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Unable to read active configuration for '{project.Name}': {ex.Message}");
            return null;
        }
    }

    private static string? GetPrimaryValue(string? value)
    {
        return NormalizeXmlValue(
            value?
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.Length > 0));
    }

    private static string? GetNormalizedProjectPropertyString(Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        JToken? token = TryGetNormalizedPropertyValue(project, name);
        return token is null || token.Type == JTokenType.Null
            ? null
            : NormalizeXmlValue(token.ToString());
    }

    private static string GetNormalizedOutputType(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? raw = GetNormalizedProjectPropertyString(project, "OutputType");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown";
        }

        if (!int.TryParse(raw, out int numericValue))
        {
            return raw!;
        }

        return numericValue switch
        {
            2 => "Library",
            0 or 1 => "Exe",
            _ => raw!,
        };
    }

    private static string[] GetOutputDirectoryCandidates(string projectDirectory, string configurationName, string? platformName, string? targetFramework)
    {
        List<string> candidates = new List<string>();

        void AddCandidate(params string[] parts)
        {
            string[] filtered = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
            if (filtered.Length == 0)
            {
                return;
            }

            string candidate = PathNormalization.NormalizeFilePath(Path.Combine(filtered));
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        AddCandidate(projectDirectory, "bin", configurationName, targetFramework ?? string.Empty);
        AddCandidate(projectDirectory, "bin", configurationName);

        if (!string.IsNullOrWhiteSpace(platformName) && !string.Equals(platformName, "Any CPU", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(projectDirectory, "bin", platformName!, configurationName, targetFramework ?? string.Empty);
            AddCandidate(projectDirectory, "bin", platformName!, configurationName);
        }

        return [.. candidates];
    }

    private static string? FindPrimaryOutputPath(IEnumerable<string> candidateDirectories, string assemblyName)
    {
        foreach (string directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string extension in PreferredOutputExtensions)
            {
                string candidate = Path.Combine(directory, assemblyName + extension);
                if (File.Exists(candidate))
                {
                    return PathNormalization.NormalizeFilePath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] EnumerateOutputArtifacts(string? outputDirectory, string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(outputDirectory)
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(PathNormalization.NormalizeFilePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetFallbackTargetExtension(Project project, string normalizedOutputType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string projectDirectory = Path.GetDirectoryName(project.FullName) ?? string.Empty;
        if (File.Exists(Path.Combine(projectDirectory, "source.extension.vsixmanifest")))
        {
            return ".vsix";
        }

        return string.Equals(normalizedOutputType, "Library", StringComparison.OrdinalIgnoreCase)
            ? ".dll"
            : ".exe";
    }

    private static JObject ProjectOutputToJson(
        Project project,
        string configurationName,
        string? platformName,
        string? targetFramework,
        string assemblyName,
        string normalizedOutputType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string projectDirectory = Path.GetDirectoryName(project.FullName) ?? string.Empty;
        string[] candidateDirectories = GetOutputDirectoryCandidates(projectDirectory, configurationName, platformName, targetFramework);
        string? primaryOutputPath = FindPrimaryOutputPath(candidateDirectories, assemblyName);
        string? outputDirectory = primaryOutputPath is null
            ? candidateDirectories.FirstOrDefault()
            : Path.GetDirectoryName(primaryOutputPath);
        string targetExtension = primaryOutputPath is null
            ? GetFallbackTargetExtension(project, normalizedOutputType)
            : Path.GetExtension(primaryOutputPath);
        string targetName = primaryOutputPath is null
            ? assemblyName
            : Path.GetFileNameWithoutExtension(primaryOutputPath);
        string targetFileName = primaryOutputPath is null
            ? targetName + targetExtension
            : Path.GetFileName(primaryOutputPath);

        return new JObject
        {
            ["project"] = project.Name,
            [UniqueNamePropertyName] = project.UniqueName,
            ["configuration"] = configurationName,
            ["platform"] = platformName,
            ["targetFramework"] = targetFramework,
            ["assemblyName"] = assemblyName,
            ["outputType"] = normalizedOutputType,
            ["outputDirectory"] = outputDirectory,
            ["targetName"] = targetName,
            ["targetExtension"] = targetExtension,
            ["targetFileName"] = targetFileName,
            ["targetPath"] = primaryOutputPath,
            ["exists"] = primaryOutputPath is not null && File.Exists(primaryOutputPath),
            ["searchedDirectories"] = new JArray(candidateDirectories),
            ["artifacts"] = new JArray(EnumerateOutputArtifacts(outputDirectory, assemblyName)),
        };
    }

    private static object? TryGetAutomationProperty(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (target is null)
        {
            return null;
        }

        try
        {
            return target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, binder: null, target, args: null);
        }
        catch (MissingMethodException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' is unavailable: {ex.Message}");
            return null;
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' is invalid: {ex.Message}");
            return null;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' could not be read: {ex.Message}");
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException or ArgumentException or MissingMethodException)
        {
            Debug.WriteLine($"Automation property '{propertyName}' threw: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    private static string? TryGetAutomationString(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetAutomationProperty(target, propertyName)?.ToString();
    }

    private static bool? TryGetAutomationBoolean(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        object? value = TryGetAutomationProperty(target, propertyName);
        return value switch
        {
            bool boolean => boolean,
            _ when bool.TryParse(value?.ToString(), out bool parsed) => parsed,
            _ => null,
        };
    }

    private static int? TryGetAutomationInt32(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        object? value = TryGetAutomationProperty(target, propertyName);
        return value switch
        {
            byte byteValue => byteValue,
            short shortValue => shortValue,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ when int.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static IEnumerable<object> EnumerateAutomationObjects(object? collection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (collection is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var automationObject in enumerable)
        {
            if (automationObject is not null)
            {
                yield return automationObject;
            }
        }
    }

    private static string GetReferenceName(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return string.Empty;
        }

        return identity!
            .Split([','], 2, StringSplitOptions.None)[0]
            .Trim();
    }

    private static string? NormalizeXmlValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value!.Trim();
    }

    private static string? GetElementOrAttributeValue(XElement element, string localName)
    {
        return NormalizeXmlValue(
            element.Attribute(localName)?.Value
            ?? element.Elements().FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value);
    }

    private static bool? TryParseBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static string? TryResolveProjectRelativePath(string projectDirectory, string? include)
    {
        string? normalized = NormalizeXmlValue(include);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        string includePath = normalized!;
        string candidate = Path.IsPathRooted(includePath)
            ? includePath
            : Path.Combine(projectDirectory, includePath);

        return PathNormalization.NormalizeFilePath(candidate);
    }

    private static Project? FindProjectByPath(IEnumerable<Project> projects, string? path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var project in projects)
        {
            if (!string.IsNullOrWhiteSpace(project.FullName) &&
                PathNormalization.AreEquivalent(project.FullName, path))
            {
                return project;
            }
        }

        return null;
    }

    private static string? TryGetProjectVersion(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
        {
            return null;
        }

        return TryGetPropertyString(project.Properties, "Version")
            ?? TryGetPropertyString(project.Properties, "AssemblyVersion")
            ?? TryGetPropertyString(project.Properties, "FileVersion");
    }

    private static JObject GetDeclaredMetadata(XElement element, params string[] excludedNames)
    {
        HashSet<string> excluded = new HashSet<string>(excludedNames, StringComparer.OrdinalIgnoreCase);
        JObject metadata = new JObject();

        foreach (var attribute in element.Attributes())
        {
            if (excluded.Contains(attribute.Name.LocalName))
            {
                continue;
            }

            string? value = NormalizeXmlValue(attribute.Value);
            if (value is not null)
            {
                metadata[attribute.Name.LocalName] = value;
            }
        }

        foreach (var child in element.Elements())
        {
            if (excluded.Contains(child.Name.LocalName))
            {
                continue;
            }

            string? value = NormalizeXmlValue(child.Value);
            if (value is not null)
            {
                metadata[child.Name.LocalName] = value;
            }
        }

        return metadata;
    }

    private static JObject CreateDeclaredReference(
        string? name,
        string? identity,
        string? path,
        string? version,
        bool? copyLocal,
        bool? specificVersion,
        string kind,
        string origin,
        string declaredItemType,
        JObject metadata,
        Project? sourceProject = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = name,
            ["identity"] = identity,
            ["path"] = path,
            ["version"] = version,
            ["culture"] = JValue.CreateNull(),
            ["publicKeyToken"] = JValue.CreateNull(),
            ["runtimeVersion"] = JValue.CreateNull(),
            ["copyLocal"] = ToJsonToken(copyLocal),
            ["specificVersion"] = ToJsonToken(specificVersion),
            ["type"] = JValue.CreateNull(),
            ["kind"] = kind,
            ["origin"] = origin,
            ["sourceProject"] = sourceProject is null ? JValue.CreateNull() : ProjectSummaryToJson(sourceProject),
            ["declared"] = true,
            ["declaredItemType"] = declaredItemType,
            ["metadata"] = metadata,
        };
    }

    private static JObject CreateDeclaredProjectReference(XElement element, string projectDirectory, IEnumerable<Project> solutionProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        string? projectPath = TryResolveProjectRelativePath(projectDirectory, include);
        Project? sourceProject = FindProjectByPath(solutionProjects, projectPath);
        return CreateDeclaredReference(
            sourceProject?.Name ?? Path.GetFileNameWithoutExtension(include),
            include,
            projectPath,
            TryGetProjectVersion(sourceProject),
            copyLocal: null,
            specificVersion: null,
            kind: "project",
            origin: "project",
            declaredItemType: "ProjectReference",
            metadata: GetDeclaredMetadata(element, "Include"),
            sourceProject: sourceProject);
    }

    private static JObject CreateDeclaredPackageReference(XElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        return CreateDeclaredReference(
            include,
            include,
            path: null,
            version: GetElementOrAttributeValue(element, "Version"),
            copyLocal: null,
            specificVersion: null,
            kind: "package",
            origin: "package",
            declaredItemType: "PackageReference",
            metadata: GetDeclaredMetadata(element, "Include", "Version"));
    }

    private static JObject CreateDeclaredFrameworkReference(XElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        return CreateDeclaredReference(
            include,
            include,
            path: null,
            version: GetElementOrAttributeValue(element, "Version"),
            copyLocal: null,
            specificVersion: null,
            kind: FrameworkOrigin,
            origin: FrameworkOrigin,
            declaredItemType: "FrameworkReference",
            metadata: GetDeclaredMetadata(element, "Include", "Version"));
    }

    private static JObject CreateDeclaredAssemblyReference(XElement element, string projectDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        string? hintPath = TryResolveProjectRelativePath(projectDirectory, GetElementOrAttributeValue(element, "HintPath"));
        string origin = hintPath is not null && IsFrameworkReferencePath(hintPath)
            ? FrameworkOrigin
            : (hintPath is null ? "local" : GetReferenceOrigin(sourceProject: null, hintPath));

        return CreateDeclaredReference(
            GetReferenceName(include),
            include,
            hintPath,
            version: GetElementOrAttributeValue(element, "Version"),
            copyLocal: TryParseBoolean(GetElementOrAttributeValue(element, "Private")),
            specificVersion: TryParseBoolean(GetElementOrAttributeValue(element, "SpecificVersion")),
            kind: "assembly",
            origin: origin,
            declaredItemType: "Reference",
            metadata: GetDeclaredMetadata(element, "Include", "HintPath", "Version", "Private", "SpecificVersion"));
    }

    private static JObject? DeclaredReferenceToJson(XElement element, string projectDirectory, IEnumerable<Project> solutionProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return element.Name.LocalName switch
        {
            "ProjectReference" => CreateDeclaredProjectReference(element, projectDirectory, solutionProjects),
            "PackageReference" => CreateDeclaredPackageReference(element),
            "FrameworkReference" => CreateDeclaredFrameworkReference(element),
            "Reference" => CreateDeclaredAssemblyReference(element, projectDirectory),
            _ => null,
        };
    }

    private static JObject[] EnumerateDeclaredProjectReferences(Project project, IReadOnlyCollection<Project> solutionProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(project.FullName))
        {
            throw new CommandErrorException(UnsupportedProjectTypeCode, $"Project '{project.Name}' does not expose a project file path.");
        }

        string projectPath = PathNormalization.NormalizeFilePath(project.FullName);
        if (!File.Exists(projectPath))
        {
            throw new CommandErrorException("project_file_not_found", $"Project file not found: {projectPath}");
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectPath, LoadOptions.None);
        }
        catch (IOException ex)
        {
            throw new CommandErrorException(
                "project_file_read_failed",
                $"Project file could not be read: {projectPath}",
                new { exception = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CommandErrorException(
                "project_file_read_failed",
                $"Project file could not be read: {projectPath}",
                new { exception = ex.Message });
        }
        catch (XmlException ex)
        {
            throw new CommandErrorException(
                "project_file_read_failed",
                $"Project file could not be read: {projectPath}",
                new { exception = ex.Message });
        }

        string projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        return [.. document
            .Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference" or "Reference")
            .Select(element => DeclaredReferenceToJson(element, projectDirectory, solutionProjects))
            .OfType<JObject>()
            .OrderBy(reference => reference["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetReferenceKind(Project? sourceProject, string? path)
    {
        if (sourceProject is not null)
        {
            return "project";
        }

        return string.IsNullOrWhiteSpace(path) ? "unknown" : "assembly";
    }

    private static string GetReferenceOrigin(Project? sourceProject, string? path)
    {
        if (sourceProject is not null)
        {
            return "project";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "unknown";
        }

        string normalizedPath = path!;

        if (IsFrameworkReferencePath(normalizedPath))
        {
            return FrameworkOrigin;
        }

        if (normalizedPath.IndexOf("\\.nuget\\packages\\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "package";
        }

        return "local";
    }

    private static bool IsFrameworkReferencePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedPath = path!;
        return normalizedPath.IndexOf("\\Reference Assemblies\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (normalizedPath.IndexOf("\\packs\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                normalizedPath.IndexOf("\\ref\\", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsFrameworkReference(JObject reference)
    {
        return string.Equals(reference.Value<string>("origin"), FrameworkOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static JObject ProjectReferenceToJson(object reference)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Project? sourceProject = TryGetAutomationProperty(reference, "SourceProject") as Project;
        string? path = TryGetAutomationString(reference, "Path");
        string origin = GetReferenceOrigin(sourceProject, path);
        return new JObject
        {
            ["name"] = TryGetAutomationString(reference, "Name"),
            ["identity"] = TryGetAutomationString(reference, "Identity"),
            ["path"] = path,
            ["version"] = TryGetAutomationString(reference, "Version"),
            ["culture"] = TryGetAutomationString(reference, "Culture"),
            ["publicKeyToken"] = TryGetAutomationString(reference, "PublicKeyToken"),
            ["runtimeVersion"] = TryGetAutomationString(reference, "RuntimeVersion"),
            ["copyLocal"] = ToJsonToken(TryGetAutomationBoolean(reference, "CopyLocal")),
            ["specificVersion"] = ToJsonToken(TryGetAutomationBoolean(reference, "SpecificVersion")),
            ["type"] = ToJsonToken(TryGetAutomationInt32(reference, "Type")),
            ["kind"] = GetReferenceKind(sourceProject, path),
            ["origin"] = origin,
            ["sourceProject"] = sourceProject is null ? JValue.CreateNull() : ProjectSummaryToJson(sourceProject),
        };
    }

    // ── list-projects ─────────────────────────────────────────────────────────

    internal sealed class IdeListProjectsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x023E)
    {
        protected override string CanonicalName => "Tools.IdeListProjects";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            IReadOnlyCollection<string> startupProjects = GetStartupProjects(context.Dte);
            JObject[] projects = EnumerateAllProjects(context.Dte)
                .Select(p => ProjectToJson(p, startupProjects))
                .ToArray();

            return new CommandExecutionResult(
                $"Found {projects.Length} project(s).",
                new JObject { ["count"] = projects.Length, ["projects"] = new JArray(projects) });
        }
    }

    // ── query-project-items ────────────────────────────────────────────────────

    internal sealed class IdeQueryProjectItemsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0245)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectItems";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            string? pathFilter = args.GetString("path");
            int max = args.GetInt32("max", 500);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            string solutionDirectory = Path.GetDirectoryName(context.Dte.Solution.FullName) ?? string.Empty;
            List<JObject> items = new List<JObject>();
            foreach (var projectItem in EnumerateProjectItems(project.ProjectItems))
            {
                string[] paths = GetProjectItemPaths(projectItem);
                if (!MatchesPathFilter(paths, pathFilter, solutionDirectory))
                {
                    continue;
                }

                items.Add(ProjectItemToJson(projectItem, paths));
                if (items.Count >= max)
                {
                    break;
                }
            }

            return new CommandExecutionResult(
                $"Found {items.Count} project item(s) in '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["pathFilter"] = pathFilter,
                    ["count"] = items.Count,
                    ["items"] = new JArray(items),
                });
        }
    }

    // ── query-project-properties ───────────────────────────────────────────────

    internal sealed class IdeQueryProjectPropertiesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0246)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectProperties";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            string[]? requestedNames = SplitRequestedNames(args.GetString("names"));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            JObject properties = new JObject();
            JArray missing = new JArray();
            if (requestedNames.Length == 0)
            {
                foreach (var (name, value) in EnumerateProjectProperties(project))
                {
                    properties[name] = value;
                }
            }
            else
            {
                foreach (var name in requestedNames)
                {
                    JToken? value = TryGetNormalizedPropertyValue(project, name);
                    if (value is null)
                    {
                        missing.Add(name);
                        continue;
                    }

                    properties[name] = value;
                }
            }

            return new CommandExecutionResult(
                $"Read {properties.Count} project propert{(properties.Count == 1 ? "y" : "ies")} from '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["count"] = properties.Count,
                    ["properties"] = properties,
                    ["missing"] = missing,
                });
        }
    }

    // ── query-project-configurations ───────────────────────────────────────────

    internal sealed class IdeQueryProjectConfigurationsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0247)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectConfigurations";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            ConfigurationManager? configurationManager;
            try
            {
                configurationManager = project.ConfigurationManager;
            }
            catch (COMException ex)
            {
                throw new CommandErrorException(
                    UnsupportedProjectTypeCode,
                    $"Project '{project.Name}' does not expose configurations.",
                    new { exception = ex.Message });
            }

            if (configurationManager is null)
            {
                throw new CommandErrorException(
                    UnsupportedProjectTypeCode,
                    $"Project '{project.Name}' does not expose configurations.");
            }

            Configuration? activeConfiguration = null;
            try
            {
                activeConfiguration = configurationManager.ActiveConfiguration;
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Unable to read active configuration for '{project.Name}': {ex.Message}");
            }

            JObject[] configurations = EnumerateProjectConfigurations(configurationManager)
                .Select(configuration => ProjectConfigurationToJson(configuration, activeConfiguration))
                .OrderBy(configuration => configuration["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new CommandExecutionResult(
                $"Found {configurations.Length} configuration(s) for '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["active"] = GetConfigurationMoniker(activeConfiguration),
                    ["count"] = configurations.Length,
                    ["configurations"] = new JArray(configurations),
                });
        }
    }

    // ── query-project-references ───────────────────────────────────────────────

    internal sealed class IdeQueryProjectReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0248)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            bool includeFramework = args.GetBoolean("include-framework", false);
            bool declaredOnly = args.GetBoolean("declared-only", false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            JObject[] allReferences;
            if (declaredOnly)
            {
                Project[] solutionProjects = EnumerateAllProjects(context.Dte).ToArray();
                allReferences = EnumerateDeclaredProjectReferences(project, solutionProjects);
            }
            else
            {
                object? referencesObject = TryGetAutomationProperty(project.Object, "References")
                    ?? throw new CommandErrorException(
                        UnsupportedProjectTypeCode,
                        $"Project '{project.Name}' does not expose automation references.");

                allReferences =
                [
                    .. EnumerateAutomationObjects(referencesObject)
                        .Select(ProjectReferenceToJson)
                        .OrderBy(reference => reference["name"]?.ToString(), StringComparer.OrdinalIgnoreCase),
                ];
            }

            JObject[] references = includeFramework
                ? allReferences
                : [.. allReferences.Where(reference => !IsFrameworkReference(reference))];

            int omittedFrameworkCount = allReferences.Length - references.Length;

            return new CommandExecutionResult(
                $"Found {references.Length} {(declaredOnly ? "declared " : string.Empty)}reference(s) for '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["count"] = references.Length,
                    ["totalCount"] = allReferences.Length,
                    ["declaredOnly"] = declaredOnly,
                    ["includeFramework"] = includeFramework,
                    ["omittedFrameworkCount"] = omittedFrameworkCount,
                    ["references"] = new JArray(references),
                });
        }
    }

    // ── query-project-outputs ─────────────────────────────────────────────────

    internal sealed class IdeQueryProjectOutputsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0249)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectOutputs";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            string? requestedConfiguration = NormalizeXmlValue(args.GetString("configuration"));
            string? requestedPlatform = NormalizeXmlValue(args.GetString("platform"));
            string? requestedTargetFramework = NormalizeXmlValue(args.GetString("target-framework"));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            Configuration? activeConfiguration = TryGetActiveProjectConfiguration(project);
            string configurationName = requestedConfiguration
                ?? NormalizeXmlValue(activeConfiguration?.ConfigurationName)
                ?? NormalizeXmlValue(context.Dte.Solution?.SolutionBuild?.ActiveConfiguration?.Name?.Split('|').FirstOrDefault())
                ?? "Debug";
            string platformName = requestedPlatform
                ?? NormalizeXmlValue(activeConfiguration?.PlatformName)
                ?? NormalizeXmlValue(context.Dte.Solution?.SolutionBuild?.ActiveConfiguration?.Name?.Split('|').Skip(1).FirstOrDefault())
                ?? "Any CPU";
            string? targetFramework = requestedTargetFramework
                ?? GetPrimaryValue(GetNormalizedProjectPropertyString(project, "TargetFramework"));
            string assemblyName = GetNormalizedProjectPropertyString(project, "AssemblyName")
                ?? project.Name;
            string outputType = GetNormalizedOutputType(project);
            JObject output = ProjectOutputToJson(project, configurationName, platformName, targetFramework, assemblyName, outputType);

            return new CommandExecutionResult(
                $"Resolved project outputs for '{project.Name}'.",
                output);
        }
    }

    // ── search-solutions ──────────────────────────────────────────────────────

    internal sealed class IdeSearchSolutionsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0244)
    {
        protected override string CanonicalName => "Tools.IdeSearchSolutions";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string rootPath = args.GetString("path") ?? GetDefaultSearchRoot();
            string? query = args.GetString("query");
            int maxDepth = args.GetInt32("max-depth", 6);
            int maxResults = args.GetInt32("max", 200);

            if (!Directory.Exists(rootPath))
                throw new CommandErrorException("path_not_found", $"Search root not found: {rootPath}");

            List<string> matches = FindSolutions(rootPath, query, maxDepth, maxResults);
            JObject[] results = matches
                .Select(f => new JObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["fileName"] = Path.GetFileName(f),
                    ["path"] = f,
                    ["directory"] = Path.GetDirectoryName(f),
                    ["lastModified"] = File.GetLastWriteTime(f).ToString("O"),
                })
                .ToArray();

            return Task.FromResult(new CommandExecutionResult(
                $"Found {results.Length} solution(s) under '{rootPath}'.",
                new JObject { ["count"] = results.Length, ["root"] = rootPath, ["solutions"] = new JArray(results) }));
        }

        private static string GetDefaultSearchRoot()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string reposPath = Path.Combine(userProfile, "source", "repos");
            return Directory.Exists(reposPath) ? reposPath : userProfile;
        }

        private static List<string> FindSolutions(string root, string? query, int maxDepth, int maxResults)
        {
            List<string> results = new();
            SearchDirectory(root, query, maxDepth, 0, results, maxResults);
            results.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            return results;
        }

        private static void SearchDirectory(string dir, string? query, int maxDepth, int depth, List<string> results, int maxResults)
        {
            if (depth > maxDepth || results.Count >= maxResults) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")))
                {
                    if (results.Count >= maxResults) break;
                    if (query is null || Path.GetFileNameWithoutExtension(file).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(file);
                }
                foreach (string subDir in Directory.EnumerateDirectories(dir))
                {
                    if (results.Count >= maxResults) break;
                    string name = Path.GetFileName(subDir);
                    if (name.StartsWith(".") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                        continue;
                    SearchDirectory(subDir, query, maxDepth, depth + 1, results, maxResults);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Skipping solution search directory '{dir}': {ex.Message}");
            }
            catch (DirectoryNotFoundException ex)
            {
                Debug.WriteLine($"Skipping missing solution search directory '{dir}': {ex.Message}");
            }
        }
    }
}
