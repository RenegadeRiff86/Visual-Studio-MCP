using System;
using System.Collections.Generic;
using VsIdeBridgeLauncher;
using Xunit;

namespace VsIdeBridgeLauncher.Tests;

internal static class LauncherProcessSelectionTestData
{
    internal static readonly HashSet<int> NoExistingProcessIds = [];
    private static readonly DateTime TestDayUtc = new(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc);
    internal const int DuplicateCandidateMinute = 2;
    internal const int ExpectedNormalizedPathCount = 2;
    internal const int StartupTimeoutMilliseconds = 30_000;

    internal static DateTime UtcAt(int hour, int minute)
    {
        return TestDayUtc.AddHours(hour).AddMinutes(minute);
    }

    internal static DateTime UtcAtEightPm(int minute)
    {
        return UtcAt(20, minute);
    }
}

public class LauncherProcessSelectionCandidateTests
{

    [Fact]
    public void SelectNewestLaunchedProcessId_IgnoresExistingProcesses()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(11, LauncherProcessSelectionTestData.UtcAt(18, 0), false, false),
            new LauncherProcessSnapshot(12, LauncherProcessSelectionTestData.UtcAt(18, 1), false, false),
            new LauncherProcessSnapshot(13, LauncherProcessSelectionTestData.UtcAt(18, LauncherProcessSelectionTestData.DuplicateCandidateMinute), false, false)
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
            new LauncherProcessSnapshot(21, LauncherProcessSelectionTestData.UtcAt(19, 0), false, false),
            new LauncherProcessSnapshot(22, LauncherProcessSelectionTestData.UtcAt(19, 5), false, false),
            new LauncherProcessSnapshot(23, LauncherProcessSelectionTestData.UtcAt(19, LauncherProcessSelectionTestData.DuplicateCandidateMinute), false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, LauncherProcessSelectionTestData.NoExistingProcessIds);

        Assert.Equal(22, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_ReturnsNullWhenNothingIsNew()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(31, LauncherProcessSelectionTestData.UtcAtEightPm(0), false, false),
            new LauncherProcessSnapshot(32, LauncherProcessSelectionTestData.UtcAtEightPm(1), false, false)
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
            new LauncherProcessSnapshot(41, LauncherProcessSelectionTestData.UtcAtEightPm(5), true, false),
            new LauncherProcessSnapshot(42, LauncherProcessSelectionTestData.UtcAtEightPm(6), false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, LauncherProcessSelectionTestData.NoExistingProcessIds);

        Assert.Equal(41, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_PrefersVisibleWindowWhenNoDiscoveryExists()
    {
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(51, LauncherProcessSelectionTestData.UtcAtEightPm(7), false, true),
            new LauncherProcessSnapshot(52, LauncherProcessSelectionTestData.UtcAtEightPm(8), false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, LauncherProcessSelectionTestData.NoExistingProcessIds);

        Assert.Equal(51, processId);
    }

    [Fact]
    public void SelectNewestLaunchedProcessId_UsesPidAsTiebreakerForEquivalentCandidates()
    {
        DateTime startTime = LauncherProcessSelectionTestData.UtcAtEightPm(9);
        List<LauncherProcessSnapshot> snapshots =
        [
            new LauncherProcessSnapshot(61, startTime, false, false),
            new LauncherProcessSnapshot(62, startTime, false, false)
        ];

        int? processId = LauncherProcessSelection.SelectNewestLaunchedProcessId(snapshots, LauncherProcessSelectionTestData.NoExistingProcessIds);

        Assert.Equal(62, processId);
    }

}
