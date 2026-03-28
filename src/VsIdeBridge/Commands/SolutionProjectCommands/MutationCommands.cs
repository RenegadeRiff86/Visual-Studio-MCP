using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private const string FileNotFoundCode = "file_not_found";
    private const string ClassLibraryTemplateName = "ClassLibrary";

    private static readonly IReadOnlyDictionary<string, string> CreateProjectTemplateAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["classlib"] = ClassLibraryTemplateName,
            ["class-library"] = ClassLibraryTemplateName,
            ["console"] = "ConsoleApplication",
            ["wpf"] = "WpfApplication",
            ["winforms"] = "WindowsFormsApplication",
            ["web"] = "WebApplication",
            ["webapi"] = "WebApiApplication",
            ["razor"] = "RazorClassLibrary",
            ["test"] = "TestProject",
            ["xunit"] = "xUnitTestProject",
            ["nunit"] = "NUnitTestProject",
            ["mstest"] = "MSTestProject",
        };

    private static readonly IReadOnlyDictionary<string, string> CreateProjectLanguageAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "CSharp",
            ["csharp"] = "CSharp",
            ["c#"] = "CSharp",
            ["vb"] = "VisualBasic",
            ["fs"] = "FSharp",
            ["fsharp"] = "FSharp",
            ["f#"] = "FSharp",
            ["cpp"] = "VC",
            ["c++"] = "VC",
        };

    private static Project FindOrCreateSolutionFolder(DTE2 dte, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project project in dte.Solution.Projects)
        {
            if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }
        }

        return ((Solution2)dte.Solution).AddSolutionFolder(name);
    }

    private static ProjectItem? FindProjectItem(ProjectItems items, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string queryFileName = Path.GetFileName(query);
        foreach (ProjectItem item in items)
        {
            if (string.Equals(item.Name, queryFileName, StringComparison.OrdinalIgnoreCase) ||
                (item.FileCount > 0 && PathNormalization.AreEquivalent(item.FileNames[1], query)))
            {
                return item;
            }

            if (item.ProjectItems is { Count: > 0 })
            {
                ProjectItem? found = FindProjectItem(item.ProjectItems, query);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    internal sealed class IdeAddProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x023F)
    {
        protected override string CanonicalName => "Tools.IdeAddProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectPath = PathNormalization.NormalizeFilePath(args.GetRequiredString("project"));
            if (!File.Exists(projectPath))
            {
                throw new CommandErrorException(FileNotFoundCode, $"Project file not found: {projectPath}");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project addedProject;
            string? folderName = args.GetString("solution-folder");
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                Project solutionFolder = FindOrCreateSolutionFolder(context.Dte, folderName!);
                addedProject = ((SolutionFolder)solutionFolder.Object).AddFromFile(projectPath);
            }
            else
            {
                addedProject = context.Dte.Solution.AddFromFile(projectPath);
            }

            return new CommandExecutionResult(
                $"Project '{addedProject.Name}' added to solution.",
                new JObject
                {
                    ["name"] = addedProject.Name,
                    [UniqueNamePropertyName] = addedProject.UniqueName,
                    ["path"] = addedProject.FullName,
                });
        }
    }

    internal sealed class IdeCreateProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0260)
    {
        protected override string CanonicalName => "Tools.IdeCreateProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string name = args.GetRequiredString("name");
            string templateName = args.GetString("template") ?? ClassLibraryTemplateName;
            string language = args.GetString("language") ?? "CSharp";
            string? directory = args.GetString("directory");
            string? folderName = args.GetString("solution-folder");

            if (CreateProjectTemplateAliases.TryGetValue(templateName, out string? resolvedTemplate))
            {
                templateName = resolvedTemplate;
            }

            if (CreateProjectLanguageAliases.TryGetValue(language, out string? resolvedLanguage))
            {
                language = resolvedLanguage;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Solution2 solution = (Solution2)context.Dte.Solution;
            if (string.IsNullOrWhiteSpace(directory))
            {
                string solutionDirectory = Path.GetDirectoryName(solution.FullName) ?? ".";
                string srcDirectory = Path.Combine(solutionDirectory, "src");
                directory = Path.Combine(Directory.Exists(srcDirectory) ? srcDirectory : solutionDirectory, name);
            }

            directory = PathNormalization.NormalizeFilePath(directory);
            if (Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0)
            {
                throw new CommandErrorException(
                    "already_exists",
                    $"Directory already exists and is not empty: {directory}. Choose a different name or directory.");
            }

            string templatePath = GetTemplatePath(solution, templateName, language);

            Directory.CreateDirectory(directory);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                Project solutionFolder = FindOrCreateSolutionFolder(context.Dte, folderName!);
                ((SolutionFolder)solutionFolder.Object).AddFromTemplate(templatePath, directory, name);
            }
            else
            {
                solution.AddFromTemplate(templatePath, directory, name, false);
            }

            Project? createdProject = null;
            foreach (Project project in solution.Projects)
            {
                if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    createdProject = project;
                    break;
                }
            }

            string projectPath = createdProject?.FullName ?? Path.Combine(directory, name + ".csproj");
            return new CommandExecutionResult(
                $"Created '{templateName}' project '{name}' at {directory}.",
                new JObject
                {
                    ["name"] = createdProject?.Name ?? name,
                    [UniqueNamePropertyName] = createdProject?.UniqueName ?? name,
                    ["path"] = projectPath,
                    ["template"] = templateName,
                    ["language"] = language,
                    ["directory"] = directory,
                });
        }
    }

        private static string GetTemplatePath(Solution2 solution, string templateName, string language)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string templatePath;
            try
            {
                templatePath = solution.GetProjectTemplate(templateName, language);
            }
            catch (COMException ex)
            {
                throw new CommandErrorException(
                    "template_not_found",
                    $"Could not find project template '{templateName}' for language '{language}'. " +
                    "Common aliases: classlib, console, wpf, winforms, web, webapi, test, xunit, nunit, mstest. " +
                    $"You can also pass any VS template name directly (e.g. BlazorServerApp, WorkerService). Error: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(templatePath))
            {
                throw new CommandErrorException(
                    "template_not_found",
                    $"Template '{templateName}' not found for language '{language}'. " +
                    "Common aliases: classlib, console, wpf, winforms, web, webapi, test, xunit, nunit, mstest. " +
                    "You can also pass any VS template name directly (e.g. BlazorServerApp, WorkerService).");
            }

            return templatePath;
        }

    internal sealed class IdeRemoveProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0240)
    {
        protected override string CanonicalName => "Tools.IdeRemoveProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw new CommandErrorException(ProjectNotFoundCode, $"Project not found: {projectQuery}");

            string projectName = project.Name;
            context.Dte.Solution.Remove(project);

            return new CommandExecutionResult(
                $"Project '{projectName}' removed from solution.",
                new JObject { ["name"] = projectName });
        }
    }

    internal sealed class IdeSetStartupProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0241)
    {
        protected override string CanonicalName => "Tools.IdeSetStartupProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw new CommandErrorException(ProjectNotFoundCode, $"Project not found: {projectQuery}");

            context.Dte.Solution.SolutionBuild.StartupProjects = project.UniqueName;

            return new CommandExecutionResult(
                $"Startup project set to '{project.Name}'.",
                new JObject
                {
                    ["name"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                });
        }
    }

    internal sealed class IdeRenameProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0262)
    {
        protected override string CanonicalName => "Tools.IdeRenameProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            string newName = args.GetRequiredString("new-name").Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new CommandErrorException("invalid_name", "Project name cannot be empty.");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            string oldName = project.Name;
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandExecutionResult(
                    $"Project '{oldName}' already uses that name.",
                    new JObject
                    {
                        ["oldName"] = oldName,
                        ["newName"] = oldName,
                        ["project"] = oldName,
                        [UniqueNamePropertyName] = project.UniqueName,
                    });
            }

            try
            {
                project.Name = newName;
            }
            catch (COMException ex)
            {
                throw new CommandErrorException(
                    "rename_failed",
                    $"Could not rename project '{oldName}' to '{newName}': {ex.Message}");
            }

            return new CommandExecutionResult(
                $"Project '{oldName}' renamed to '{project.Name}'.",
                new JObject
                {
                    ["oldName"] = oldName,
                    ["newName"] = project.Name,
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                });
        }
    }

    internal sealed class IdeAddFileToProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0242)
    {
        protected override string CanonicalName => "Tools.IdeAddFileToProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string filePath = PathNormalization.NormalizeFilePath(args.GetRequiredString("file"));
            string projectQuery = args.GetRequiredString("project");
            if (!File.Exists(filePath))
            {
                throw new CommandErrorException(FileNotFoundCode, $"File not found: {filePath}");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            ProjectItem projectItem = project.ProjectItems.AddFromFile(filePath);
            return new CommandExecutionResult(
                $"File '{Path.GetFileName(filePath)}' added to project '{project.Name}'.",
                new JObject
                {
                    ["file"] = filePath,
                    ["fileName"] = projectItem.Name,
                    ["project"] = project.Name,
                });
        }
    }

    internal sealed class IdeRemoveFileFromProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0243)
    {
        protected override string CanonicalName => "Tools.IdeRemoveFileFromProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string fileQuery = args.GetRequiredString("file");
            string projectQuery = args.GetRequiredString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            ProjectItem projectItem = FindProjectItem(project.ProjectItems, fileQuery)
                ?? throw new CommandErrorException(FileNotFoundCode, $"File '{fileQuery}' not found in project '{project.Name}'.");

            string fileName = projectItem.Name;
            projectItem.Remove();

            return new CommandExecutionResult(
                $"File '{fileName}' removed from project '{project.Name}'.",
                new JObject
                {
                    ["fileName"] = fileName,
                    ["project"] = project.Name,
                });
        }
    }
}
