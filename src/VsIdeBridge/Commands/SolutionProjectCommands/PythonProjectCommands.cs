using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private const string PythonProjectFileExtension = ".pyproj";
    private const string InterpreterPathPropertyName = "InterpreterPath";
    private const string StartupFilePropertyName = "StartupFile";

    private static Project FindPythonProject(DTE2 dte, string? query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!string.IsNullOrWhiteSpace(query))
        {
            Project project = FindProject(dte, query!) ?? throw CreateProjectNotFound(query!);
            EnsurePythonProject(project, query!);
            return project;
        }

        Array? activeProjects = dte.ActiveSolutionProjects as Array;
        if (activeProjects is not null)
        {
            foreach (object? candidate in activeProjects)
            {
                if (candidate is Project activeProject && IsPythonProject(activeProject))
                {
                    return activeProject;
                }
            }
        }

        Project? onlyPythonProject = null;
        foreach (Project project in EnumerateAllProjects(dte))
        {
            if (!IsPythonProject(project))
            {
                continue;
            }

            if (onlyPythonProject is not null)
            {
                throw new CommandErrorException(
                    UnsupportedProjectTypeCode,
                    "Multiple Python projects are open. Specify --project.");
            }

            onlyPythonProject = project;
        }

        return onlyPythonProject
            ?? throw new CommandErrorException(
                ProjectNotFoundCode,
                "No Python project is open. Specify --project with a .pyproj project.");
    }

    private static void EnsurePythonProject(Project project, string projectQuery)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsPythonProject(project))
        {
            throw new CommandErrorException(
                UnsupportedProjectTypeCode,
                $"Project '{projectQuery}' is not a Python project.");
        }
    }

    private static bool IsPythonProject(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? projectPath = project.FullName;
        return !string.IsNullOrWhiteSpace(projectPath) &&
               string.Equals(Path.GetExtension(projectPath), PythonProjectFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRequiredProjectFilePath(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? projectPath = project.FullName;
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new CommandErrorException(
                FileNotFoundCode,
                $"Project file not found: {projectPath ?? "<unknown>"}");
        }

        return PathNormalization.NormalizeFilePath(projectPath);
    }

    private static bool TrySetAutomationProperty(Project project, string propertyName, object value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Property? property = project.Properties?.Item(propertyName);
            if (property is null)
            {
                return false;
            }

            property.Value = value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (NotImplementedException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string? TryGetAutomationPropertyString(Project project, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Property? property = project.Properties?.Item(propertyName);
            object? value = property?.Value;
            return value?.ToString();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (NotImplementedException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadProjectPropertyFromXml(string projectPath, string propertyName)
    {
        XDocument document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        XElement? root = document.Root;
        if (root is null)
        {
            return null;
        }

        XNamespace xmlNamespace = root.Name.Namespace;
        foreach (XElement propertyGroup in root.Elements(xmlNamespace + "PropertyGroup"))
        {
            XElement? propertyElement = propertyGroup.Element(xmlNamespace + propertyName);
            if (propertyElement is not null && !string.IsNullOrWhiteSpace(propertyElement.Value))
            {
                return propertyElement.Value;
            }
        }

        return null;
    }

    private static void WriteProjectPropertyToXml(string projectPath, string propertyName, string propertyValue)
    {
        XDocument document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        XElement root = document.Root
            ?? throw new CommandErrorException("invalid_project", $"Project file is missing a root element: {projectPath}");
        XNamespace xmlNamespace = root.Name.Namespace;

        XElement? targetPropertyGroup = null;
        XElement? propertyElement = null;
        foreach (XElement propertyGroup in root.Elements(xmlNamespace + "PropertyGroup"))
        {
            XElement? candidate = propertyGroup.Element(xmlNamespace + propertyName);
            if (candidate is not null)
            {
                targetPropertyGroup = propertyGroup;
                propertyElement = candidate;
                break;
            }

            if (targetPropertyGroup is null && !propertyGroup.HasAttributes)
            {
                targetPropertyGroup = propertyGroup;
            }
        }

        targetPropertyGroup ??= new XElement(xmlNamespace + "PropertyGroup");
        if (targetPropertyGroup.Parent is null)
        {
            root.AddFirst(targetPropertyGroup);
        }

        propertyElement ??= new XElement(xmlNamespace + propertyName);
        if (propertyElement.Parent is null)
        {
            targetPropertyGroup.Add(propertyElement);
        }

        propertyElement.Value = propertyValue;
        document.Save(projectPath);
    }

    private static string NormalizeStartupFileValue(string projectPath, string filePath)
    {
        string normalizedFilePath = PathNormalization.NormalizeFilePath(filePath);
        string projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(projectDirectory) &&
            normalizedFilePath.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Uri projectDirectoryUri = new(AppendDirectorySeparator(projectDirectory), UriKind.Absolute);
            Uri fileUri = new(normalizedFilePath, UriKind.Absolute);
            string relativePath = Uri.UnescapeDataString(projectDirectoryUri.MakeRelativeUri(fileUri).ToString());
            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        return normalizedFilePath;
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    internal sealed class IdeSetPythonProjectEnvCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x024F)
    {
        protected override string CanonicalName => "Tools.IdeSetPythonProjectEnv";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string interpreterPath = PathNormalization.NormalizeFilePath(args.GetRequiredString("path"));
            string? projectQuery = args.GetString("project");
            if (!File.Exists(interpreterPath))
            {
                throw new CommandErrorException(FileNotFoundCode, $"Interpreter path not found: {interpreterPath}");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindPythonProject(context.Dte, projectQuery);
            string projectPath = GetRequiredProjectFilePath(project);
            if (!TrySetAutomationProperty(project, InterpreterPathPropertyName, interpreterPath))
            {
                WriteProjectPropertyToXml(projectPath, InterpreterPathPropertyName, interpreterPath);
            }

            project.Save(projectPath);
            return new CommandExecutionResult(
                $"Python interpreter for '{project.Name}' set to '{interpreterPath}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["path"] = projectPath,
                    ["interpreterPath"] = interpreterPath,
                });
        }
    }

    internal sealed class IdeSetPythonStartupFileCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0250)
    {
        protected override string CanonicalName => "Tools.IdeSetPythonStartupFile";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string requestedFilePath = PathNormalization.NormalizeFilePath(args.GetRequiredString("file"));
            string? projectQuery = args.GetString("project");
            if (!File.Exists(requestedFilePath))
            {
                throw new CommandErrorException(FileNotFoundCode, $"Startup file not found: {requestedFilePath}");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindPythonProject(context.Dte, projectQuery);
            string projectPath = GetRequiredProjectFilePath(project);
            string startupFileValue = NormalizeStartupFileValue(projectPath, requestedFilePath);
            if (!TrySetAutomationProperty(project, StartupFilePropertyName, startupFileValue))
            {
                WriteProjectPropertyToXml(projectPath, StartupFilePropertyName, startupFileValue);
            }

            project.Save(projectPath);
            return new CommandExecutionResult(
                $"Startup file for '{project.Name}' set to '{startupFileValue}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["path"] = projectPath,
                    ["startupFile"] = startupFileValue,
                });
        }
    }

    internal sealed class IdeGetPythonStartupFileCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0251)
    {
        protected override string CanonicalName => "Tools.IdeGetPythonStartupFile";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string? projectQuery = args.GetString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindPythonProject(context.Dte, projectQuery);
            string projectPath = GetRequiredProjectFilePath(project);
            string? startupFileValue = TryGetAutomationPropertyString(project, StartupFilePropertyName)
                ?? ReadProjectPropertyFromXml(projectPath, StartupFilePropertyName);
            if (string.IsNullOrWhiteSpace(startupFileValue))
            {
                throw new CommandErrorException(
                    "startup_file_not_set",
                    $"No startup file is configured for Python project '{project.Name}'.");
            }

            return new CommandExecutionResult(
                $"Startup file for '{project.Name}' is '{startupFileValue}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["path"] = projectPath,
                    ["startupFile"] = startupFileValue,
                });
        }
    }
}
