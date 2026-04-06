using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    internal sealed class IdeListProjectsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x023E)
    {
        protected override string CanonicalName => "Tools.IdeListProjects";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject[] projects = await GetProjectsAsync(context).ConfigureAwait(false);

            string projectList = FormatProjectList(projects);
            string message = projects.Length switch
            {
                0 => "No projects found in the solution.",
                1 => $"Found 1 project: {projectList}",
                _ => $"Found {projects.Length} projects: {projectList}",
            };

            return new CommandExecutionResult(
                message,
                new JObject { ["count"] = projects.Length, ["projects"] = new JArray(projects) });
        }

        private static async Task<JObject[]> GetProjectsAsync(IdeCommandContext context)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            IReadOnlyCollection<string> startupProjects = GetStartupProjects(context.Dte);
            return
            [..
                EnumerateAllProjects(context.Dte)
                    .Select(project => ProjectToJson(project, startupProjects))
            ];
        }

        private static string FormatProjectList(JObject[] projects)
        {
            string[] projectNames =
            [..
                projects
                    .Take(10)
                    .Select(project =>
                    {
                        string name = project["name"]?.ToString() ?? "Unknown";
                        bool isStartup = project["isStartup"]?.Value<bool>() == true;
                        string typeString = isStartup ? "startup" : (project["kind"]?.ToString() ?? string.Empty);
                        return typeString.Length > 0 ? $"{name} ({typeString})" : name;
                    })
            ];

            string projectSummary = string.Join(", ", projectNames);
            if (projects.Length > 10)
            {
                projectSummary += $", ... and {projects.Length - 10} more";
            }

            return projectSummary;
        }
    }

    internal sealed class IdeQueryProjectItemsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0245)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectItems";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            string? pathFilter = args.GetString("path");
            int max = args.GetInt32("max", 500);

            (List<ProjectItemData> extractedData, string projectName, string uniqueName) =
                await GetProjectItemsAsync(context, projectQuery, pathFilter, max).ConfigureAwait(false);

            List<JObject> items = [];
            foreach (ProjectItemData data in extractedData)
            {
                items.Add(ProjectItemToJsonFromData(data));
            }

            return new CommandExecutionResult(
                $"Found {items.Count} project item(s) in '{projectName}'.",
                new JObject
                {
                    ["project"] = projectName,
                    [UniqueNamePropertyName] = uniqueName,
                    ["pathFilter"] = pathFilter,
                    ["count"] = items.Count,
                    ["items"] = new JArray(items),
                });
        }

        private static async Task<(List<ProjectItemData> Items, string ProjectName, string UniqueName)> GetProjectItemsAsync(
            IdeCommandContext context,
            string projectQuery,
            string? pathFilter,
            int max)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);
            string solutionDirectory = Path.GetDirectoryName(context.Dte.Solution.FullName) ?? string.Empty;
            List<ProjectItemData> extractedData =
            [.. ExtractProjectItems(project, pathFilter, solutionDirectory)
                .Take(max)];
            return (extractedData, project.Name, project.UniqueName);
        }
    }
}
