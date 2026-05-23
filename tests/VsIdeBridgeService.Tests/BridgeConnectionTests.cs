using System;
using System.Reflection;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class BridgeConnectionTests
{
    [Fact]
    public void DropCachedInstanceIfStaleClearsMissingCachedBinding()
    {
        BridgeConnection bridge = new([]);
        BridgeInstance stale = CreateInstance("vs18-1", "pipe-old", 1, "C:\\Repos\\Old\\Old.sln");
        SetConnectionState(bridge, "Cached", stale);
        SetConnectionState(bridge, "LastSolutionPath", stale.SolutionPath);

        BridgeInstance? dropped = bridge.DropCachedInstanceIfStale([]);

        Assert.Same(stale, dropped);
        Assert.Null(bridge.CurrentInstance);
        Assert.Equal(string.Empty, bridge.CurrentSolutionPath);
    }

    [Fact]
    public void DropCachedInstanceIfStaleRefreshesVisibleCachedMetadata()
    {
        BridgeConnection bridge = new([]);
        BridgeInstance cached = CreateInstance("vs18-1", "pipe-live", 1, "C:\\Repos\\Old\\Old.sln");
        BridgeInstance visible = cached with
        {
            SolutionPath = "C:\\Repos\\New\\New.sln",
            SolutionName = "New.sln",
        };
        SetConnectionState(bridge, "Cached", cached);
        SetConnectionState(bridge, "LastSolutionPath", cached.SolutionPath);

        BridgeInstance? dropped = bridge.DropCachedInstanceIfStale([visible]);

        Assert.Null(dropped);
        Assert.Equal(visible, bridge.CurrentInstance);
        Assert.Equal(visible.SolutionPath, bridge.CurrentSolutionPath);
    }

    private static BridgeInstance CreateInstance(string instanceId, string pipeName, int processId, string solutionPath)
        => new()
        {
            InstanceId = instanceId,
            PipeName = pipeName,
            ProcessId = processId,
            SolutionPath = solutionPath,
            SolutionName = System.IO.Path.GetFileName(solutionPath),
            Label = System.IO.Path.GetFileNameWithoutExtension(solutionPath),
            Source = "test",
            DiscoveryFile = "test.json",
            LastWriteTimeUtc = DateTime.UnixEpoch,
        };

    private static void SetConnectionState<T>(BridgeConnection bridge, string propertyName, T value)
    {
        object state = typeof(BridgeConnection)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bridge)!;
        state.GetType().GetProperty(propertyName)!.SetValue(state, value);
    }
}
