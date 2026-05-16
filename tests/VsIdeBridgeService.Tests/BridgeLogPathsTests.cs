using System;
using System.IO;
using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class BridgeLogPathsTests
{
    [Fact]
    public void SharedLogPathsUseStableProductLogFolder()
    {
        string directory = BridgeLogPaths.GetSharedLogDirectory();

        Assert.EndsWith(Path.Combine("VsIdeBridge", "logs"), directory);
        Assert.Equal(Path.Combine(directory, "mcp-server.log"), BridgeLogPaths.GetMcpServerLogPath());
        Assert.Equal(
            Path.Combine(directory, "vs-ide-bridge-2026-05-07.log"),
            BridgeLogPaths.GetVisualStudioExtensionLogPath(new DateTime(2026, 5, 7)));
    }
}
