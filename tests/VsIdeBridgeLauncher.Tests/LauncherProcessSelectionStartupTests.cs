using VsIdeBridgeLauncher;
using Xunit;

namespace VsIdeBridgeLauncher.Tests;

public class LauncherProcessSelectionStartupTests
{
    [Fact]
    public void EvaluateStartupProgress_FailsWhenPrimaryProcessExitsAndNoReplacementExists()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 71,
            primaryHasExited: true,
            primaryExitCode: 99,
            snapshots: [],
            existingProcessIds: LauncherProcessSelectionTestData.NoExistingProcessIds,
            timedOut: false,
            timeoutMilliseconds: LauncherProcessSelectionTestData.StartupTimeoutMilliseconds);

        Assert.Equal(LauncherStartupState.Failed, evaluation.State);
        Assert.Contains("ExitCode=99", evaluation.Error);
    }

    [Fact]
    public void EvaluateStartupProgress_ContinuesWithReplacementProcessWhenPrimaryExits()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 81,
            primaryHasExited: true,
            primaryExitCode: 1,
            snapshots:
            [
                new LauncherProcessSnapshot(82, LauncherProcessSelectionTestData.UtcAtEightPm(10), false, false)
            ],
            existingProcessIds: LauncherProcessSelectionTestData.NoExistingProcessIds,
            timedOut: false,
            timeoutMilliseconds: LauncherProcessSelectionTestData.StartupTimeoutMilliseconds);

        Assert.Equal(LauncherStartupState.Continue, evaluation.State);
        Assert.Equal(82, evaluation.ActiveProcessId);
    }

    [Fact]
    public void EvaluateStartupProgress_SucceedsWhenReplacementProcessShowsBridgeDiscovery()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 91,
            primaryHasExited: true,
            primaryExitCode: 1,
            snapshots:
            [
                new LauncherProcessSnapshot(92, LauncherProcessSelectionTestData.UtcAtEightPm(11), true, false)
            ],
            existingProcessIds: LauncherProcessSelectionTestData.NoExistingProcessIds,
            timedOut: false,
            timeoutMilliseconds: LauncherProcessSelectionTestData.StartupTimeoutMilliseconds);

        Assert.Equal(LauncherStartupState.Succeeded, evaluation.State);
        Assert.Equal(92, evaluation.ActiveProcessId);
    }

    [Fact]
    public void EvaluateStartupProgress_FailsWithTimedOutActiveProcess()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 101,
            primaryHasExited: false,
            primaryExitCode: null,
            snapshots:
            [
                new LauncherProcessSnapshot(102, LauncherProcessSelectionTestData.UtcAtEightPm(12), false, false)
            ],
            existingProcessIds: LauncherProcessSelectionTestData.NoExistingProcessIds,
            timedOut: true,
            timeoutMilliseconds: LauncherProcessSelectionTestData.StartupTimeoutMilliseconds);

        Assert.Equal(LauncherStartupState.Failed, evaluation.State);
        Assert.Contains("PID 102", evaluation.Error);
    }

    [Fact]
    public void EvaluateStartupProgress_SucceedsWhenPrimaryProcessShowsMainWindow()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 111,
            primaryHasExited: false,
            primaryExitCode: null,
            snapshots:
            [
                new LauncherProcessSnapshot(111, LauncherProcessSelectionTestData.UtcAtEightPm(13), false, true)
            ],
            existingProcessIds: LauncherProcessSelectionTestData.NoExistingProcessIds,
            timedOut: false,
            timeoutMilliseconds: LauncherProcessSelectionTestData.StartupTimeoutMilliseconds);

        Assert.Equal(LauncherStartupState.Succeeded, evaluation.State);
        Assert.Equal(111, evaluation.ActiveProcessId);
    }

    [Fact]
    public void EvaluateStartupProgress_TimesOutAgainstPrimaryProcessWhenNoReplacementExists()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 121,
            primaryHasExited: false,
            primaryExitCode: null,
            snapshots: [],
            existingProcessIds: LauncherProcessSelectionTestData.NoExistingProcessIds,
            timedOut: true,
            timeoutMilliseconds: LauncherProcessSelectionTestData.StartupTimeoutMilliseconds);

        Assert.Equal(LauncherStartupState.Failed, evaluation.State);
        Assert.Contains("PID 121", evaluation.Error);
    }
}
