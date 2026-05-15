using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class StdioHostLeaseTests
{
    [Fact]
    public void LeaseFileNamesAreScopedToHostProcess()
    {
        string firstHost = StdioHostLease.GetLeaseFileName(parentPid: 100, currentPid: 200);
        string secondHost = StdioHostLease.GetLeaseFileName(parentPid: 100, currentPid: 201);

        Assert.Equal("mcp-parent-100-host-200.lease", firstHost);
        Assert.Equal("mcp-parent-100-host-201.lease", secondHost);
        Assert.NotEqual(firstHost, secondHost);
    }
}
