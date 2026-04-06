using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    internal sealed class IdeQueryProjectPropertiesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0246)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectProperties";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");
            string? requestedNames = args.GetString("names");

            (List<(string Name, JToken? Value)> allProperties, string projectName, string uniqueName) =
                await GetProjectPropertiesAsync(context, projectQuery).ConfigureAwait(false);

            string[] requestedNameArray = SplitRequestedNames(requestedNames);
            List<(string Name, JToken? Value)> filteredProperties = requestedNameArray.Length == 0
                ? allProperties
                : [.. allProperties.Where(property => requestedNameArray.Contains(property.Name, StringComparer.OrdinalIgnoreCase))];

            JObject properties = [];
            foreach ((string name, JToken? value) in filteredProperties)
            {
                properties[name] = value;
            }

            return new CommandExecutionResult(
                $"Read {properties.Count} project propert{(properties.Count == 1 ? "y" : "ies")} from '{projectName}'.",
                new JObject
                {
                    ["project"] = projectName,
                    [UniqueNamePropertyName] = uniqueName,
                    ["count"] = properties.Count,
                    ["properties"] = properties,
                    ["missing"] = new JArray(),
                });
        }

        private static async Task<(List<(string Name, JToken? Value)> Properties, string ProjectName, string UniqueName)> GetProjectPropertiesAsync(
            IdeCommandContext context,
            string projectQuery)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);
            List<(string Name, JToken? Value)> allProperties = [.. EnumerateProjectProperties(project)];
            return (allProperties, project.Name, project.UniqueName);
        }
    }

    internal sealed class IdeQueryProjectConfigurationsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0247)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectConfigurations";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string projectQuery = args.GetRequiredString("project");

            (List<ConfigurationData> extractedConfigurations, string projectName, string uniqueName, string? activeMoniker) =
                await GetProjectConfigurationsAsync(context, projectQuery).ConfigureAwait(false);

            JObject[] sortedConfigurations =
            [..
                extractedConfigurations.Select(configuration => new JObject
                {
                    ["name"] = configuration.Moniker,
                    ["configurationName"] = configuration.ConfigurationName,
                    ["platformName"] = configuration.PlatformName,
                    ["isActive"] = configuration.IsActive,
                })
                .OrderBy(configuration => configuration["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
            ];

            return new CommandExecutionResult(
                $"Found {sortedConfigurations.Length} configuration(s) for '{projectName}'.",
                new JObject
                {
                    ["project"] = projectName,
                    [UniqueNamePropertyName] = uniqueName,
                    ["active"] = activeMoniker,
                    ["count"] = sortedConfigurations.Length,
                    ["configurations"] = new JArray(sortedConfigurations),
                });
        }

        private static async Task<(List<ConfigurationData> Configurations, string ProjectName, string UniqueName, string? ActiveMoniker)> GetProjectConfigurationsAsync(
            IdeCommandContext context,
            string projectQuery)
        {
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

            List<ConfigurationData> extractedConfigurations = [];
            foreach (Configuration configuration in EnumerateProjectConfigurations(configurationManager))
            {
                extractedConfigurations.Add(new ConfigurationData(
                    configuration.ConfigurationName,
                    configuration.PlatformName,
                    GetConfigurationMoniker(configuration) ?? string.Empty,
                    configuration == activeConfiguration));
            }

            return (extractedConfigurations, project.Name, project.UniqueName, GetConfigurationMoniker(activeConfiguration));
        }
    }

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

            (JObject output, string projectName) =
                await GetProjectOutputInputsAsync(
                    context,
                    projectQuery,
                    requestedConfiguration,
                    requestedPlatform,
                    requestedTargetFramework).ConfigureAwait(false);

            return new CommandExecutionResult(
                $"Resolved project outputs for '{projectName}'.",
                output);
        }

        private static async Task<(JObject Output, string ProjectName)> GetProjectOutputInputsAsync(
            IdeCommandContext context,
            string projectQuery,
            string? requestedConfiguration,
            string? requestedPlatform,
            string? requestedTargetFramework)
        {
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
            return (output, project.Name);
        }
    }
}
