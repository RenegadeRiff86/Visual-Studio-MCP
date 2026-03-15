using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class VsOpenInstanceSelectorTests
{
    private const string ExistingInstanceId = "existing";
    private const string OtherInstanceId = "other";
    private const string NewSolutionInstanceId = "new-solution";
    private const string LaunchedInstanceId = "launched";
    private const string RequestedSolutionPath = @"C:\repo\Requested.sln";
    private const int ExistingProcessId = 101;
    private const int OtherProcessId = 202;
    private const int RequestedProcessId = 303;
    private const int LaunchedProcessId = 404;
    private const int LauncherStubProcessId = 999;

    [Fact]
    public void SelectInstance_PrefersNewInstanceMatchingRequestedSolution()
    {
        var now = DateTime.UtcNow;
        var existingInstances = CreateInstances(
            CreateDiscovery(ExistingInstanceId, ExistingProcessId, @"C:\repo\Old.sln", now.AddMinutes(-2)),
            CreateDiscovery(OtherInstanceId, OtherProcessId, @"C:\repo\Other.sln", now.AddMinutes(-1)));

        var currentInstances = CreateInstances(
            existingInstances[0],
            existingInstances[1],
            CreateDiscovery(NewSolutionInstanceId, RequestedProcessId, RequestedSolutionPath, now));

        var selected = VsOpenInstanceSelector.SelectInstance(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExistingInstanceId, OtherInstanceId },
            new HashSet<int> { ExistingProcessId, OtherProcessId },
            currentInstances,
            launchedProcessId: LauncherStubProcessId,
            requestedSolutionPath: RequestedSolutionPath);

        Assert.NotNull(selected);
        Assert.Equal(NewSolutionInstanceId, selected!.InstanceId);
        Assert.Equal(RequestedProcessId, selected.ProcessId);
    }

    [Fact]
    public void SelectInstance_FallsBackToLaunchedPidWhenSolutionIsUnknown()
    {
        var now = DateTime.UtcNow;
        var currentInstances = CreateInstances(
            CreateDiscovery(ExistingInstanceId, ExistingProcessId, @"C:\repo\Old.sln", now.AddMinutes(-1)),
            CreateDiscovery(LaunchedInstanceId, LaunchedProcessId, @"C:\repo\Fresh.sln", now));

        var selected = VsOpenInstanceSelector.SelectInstance(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExistingInstanceId },
            new HashSet<int> { ExistingProcessId },
            currentInstances,
            launchedProcessId: LaunchedProcessId,
            requestedSolutionPath: null);

        Assert.NotNull(selected);
        Assert.Equal(LaunchedInstanceId, selected!.InstanceId);
        Assert.Equal(LaunchedProcessId, selected.ProcessId);
    }

    [Fact]
    public void SelectInstance_FallsBackToExistingMatchingSolutionWhenNewInstanceIsNotYetDiscovered()
    {
        var now = DateTime.UtcNow;
        var existing = CreateDiscovery(ExistingInstanceId, ExistingProcessId, RequestedSolutionPath, now);

        var selected = VsOpenInstanceSelector.SelectInstance(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExistingInstanceId },
            new HashSet<int> { ExistingProcessId },
            CreateInstances(existing),
            launchedProcessId: LauncherStubProcessId,
            requestedSolutionPath: RequestedSolutionPath);

        Assert.NotNull(selected);
        Assert.Equal(ExistingInstanceId, selected!.InstanceId);
    }

    [Fact]
    public void CreateSelector_UsesSolutionArgumentAsSolutionHint()
    {
        var bridgeBindingType = typeof(CliApp)
            .GetNestedType("McpServer", BindingFlags.NonPublic)
            ?.GetNestedType("BridgeBinding", BindingFlags.NonPublic);

        Assert.NotNull(bridgeBindingType);

        var createSelector = bridgeBindingType!.GetMethod("CreateSelector", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createSelector);

        var args = new JsonObject
        {
            ["solution"] = RequestedSolutionPath,
        };

        var selector = createSelector!.Invoke(null, [args]);
        Assert.NotNull(selector);

        var solutionHint = selector!.GetType().GetProperty("SolutionHint")?.GetValue(selector) as string;
        Assert.Equal(RequestedSolutionPath, solutionHint);
    }

    private static List<PipeDiscovery> CreateInstances(params PipeDiscovery[] items)
    {
        return [.. items];
    }

    private static PipeDiscovery CreateDiscovery(string instanceId, int processId, string solutionPath, DateTime lastWriteTimeUtc)
    {
        return new PipeDiscovery
        {
            InstanceId = instanceId,
            PipeName = $"Pipe-{processId}",
            ProcessId = processId,
            SolutionPath = solutionPath,
            SolutionName = System.IO.Path.GetFileName(solutionPath),
            StartedAtUtc = lastWriteTimeUtc.ToString("O"),
            DiscoveryFile = $"discovery-{processId}.json",
            LastWriteTimeUtc = lastWriteTimeUtc,
            Source = "test",
        };
    }
}
