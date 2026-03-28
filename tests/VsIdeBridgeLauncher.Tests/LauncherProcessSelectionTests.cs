using System;
using System.Collections.Generic;
using VsIdeBridgeLauncher;
using Xunit;

namespace VsIdeBridgeLauncher.Tests;

public class LauncherProcessSelectionTests
{
    [Fact]
    public void SelectNewestLaunchedProcessId_IgnoresExistingProcesses()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(11, new DateTime(2026, 3, 27, 18, 0, 0, DateTimeKind.Utc), false, false),
            new LauncherProcessSnapshot(12, new DateTime(2026, 3, 27, 18, 1, 0, DateTimeKind.Utc), false, false),
            new LauncherProcessSnapshot(13, new DateTime(2026, 3, 27, 18, 2, 0, DateTimeKind.Utc), false, false)
        ];

        HashSet<int> existingProcessIds = [13];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, existingProcessIds);

        Assert.Equal(12, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_ReturnsNewestNewProcess()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(21, new DateTime(2026, 3, 27, 19, 0, 0, DateTimeKind.Utc), false, false),
            new LauncherProcessSnapshot(22, new DateTime(2026, 3, 27, 19, 5, 0, DateTimeKind.Utc), false, false),
            new LauncherProcessSnapshot(23, new DateTime(2026, 3, 27, 19, 2, 0, DateTimeKind.Utc), false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, new HashSet<int>());

        Assert.Equal(22, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_ReturnsNullWhenNothingIsNew()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(31, new DateTime(2026, 3, 27, 20, 0, 0, DateTimeKind.Utc), false, false),
            new LauncherProcessSnapshot(32, new DateTime(2026, 3, 27, 20, 1, 0, DateTimeKind.Utc), false, false)
        ];

        HashSet<int> existingProcessIds = [31, 32];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, existingProcessIds);

        Assert.Null(processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_PrefersBridgeDiscoveryOverNewerHeadlessProcess()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(41, new DateTime(2026, 3, 27, 20, 5, 0, DateTimeKind.Utc), true, false),
            new LauncherProcessSnapshot(42, new DateTime(2026, 3, 27, 20, 6, 0, DateTimeKind.Utc), false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, new HashSet<int>());

        Assert.Equal(41, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_PrefersVisibleWindowWhenNoDiscoveryExists()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(51, new DateTime(2026, 3, 27, 20, 7, 0, DateTimeKind.Utc), false, true),
            new LauncherProcessSnapshot(52, new DateTime(2026, 3, 27, 20, 8, 0, DateTimeKind.Utc), false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, new HashSet<int>());

        Assert.Equal(51, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_UsesPidAsTiebreakerForEquivalentCandidates()
    {
        DateTime startTime = new DateTime(2026, 3, 27, 20, 9, 0, DateTimeKind.Utc);
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(61, startTime, false, false),
            new LauncherProcessSnapshot(62, startTime, false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, new HashSet<int>());

        Assert.Equal(62, processId);
    }

    [Fact]
    public void EvaluateStartupProgress_FailsWhenPrimaryProcessExitsAndNoReplacementExists()
    {
        LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
            primaryProcessId: 71,
            primaryHasExited: true,
            primaryExitCode: 99,
            snapshots: [],
            existingProcessIds: new HashSet<int>(),
            timedOut: false,
            timeoutMilliseconds: 30_000);

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
                new LauncherProcessSnapshot(82, new DateTime(2026, 3, 27, 20, 10, 0, DateTimeKind.Utc), false, false)
            ],
            existingProcessIds: new HashSet<int>(),
            timedOut: false,
            timeoutMilliseconds: 30_000);

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
                new LauncherProcessSnapshot(92, new DateTime(2026, 3, 27, 20, 11, 0, DateTimeKind.Utc), true, false)
            ],
            existingProcessIds: new HashSet<int>(),
            timedOut: false,
            timeoutMilliseconds: 30_000);

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
                new LauncherProcessSnapshot(102, new DateTime(2026, 3, 27, 20, 12, 0, DateTimeKind.Utc), false, false)
            ],
            existingProcessIds: new HashSet<int>(),
            timedOut: true,
            timeoutMilliseconds: 30_000);

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
                new LauncherProcessSnapshot(111, new DateTime(2026, 3, 27, 20, 13, 0, DateTimeKind.Utc), false, true)
            ],
            existingProcessIds: new HashSet<int>(),
            timedOut: false,
            timeoutMilliseconds: 30_000);

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
            existingProcessIds: new HashSet<int>(),
            timedOut: true,
            timeoutMilliseconds: 30_000);

        Assert.Equal(LauncherStartupState.Failed, evaluation.State);
        Assert.Contains("PID 121", evaluation.Error);
    }

    [Fact]
    public void NormalizeTempRoots_RemovesEmptyAndDuplicateEntries()
    {
        IReadOnlyList<string> roots = LauncherProcessSelection.NormalizeTempRoots(
        new object?[]
        {
            null,
            "",
            "C:\\Temp\\",
            "C:\\Temp",
            "D:\\Scratch\\",
            "D:\\Scratch"
        }.Cast<string>());

        Assert.Equal(2, roots.Count);
        Assert.Equal("C:\\Temp", roots[0]);
        Assert.Equal("D:\\Scratch", roots[1]);
    }

    [Fact]
    public void BuildDiscoveryFileCandidates_UsesNormalizedRoots()
    {
        IReadOnlyList<string> paths = LauncherProcessSelection.BuildDiscoveryFileCandidates(
        [
            "C:\\Temp\\",
            "D:\\Scratch"
        ],
            processId: 4321);

        Assert.Equal(2, paths.Count);
        Assert.Equal("C:\\Temp\\vs-ide-bridge\\pipes\\bridge-4321.json", paths[0]);
        Assert.Equal("D:\\Scratch\\vs-ide-bridge\\pipes\\bridge-4321.json", paths[1]);
    }
}
