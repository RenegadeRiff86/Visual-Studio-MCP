using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    internal sealed class IdeQueryProjectReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0248)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            bool includeFramework = args.GetBoolean("include-framework", false);
            bool declaredOnly = args.GetBoolean("declared-only", false);

            (JObject[] allReferences, string projectName, string uniqueName) =
                await GetProjectReferencesAsync(context, projectQuery, declaredOnly).ConfigureAwait(false);

            JObject[] references = includeFramework
                ? allReferences
                : [.. allReferences.Where(reference => !IsFrameworkReference(reference))];

            return new CommandExecutionResult(
                $"Found {references.Length} {(declaredOnly ? "declared " : string.Empty)}reference(s) for '{projectName}'.",
                new JObject
                {
                    ["project"] = projectName,
                    [UniqueNamePropertyName] = uniqueName,
                    ["count"] = references.Length,
                    ["totalCount"] = allReferences.Length,
                    ["declaredOnly"] = declaredOnly,
                    ["includeFramework"] = includeFramework,
                    ["omittedFrameworkCount"] = allReferences.Length - references.Length,
                    ["references"] = new JArray(references),
                });
        }

        private static async Task<(JObject[] References, string ProjectName, string UniqueName)> GetProjectReferencesAsync(
            IdeCommandContext context,
            string projectQuery,
            bool declaredOnly)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            JObject[] allReferences;
            if (declaredOnly)
            {
                Project[] solutionProjects = [..EnumerateAllProjects(context.Dte)];
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
                        .OrderBy(reference => reference["name"]?.ToString(), System.StringComparer.OrdinalIgnoreCase),
                ];
            }

            return (allReferences, project.Name, project.UniqueName);
        }
    }
}
