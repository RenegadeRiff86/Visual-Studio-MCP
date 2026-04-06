using Microsoft.VisualStudio.OperationProgress;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class ReadinessService
{
    private const int DefaultReadinessTimeoutMilliseconds = 120_000;
    private const int PollIntervalMilliseconds = 500;
    private const int StableStatusBarSampleCount = 2;

    public async Task<JObject> WaitForReadyAsync(IdeCommandContext context, int timeoutMilliseconds, bool afterEdit = false)
    {
        string solutionPath = await GetOpenSolutionPathAsync(context).ConfigureAwait(false);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        await context.Logger.LogAsync("IDE Bridge: waiting for IntelliSense readiness", context.CancellationToken).ConfigureAwait(false);
        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds <= 0 ? DefaultReadinessTimeoutMilliseconds : timeoutMilliseconds);
        IVsOperationProgressStatusService? service = await context.Package.GetServiceAsync(typeof(SVsOperationProgressStatusService)).ConfigureAwait(false) as IVsOperationProgressStatusService;
        IVsOperationProgressStageStatusForSolutionLoad? stage = service?.GetStageStatusForSolutionLoad(CommonOperationProgressStageIds.Intellisense);
        IVsStatusbar? statusbar = await GetStatusBarAsync(context).ConfigureAwait(false);
        DateTimeOffset deadline = startedAt.Add(timeout);

        // After an edit, VS needs a moment to schedule IntelliSense re-analysis.
        // Wait one poll interval so IsInProgress has a chance to transition from
        // idle to active before we evaluate readiness.
        if (afterEdit)
        {
            await Task.Delay(PollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
        }

        int readyStatusSamples = 0;
        string lastStatusBarText = string.Empty;
        bool statusBarReady = false;
        bool stageInProgress = false;
        bool intellisenseCompleted = false;
        if (stage is not null || statusbar is not null)
        {
            (intellisenseCompleted, stageInProgress, lastStatusBarText, statusBarReady)
                = await SampleReadinessStateAsync(stage, statusbar, context.CancellationToken).ConfigureAwait(false);
        }

        string satisfiedBy = intellisenseCompleted ? "intellisense" : "pending";

        while (DateTimeOffset.UtcNow < deadline)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            (intellisenseCompleted, stageInProgress, lastStatusBarText, statusBarReady)
                = await SampleReadinessStateAsync(stage, statusbar, context.CancellationToken).ConfigureAwait(false);

            if (intellisenseCompleted)
            {
                satisfiedBy = "intellisense";
                break;
            }

            readyStatusSamples = statusBarReady ? readyStatusSamples + 1 : 0;
            if (readyStatusSamples >= StableStatusBarSampleCount)
            {
                satisfiedBy = "status-bar";
                break;
            }

            await Task.Delay(PollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
        }

        bool timedOut = satisfiedBy == "pending";
        if (timedOut)
        {
            satisfiedBy = "timeout";
        }

        await context.Logger.LogAsync($"IDE Bridge: IntelliSense ready (satisfiedBy={satisfiedBy})", context.CancellationToken).ConfigureAwait(false);

        return new JObject
        {
            ["solutionPath"] = solutionPath,
            ["serviceAvailable"] = service is not null,
            ["intellisenseStageAvailable"] = stage is not null,
            ["intellisenseCompleted"] = intellisenseCompleted,
            ["timedOut"] = timedOut,
            ["elapsedMilliseconds"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            ["isInProgress"] = stageInProgress,
            ["statusBarAvailable"] = statusbar is not null,
            ["statusBarText"] = lastStatusBarText,
            ["statusBarReady"] = statusBarReady,
            ["readyStatusSamples"] = readyStatusSamples,
            ["satisfiedBy"] = satisfiedBy,
        };
    }

    private static async Task<string> GetOpenSolutionPathAsync(IdeCommandContext context)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        if (context.Dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException("solution_not_open", "No solution is open.");
        }

        return context.Dte.Solution.FullName;
    }

    private static async Task<IVsStatusbar?> GetStatusBarAsync(IdeCommandContext context)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        return await context.Package.GetServiceAsync(typeof(SVsStatusbar)).ConfigureAwait(true) as IVsStatusbar;
    }

    private static async Task<(bool IntellisenseCompleted, bool StageInProgress, string StatusBarText, bool StatusBarReady)> SampleReadinessStateAsync(
        IVsOperationProgressStageStatusForSolutionLoad? stage,
        IVsStatusbar? statusbar,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        bool stageInProgress = stage?.IsInProgress ?? false;
        string statusBarText = TryGetStatusBarText(statusbar);
        bool statusBarReady = IsReadyStatusText(statusBarText);
        return (!stageInProgress, stageInProgress, statusBarText, statusBarReady);
    }

    private static string TryGetStatusBarText(IVsStatusbar? statusbar)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (statusbar is null)
        {
            return string.Empty;
        }

        try
        {
            statusbar.GetText(out string? text);
            return text?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsReadyStatusText(string text)
    {
        return string.Equals(text.Trim(), "Ready", StringComparison.OrdinalIgnoreCase);
    }
}
