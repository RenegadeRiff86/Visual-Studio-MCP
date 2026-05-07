using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Associates MCP request IDs with CancellationTokenSources so that
// notifications/cancelled messages can abort in-flight tool handlers.
internal static class RequestCancellationRegistry
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    /// <summary>Register a CTS for this request. The returned CTS is owned by the
    /// caller and must be disposed when the request completes.</summary>
    public static CancellationTokenSource Register(JsonNode? id)
    {
        CancellationTokenSource cts = new();
        if (id is not null)
            _inFlight[id.ToJsonString()] = cts;
        return cts;
    }

    /// <summary>Cancel the in-flight request with this id, if any.</summary>
    public static void Cancel(JsonNode? id)
    {
        if (id is not null && _inFlight.TryGetValue(id.ToJsonString(), out CancellationTokenSource? cts))
            cts.Cancel();
    }

    /// <summary>Return the cancellation token for this request, or <see cref="CancellationToken.None"/>.</summary>
    public static CancellationToken GetToken(JsonNode? id)
    {
        if (id is not null && _inFlight.TryGetValue(id.ToJsonString(), out CancellationTokenSource? cts))
            return cts.Token;
        return CancellationToken.None;
    }

    /// <summary>Remove and dispose the CTS after the request completes.</summary>
    public static void Unregister(JsonNode? id)
    {
        if (id is not null && _inFlight.TryRemove(id.ToJsonString(), out CancellationTokenSource? cts))
            cts.Dispose();
    }
}
