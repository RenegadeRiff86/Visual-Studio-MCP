using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Runtime.InteropServices;
using System.Threading;
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
        (DTE2 dte, SolutionBuild solutionBuild) = await PrepareBuildAsync(context, configuration, platform).ConfigureAwait(true);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = GetDeadline(timeoutMilliseconds);
        string solutionName = dte.Solution.FullName;

        // GetServiceAsync does not require the main thread — release it here.
        IVsSolutionBuildManager2? buildManager = await context.Package.GetServiceAsync(typeof(SVsSolutionBuildManager)).ConfigureAwait(false) as IVsSolutionBuildManager2;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        await context.Logger.LogAsync($"IDE Bridge: build starting ({solutionName})", context.CancellationToken).ConfigureAwait(true);
        await RunSolutionBuildStepAsync(context, solutionBuild, buildManager, clean: false, deadline, "Timed out waiting for the build to finish.").ConfigureAwait(true);

        double elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        bool succeeded = solutionBuild.LastBuildInfo == 0;
        await context.Logger.LogAsync(
            $"IDE Bridge: build {(succeeded ? "succeeded" : "failed")} in {elapsed:0}ms",
            context.CancellationToken).ConfigureAwait(true);

        return CreateBuildResult(dte, solutionBuild, startedAt, operation: "build");
    }

    public async Task<JObject> RebuildSolutionAsync(IdeCommandContext context, int timeoutMilliseconds, string? configuration, string? platform)
    {
        (DTE2 dte, SolutionBuild solutionBuild) = await PrepareBuildAsync(context, configuration, platform).ConfigureAwait(true);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = GetDeadline(timeoutMilliseconds);
        string solutionName = dte.Solution.FullName;

        // GetServiceAsync does not require the main thread — release it here.
        IVsSolutionBuildManager2? buildManager = await context.Package.GetServiceAsync(typeof(SVsSolutionBuildManager)).ConfigureAwait(false) as IVsSolutionBuildManager2;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        await context.Logger.LogAsync($"IDE Bridge: rebuild starting ({solutionName})", context.CancellationToken).ConfigureAwait(true);
        await RunSolutionBuildStepAsync(context, solutionBuild, buildManager, clean: true, deadline, "Timed out waiting for the clean step to finish.").ConfigureAwait(true);
        await RunSolutionBuildStepAsync(context, solutionBuild, buildManager, clean: false, deadline, "Timed out waiting for the rebuild to finish.").ConfigureAwait(true);

        double elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        bool succeeded = solutionBuild.LastBuildInfo == 0;
        await context.Logger.LogAsync(
            $"IDE Bridge: rebuild {(succeeded ? "succeeded" : "failed")} in {elapsed:0}ms",
            context.CancellationToken).ConfigureAwait(true);

        return CreateBuildResult(dte, solutionBuild, startedAt, operation: "rebuild");
    }

    public async Task<JObject> GetBuildStateAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return GetBuildStateCore(dte);
    }

    private static JObject GetBuildStateCore(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        SolutionBuild solutionBuild = dte.Solution.SolutionBuild;
        bool lastBuildInfoKnown = true;
        int lastBuildInfoValue = 0;
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

        JObject buildStatus = new()
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
        return ListConfigurationsCore(dte);
    }

    private static JObject ListConfigurationsCore(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        SolutionBuild solutionBuild = dte.Solution.SolutionBuild;
        string activeConfiguration = solutionBuild.ActiveConfiguration?.Name ?? string.Empty;
        string activePlatform = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty;
        JArray items = [];

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

        SolutionBuild solutionBuild = dte.Solution.SolutionBuild;
        bool activated = TryActivateConfiguration(solutionBuild, configuration, platform, requireMatch: true);
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
        (DTE2 dte, SolutionBuild solutionBuild) = await PrepareBuildAsync(context, configuration, platform).ConfigureAwait(true);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        string uniqueName = FindProjectUniqueName(dte, projectName)
            ?? throw new CommandErrorException("project_not_found", $"Project '{projectName}' was not found in the solution.");
        string activeConfig = solutionBuild.ActiveConfiguration?.Name ?? "Debug";

        // GetServiceAsync does not require the main thread — release it here.
        IVsSolutionBuildManager2? buildManager = await context.Package.GetServiceAsync(typeof(SVsSolutionBuildManager)).ConfigureAwait(false) as IVsSolutionBuildManager2;

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = GetDeadline(timeoutMilliseconds);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        await context.Logger.LogAsync($"IDE Bridge: building project '{uniqueName}'", context.CancellationToken).ConfigureAwait(true);
        await RunProjectBuildAsync(context, solutionBuild, buildManager, activeConfig, uniqueName, deadline).ConfigureAwait(true);

        double elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        bool succeeded = solutionBuild.LastBuildInfo == 0;
        await context.Logger.LogAsync(
            $"IDE Bridge: project build {(succeeded ? "succeeded" : "failed")} in {elapsed:0}ms",
            context.CancellationToken).ConfigureAwait(true);

        return CreateBuildResult(dte, solutionBuild, startedAt, operation: "build", projectName, uniqueName);
    }

    private static async Task RunProjectBuildAsync(
        IdeCommandContext context,
        SolutionBuild solutionBuild,
        IVsSolutionBuildManager2? buildManager,
        string activeConfig,
        string uniqueName,
        DateTimeOffset deadline)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        const string timeoutMessage = "Timed out waiting for the build to finish.";
        if (buildManager is not null)
        {
            BuildCompletionWaiter waiter = new(buildManager);
            try
            {
                solutionBuild.BuildProject(activeConfig, uniqueName, false);
                await WaitForBuildEventAsync(waiter, deadline, context.CancellationToken, timeoutMessage).ConfigureAwait(false);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                waiter.Unsubscribe();
            }
        }
        else
        {
            solutionBuild.BuildProject(activeConfig, uniqueName, false);
            await WaitForBuildCompletionAsync(context, solutionBuild, deadline, timeoutMessage).ConfigureAwait(true);
        }
    }

    private static async Task RunSolutionBuildStepAsync(
        IdeCommandContext context,
        SolutionBuild solutionBuild,
        IVsSolutionBuildManager2? buildManager,
        bool clean,
        DateTimeOffset deadline,
        string timeoutMessage)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        if (buildManager is not null)
        {
            BuildCompletionWaiter waiter = new(buildManager);
            try
            {
                if (clean)
                    solutionBuild.Clean(false);
                else
                    solutionBuild.Build(false);
                await WaitForBuildEventAsync(waiter, deadline, context.CancellationToken, timeoutMessage).ConfigureAwait(false);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                waiter.Unsubscribe();
            }
        }
        else
        {
            if (clean)
                solutionBuild.Clean(true);
            else
                solutionBuild.Build(true);
            await WaitForBuildCompletionAsync(context, solutionBuild, deadline, timeoutMessage).ConfigureAwait(true);
        }
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
        JObject build = await BuildSolutionAsync(context, timeoutMilliseconds, null, null).ConfigureAwait(true);
        if (waitForIntellisense)
        {
            build["readiness"] = await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        }

        return build;
    }

    private static async Task<(DTE2 Dte, SolutionBuild SolutionBuild)> PrepareBuildAsync(IdeCommandContext context, string? configuration, string? platform)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        DTE2 dte = context.Dte;
        if (dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException(SolutionNotOpenCode, NoSolutionOpen);
        }

        SolutionBuild solutionBuild = dte.Solution.SolutionBuild;
        if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            throw new CommandErrorException("build_in_progress", "A build is already in progress.");
        }

        TryActivateConfiguration(solutionBuild, configuration, platform);
        return (dte, solutionBuild);
    }

    private static DateTimeOffset GetDeadline(int timeoutMilliseconds)
    {
        return DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds <= 0 ? DefaultBuildTimeoutMilliseconds : timeoutMilliseconds);
    }

    private static async Task WaitForBuildCompletionAsync(
        IdeCommandContext context,
        SolutionBuild solutionBuild,
        DateTimeOffset deadline,
        string timeoutMessage)
    {
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
            if (solutionBuild.BuildState != vsBuildState.vsBuildStateInProgress)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new CommandErrorException("timeout", timeoutMessage);
            }

            await Task.Delay(BuildPollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static JObject CreateBuildResult(
        DTE2 dte,
        SolutionBuild solutionBuild,
        DateTimeOffset startedAt,
        string operation,
        string? projectName = null,
        string? uniqueName = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        JObject result = new()
        {
            ["operation"] = operation,
            [SolutionPathKey] = dte.Solution.FullName,
            [ActiveConfigurationKey] = solutionBuild.ActiveConfiguration?.Name ?? string.Empty,
            [ActivePlatformKey] = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty,
            ["lastBuildInfo"] = solutionBuild.LastBuildInfo,
            ["succeeded"] = solutionBuild.LastBuildInfo == 0,
            ["elapsedMilliseconds"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
        };

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            result["projectName"] = projectName;
        }

        if (!string.IsNullOrWhiteSpace(uniqueName))
        {
            result["projectUniqueName"] = uniqueName;
        }

        return result;
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

    private static async Task WaitForBuildEventAsync(
        BuildCompletionWaiter waiter,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new CommandErrorException("timeout", timeoutMessage);
        }

        Task buildTask = waiter.CompletionTask;
        Task delayTask = Task.Delay((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue), cancellationToken);
        Task first = await Task.WhenAny(buildTask, delayTask).ConfigureAwait(false);

        if (first == buildTask)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new CommandErrorException("timeout", timeoutMessage);
    }

    private sealed class BuildCompletionWaiter : IVsUpdateSolutionEvents
    {
        private readonly IVsSolutionBuildManager2 _buildManager;
        private readonly TaskCompletionSource<bool> _tcs = new();
        private uint _cookie;

        internal Task CompletionTask => _tcs.Task;

        internal BuildCompletionWaiter(IVsSolutionBuildManager2 buildManager)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _buildManager = buildManager;
            _buildManager.AdviseUpdateSolutionEvents(this, out _cookie);
        }

        internal void Unsubscribe()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie != 0)
            {
                _buildManager.UnadviseUpdateSolutionEvents(_cookie);
                _cookie = 0;
            }
            _tcs.TrySetCanceled();
        }

        int IVsUpdateSolutionEvents.UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;

        int IVsUpdateSolutionEvents.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            _tcs.TrySetResult(true);
            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

        int IVsUpdateSolutionEvents.UpdateSolution_Cancel()
        {
            _tcs.TrySetResult(true);
            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
    }
}
