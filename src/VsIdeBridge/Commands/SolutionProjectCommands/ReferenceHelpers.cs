using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
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
        return bool.TryParse(value, out bool parsed)
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

        foreach (Project project in projects)
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
        HashSet<string> excluded = new(excludedNames, StringComparer.OrdinalIgnoreCase);
        JObject metadata = [];

        foreach (XAttribute attribute in element.Attributes())
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

        foreach (XElement child in element.Elements())
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
}
