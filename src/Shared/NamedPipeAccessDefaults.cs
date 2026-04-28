using System.IO.Pipes;
#if NET8_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace VsIdeBridge.Shared;

#if NET8_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
public static class NamedPipeAccessDefaults
{
    // Windows named-pipe clients need Synchronize in addition to read/write rights.
    public const PipeAccessRights ClientReadWriteRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <summary>
    /// Name of the cross-process control pipe owned by VsIdeBridgeService.
    /// Clients write newline-terminated event names ("HTTP_ENABLE", "HTTP_DISABLE",
    /// "MCP_REQUEST", "COMMAND_START", etc.) to it; the service handles them in
    /// its <c>HandleEvent</c> dispatch.
    /// </summary>
    public const string ServiceControlPipeName = "VsIdeBridgeServiceControl";
}