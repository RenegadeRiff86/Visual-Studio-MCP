using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class McpHttpCompatibilityTests
{
    [Fact]
    public void ProtocolNegotiationAccepts20241105()
    {
        Assert.Contains("2024-11-05", McpProtocol.SupportedProtocolVersions);
        Assert.Equal("2024-11-05", McpProtocol.SelectProtocolVersion("2024-11-05"));
    }

    [Fact]
    public void LegacySseRoutesAreRecognized()
    {
        Assert.True(LegacySseHttpTransport.IsSsePath("/sse"));
        Assert.True(LegacySseHttpTransport.IsSsePath("/sse/"));
        Assert.True(LegacySseHttpTransport.IsMessagesPath("/messages"));
          Assert.True(LegacySseHttpTransport.IsMessagesPath("/messages/"));
        Assert.False(LegacySseHttpTransport.IsSsePath("/mcp"));
        Assert.False(LegacySseHttpTransport.IsMessagesPath("/mcp"));
    }
}
