using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class BuildService(ReadinessService readinessService)
{
    private const int DefaultBuildTimeoutMilliseconds = 600_000;
    private const int BuildPollIntervalMilliseconds = 500;

    private const string SolutionNotOpenCode = "solution_not_open";
    private const string NoSolutionOpen = "No solution is open.";
    private const string SolutionPathKey = "solutionPath";
    private const string ActiveConfigurationKey = "activeConfiguration";
    private const string ActivePlatformKey = "activePlatform";

    private readonly ReadinessService _readinessService = readinessService;

    public async Task<JObject> BuildSolutionAsync(IdeCommandContext context, int timeoutMilliseconds, string? configuration, string? platform)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var dte = context.Dte;
        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        var solutionBuild = dte.Solution.SolutionBuild;
        if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            throw new CommandErrorException("build_in_progress", "A build is already in progress.");
        }

        TryActivateConfiguration(solutionBuild, configuration, platform);

        var startedAt = DateTimeOffset.UtcNow;
        await context.Logger.LogAsync($"IDE Bridge: build starting ({dte.Solution.FullName})", context.CancellationToken).ConfigureAwait(true);
        solutionBuild.Build(true);

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds <= 0 ? DefaultBuildTimeoutMilliseconds : timeoutMilliseconds);
        while (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new CommandErrorException("timeout", "Timed out waiting for the build to finish.");
            }

            await Task.Delay(BuildPollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        }

        var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        var succeeded = solutionBuild.LastBuildInfo == 0;
        await context.Logger.LogAsync(
            $"IDE Bridge: build {(succeeded ? "succeeded" : "failed")} in {elapsed:0}ms",
            context.CancellationToken).ConfigureAwait(true);

        return new JObject
        {
            [SolutionPathKey] = dte.Solution.FullName,
            [ActiveConfigurationKey] = solutionBuild.ActiveConfiguration?.Name ?? string.Empty,
            [ActivePlatformKey] = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty,
            ["lastBuildInfo"] = solutionBuild.LastBuildInfo,
            ["succeeded"] = solutionBuild.LastBuildInfo == 0,
            ["elapsedMilliseconds"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
        };
    }

    public async Task<JObject> GetBuildStateAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        var solutionBuild = dte.Solution.SolutionBuild;
        var lastBuildInfoKnown = true;
        var lastBuildInfoValue = 0;
        string? lastBuildInfoReason = null;

        try
        {
            lastBuildInfoValue = solutionBuild.LastBuildInfo;
        }
        catch (COMException ex)
        {
            lastBuildInfoKnown = false;
            lastBuildInfoReason = ex.Message;
        }

        var buildStatus = new JObject
        {
            [SolutionPathKey] = dte.Solution.FullName,
            [ActiveConfigurationKey] = solutionBuild.ActiveConfiguration?.Name ?? string.Empty,
            [ActivePlatformKey] = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty,
            ["buildState"] = solutionBuild.BuildState.ToString(),
            ["lastBuildInfoKnown"] = lastBuildInfoKnown,
            ["lastBuildInfo"] = lastBuildInfoKnown ? lastBuildInfoValue : JValue.CreateNull(),
        };

        if (!string.IsNullOrWhiteSpace(lastBuildInfoReason))
        {
            buildStatus["lastBuildInfoReason"] = lastBuildInfoReason;
        }

        return buildStatus;
    }

    public async Task<JObject> ListConfigurationsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        var solutionBuild = dte.Solution.SolutionBuild;
        var activeConfiguration = solutionBuild.ActiveConfiguration?.Name ?? string.Empty;
        var activePlatform = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty;
        var items = new JArray();

        foreach (SolutionConfiguration2 item in solutionBuild.SolutionConfigurations)
        {
            items.Add(new JObject
            {
                ["name"] = item.Name ?? string.Empty,
                ["platform"] = item.PlatformName ?? string.Empty,
                ["isActive"] = string.Equals(item.Name, activeConfiguration, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.PlatformName ?? string.Empty, activePlatform, StringComparison.OrdinalIgnoreCase),
            });
        }

        return new JObject
        {
            [SolutionPathKey] = dte.Solution.FullName,
            [ActiveConfigurationKey] = activeConfiguration,
            [ActivePlatformKey] = activePlatform,
            ["count"] = items.Count,
            ["items"] = items,
        };
    }

    public async Task<JObject> SetConfigurationAsync(DTE2 dte, string configuration, string? platform)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        var solutionBuild = dte.Solution.SolutionBuild;
        var activated = TryActivateConfiguration(solutionBuild, configuration, platform, requireMatch: true);
        if (!activated)
        {
            throw new CommandErrorException(
                "build_configuration_not_found",
                $"Configuration '{configuration}' with platform '{platform ?? "<any>"}' was not found.");
        }

        return new JObject
        {
            [SolutionPathKey] = dte.Solution.FullName,
            [ActiveConfigurationKey] = solutionBuild.ActiveConfiguration?.Name ?? string.Empty,
            [ActivePlatformKey] = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty,
        };
    }

    public async Task<JObject> BuildProjectAsync(IdeCommandContext context, int timeoutMilliseconds, string projectName, string? configuration, string? platform)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var dte = context.Dte;
        if (dte.Solution?.IsOpen != true)
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);

        var solutionBuild = dte.Solution.SolutionBuild;
        if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
            throw new CommandErrorException("build_in_progress", "A build is already in progress.");

        string? uniqueName = FindProjectUniqueName(dte, projectName);
        if (uniqueName is null)
            throw new CommandErrorException("project_not_found", $"Project '{projectName}' was not found in the solution.");

        TryActivateConfiguration(solutionBuild, configuration, platform);

        string activeConfig = solutionBuild.ActiveConfiguration?.Name ?? "Debug";

        var startedAt = DateTimeOffset.UtcNow;
        await context.Logger.LogAsync($"IDE Bridge: building project '{uniqueName}'", context.CancellationToken).ConfigureAwait(true);
        solutionBuild.BuildProject(activeConfig, uniqueName, false);

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds <= 0 ? DefaultBuildTimeoutMilliseconds : timeoutMilliseconds);
        while (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new CommandErrorException("timeout", "Timed out waiting for the build to finish.");
            await Task.Delay(BuildPollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        }

        var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        var succeeded = solutionBuild.LastBuildInfo == 0;
        await context.Logger.LogAsync(
            $"IDE Bridge: project build {(succeeded ? "succeeded" : "failed")} in {elapsed:0}ms",
            context.CancellationToken).ConfigureAwait(true);

        return new JObject
        {
            ["projectName"] = projectName,
            ["projectUniqueName"] = uniqueName,
            [SolutionPathKey] = dte.Solution.FullName,
            [ActiveConfigurationKey] = solutionBuild.ActiveConfiguration?.Name ?? string.Empty,
            [ActivePlatformKey] = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty,
            ["lastBuildInfo"] = solutionBuild.LastBuildInfo,
            ["succeeded"] = succeeded,
            ["elapsedMilliseconds"] = elapsed,
        };
    }

    private static string? FindProjectUniqueName(DTE2 dte, string projectName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (EnvDTE.Project project in dte.Solution.Projects)
        {
            if (string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase)
                || (project.UniqueName?.EndsWith(projectName, StringComparison.OrdinalIgnoreCase) == true))
            {
                return project.UniqueName;
            }
        }
        return null;
    }

    public async Task<JObject> BuildAndCaptureErrorsAsync(IdeCommandContext context, int timeoutMilliseconds, bool waitForIntellisense)
    {
        var build = await BuildSolutionAsync(context, timeoutMilliseconds, null, null).ConfigureAwait(true);
        if (waitForIntellisense)
        {
            build["readiness"] = await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        }

        return build;
    }

    private static bool TryActivateConfiguration(SolutionBuild solutionBuild, string? configuration, string? platform, bool requireMatch = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (string.IsNullOrWhiteSpace(configuration) && string.IsNullOrWhiteSpace(platform))
        {
            return true;
        }

        foreach (SolutionConfiguration2 item in solutionBuild.SolutionConfigurations)
        {
            if (!string.IsNullOrWhiteSpace(configuration) &&
                !string.Equals(item.Name, configuration, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(platform) &&
                !string.Equals(item.PlatformName, platform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.Activate();
            return true;
        }

        return !requireMatch;
    }
}
