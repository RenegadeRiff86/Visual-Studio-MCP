using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

// MCP HTTP transports. The /mcp endpoint implements Streamable HTTP (2025-03-26),
// while /sse and /messages provide the HTTP+SSE transport used by 2024-11-05 clients.
//
// POST /mcp      - client sends JSON-RPC requests and notifications;
//                  server responds with application/json or text/event-stream.
// GET  /mcp      - client opens a persistent SSE stream for server-initiated
//                  messages (none from this bridge; kept alive with comments).
// DELETE /mcp    - client terminates a session.
// GET  /sse      - client opens a 2024-11-05 SSE stream and receives an endpoint event.
// POST /messages - client posts 2024-11-05 JSON-RPC messages; responses return over SSE.
// GET  /         - health check (returns JSON status object).
internal static class StreamableHttpMode
{
    private const string McpPath = "/mcp";
    private const string ContentTypeJson = "application/json; charset=utf-8";
    private const string ContentTypeSse = "text/event-stream";
    private const string SessionIdHeader = "MCP-Session-Id";
    private const int StatusOk = 200;
    private const int StatusAccepted = 202;
    private const int StatusBadRequest = 400;

    private static readonly ConcurrentDictionary<string, StreamableSession> Sessions = new();

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        int port = GetIntArg(args, "--port", HttpServerDefaults.StreamableHttpPort);
        BridgeConnection bridge = new(args);
        McpToolSurface toolSurface = McpToolSurface.FromArgs(args);

        McpServerLog.Write($"streamable-http MCP server starting on port {port}");

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            listener.Start();
            McpServerLog.Write("streamable-http listener started");
        }
        catch (HttpListenerException ex)
        {
            McpServerLog.Write($"streamable-http failed to start: {ex}");
            throw;
        }

        using CancellationTokenRegistration stopReg = cancellationToken.Register(
            static state => ((HttpListener)state!).Stop(), listener);

        while (true)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (!listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                McpServerLog.Write($"streamable-http accept error: {ex.Message}");
                continue;
            }
            catch (ObjectDisposedException ex)
            {
                McpServerLog.Write($"streamable-http accept error: {ex.Message}");
                continue;
            }

            // Each request runs on its own task; GET /mcp may be long-lived.
            HttpListenerContext capturedCtx = ctx;
            _ = Task.Run(() => HandleContextAsync(capturedCtx, bridge, toolSurface), cancellationToken);
        }

        McpServerLog.Write("streamable-http listener stopped");
    }

    private static async Task HandleContextAsync(HttpListenerContext ctx, BridgeConnection bridge, McpToolSurface toolSurface)
    {
        try
        {
            AddCorsHeaders(ctx);

            string method = ctx.Request.HttpMethod;
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = StatusOk;
                return;
            }

            bool isHealthPath = path == "/";
            bool isMcpPath = IsPath(path, McpPath);
            bool isLegacySsePath = LegacySseHttpTransport.IsSsePath(path);
            bool isLegacyMessagesPath = LegacySseHttpTransport.IsMessagesPath(path);

            if (!isHealthPath && !isMcpPath && !isLegacySsePath && !isLegacyMessagesPath)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            if (method == "GET" && isHealthPath)
            {
                await WriteHealthAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (isLegacySsePath)
            {
                if (method == "GET")
                    await LegacySseHttpTransport.HandleSseAsync(ctx).ConfigureAwait(false);
                else
                    SetMethodNotAllowed(ctx, "GET, OPTIONS");
                return;
            }

            if (isLegacyMessagesPath)
            {
                if (method == "POST")
                    await LegacySseHttpTransport.HandleMessagePostAsync(ctx, bridge, toolSurface).ConfigureAwait(false);
                else
                    SetMethodNotAllowed(ctx, "POST, OPTIONS");
                return;
            }

            switch (method)
            {
                case "POST":
                    await HandlePostAsync(ctx, bridge, toolSurface).ConfigureAwait(false);
                    break;
                case "GET":
                    await HandleGetAsync(ctx).ConfigureAwait(false);
                    break;
                case "DELETE":
                    HandleDelete(ctx);
                    break;
                default:
                    SetMethodNotAllowed(ctx, "GET, POST, DELETE, OPTIONS");
                    break;
            }
        }
        catch (HttpListenerException ex)
        {
            McpServerLog.Write($"streamable-http handler error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not null)
        {
            McpServerLog.Write($"streamable-http handler fatal: {ex.Message}");
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); }
            catch (ObjectDisposedException ex) { McpServerLog.Write($"streamable-http response close: {ex.Message}"); }
            catch (HttpListenerException ex) { McpServerLog.Write($"streamable-http response close: {ex.Message}"); }
        }
    }

    private static async Task HandlePostAsync(HttpListenerContext ctx, BridgeConnection bridge, McpToolSurface toolSurface)
    {
        JsonObject? request = await ReadJsonRequestAsync(ctx).ConfigureAwait(false);
        if (request is null)
            return;

        string requestMethod = request["method"]?.GetValue<string>() ?? string.Empty;
        bool isInitialize = string.Equals(requestMethod, "initialize", StringComparison.Ordinal);

        // Validate session ID on every request except initialize.
        string? sessionId = ctx.Request.Headers[SessionIdHeader];
        if (!isInitialize && (string.IsNullOrEmpty(sessionId) || !Sessions.ContainsKey(sessionId)))
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return;
        }

        // Notifications get 202; no response body needed.
        if (requestMethod.StartsWith("notifications/", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusAccepted;
            return;
        }

        McpServerLog.WriteRequest(request, McpProtocol.WireFormat.RawJson);

        JsonObject? response = await McpServerMode.HandleRequestAsync(request, bridge, controlClient: null, toolSurface)
            .ConfigureAwait(false);

        if (response is null)
        {
            ctx.Response.StatusCode = StatusAccepted;
            return;
        }

        // On successful initialize: create a session and attach its ID to the response header.
        if (isInitialize)
        {
            string newId = GenerateSessionId();
            Sessions[newId] = new StreamableSession(newId);
            ctx.Response.Headers[SessionIdHeader] = newId;
            McpServerLog.Write($"streamable-http session created id={newId}");
        }

        McpServerLog.WriteResponse(response);

        // Respond with SSE if the client accepts it, otherwise plain JSON.
        string? accept = ctx.Request.Headers["Accept"];
        bool useSse = accept != null
            && accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (useSse)
        {
            await WriteSseEventAsync(ctx, response).ConfigureAwait(false);
        }
        else
        {
            await WriteJsonAsync(ctx, response).ConfigureAwait(false);
        }
    }

    private static async Task HandleGetAsync(HttpListenerContext ctx)
    {
        // Validate the session.
        string? sessionId = ctx.Request.Headers[SessionIdHeader];
        if (string.IsNullOrEmpty(sessionId) || !Sessions.ContainsKey(sessionId))
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return;
        }

        ctx.Response.StatusCode = StatusOk;
        ctx.Response.ContentType = ContentTypeSse;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        McpServerLog.Write($"streamable-http SSE stream opened session={sessionId}");

        try
        {
            // Priming comment so the client knows the stream is live.
            await WriteRawSseAsync(ctx, ": stream open\n\n").ConfigureAwait(false);

            // Keep alive with periodic comment events; this bridge never sends
            // server-initiated messages so there is nothing else to write.
            while (true)
            {
                await Task.Delay(15_000).ConfigureAwait(false);
                await WriteRawSseAsync(ctx, ": keep-alive\n\n").ConfigureAwait(false);
            }
        }
        catch (HttpListenerException ex)
        {
            McpServerLog.Write($"streamable-http SSE stream disconnect session={sessionId}: {ex.Message}");
        }
        catch (IOException ex)
        {
            McpServerLog.Write($"streamable-http SSE stream disconnect session={sessionId}: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            McpServerLog.Write($"streamable-http SSE stream stopped session={sessionId}");
        }

        McpServerLog.Write($"streamable-http SSE stream closed session={sessionId}");
    }

    private static void HandleDelete(HttpListenerContext ctx)
    {
        string? sessionId = ctx.Request.Headers[SessionIdHeader];
        if (!string.IsNullOrEmpty(sessionId) && Sessions.TryRemove(sessionId, out _))
        {
            McpServerLog.Write($"streamable-http session deleted id={sessionId}");
        }

        ctx.Response.StatusCode = StatusAccepted;
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static async Task WriteSseEventAsync(HttpListenerContext ctx, JsonObject payload)
    {
        string json = payload.ToJsonString();
        byte[] bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        ctx.Response.StatusCode = StatusOk;
        ctx.Response.ContentType = ContentTypeSse;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteRawSseAsync(HttpListenerContext ctx, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, JsonObject payload)
    {
        string json = payload.ToJsonString();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = StatusOk;
        ctx.Response.ContentType = ContentTypeJson;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteHealthAsync(HttpListenerContext ctx)
    {
        JsonObject info = new()
        {
            ["name"] = "vs-ide-bridge",
            ["version"] = "0.1.0",
            ["transport"] = "streamable-http",
            ["protocolVersions"] = CreateSupportedProtocolVersionsArray(),
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["status"] = "ok",
        };
        await WriteJsonAsync(ctx, info).ConfigureAwait(false);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static async Task<JsonObject?> ReadJsonRequestAsync(HttpListenerContext ctx)
    {
        string body;
        try
        {
            using StreamReader reader = new(ctx.Request.InputStream,
                ctx.Request.ContentEncoding ?? Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            McpServerLog.Write($"streamable-http body read error: {ex.Message}");
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.Write($"streamable-http body read error: {ex.Message}");
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }

        JsonObject? request;
        try
        {
            request = JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }

        if (request is null)
            ctx.Response.StatusCode = StatusBadRequest;

        return request;
    }

    private static bool IsPath(string path, string expected)
        => string.Equals(path, expected, StringComparison.Ordinal)
            || string.Equals(path, expected + "/", StringComparison.Ordinal);

    private static void SetMethodNotAllowed(HttpListenerContext ctx, string allow)
    {
        ctx.Response.StatusCode = 405;
        ctx.Response.Headers["Allow"] = allow;
    }

    private static JsonArray CreateSupportedProtocolVersionsArray()
    {
        JsonArray versions = [];
        foreach (string version in McpProtocol.SupportedProtocolVersions)
            versions.Add(version);
        return versions;
    }

    private static void AddCorsHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] =
            $"Content-Type, Accept, {SessionIdHeader}, MCP-Protocol-Version";
        ctx.Response.Headers["Access-Control-Expose-Headers"] = SessionIdHeader;
    }

    private static string GenerateSessionId()
        => Guid.NewGuid().ToString("N"); // 32-char lowercase hex, URL-safe

    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out int value)
                && value > 0 && value < 65536)
            {
                return value;
            }
        }

        return defaultValue;
    }

    // ── Session state ─────────────────────────────────────────────────────────

    private sealed class StreamableSession(string id)
    {
        public string Id { get; } = id;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }
}

internal static class LegacySseHttpTransport
{
    private const string SsePath = "/sse";
    private const string MessagesPath = "/messages";
    private const string SessionIdQueryParameter = "sessionId";
    private const string ContentTypeSse = "text/event-stream";
    private const int StatusOk = 200;
    private const int StatusAccepted = 202;
    private const int StatusBadRequest = 400;

    private static readonly ConcurrentDictionary<string, LegacySseSession> Sessions = new();

    public static bool IsSsePath(string path)
        => IsPath(path, SsePath);

    public static bool IsMessagesPath(string path)
        => IsPath(path, MessagesPath);

    public static async Task HandleSseAsync(HttpListenerContext ctx)
    {
        string newId = GenerateSessionId();
        LegacySseSession session = new(newId);
        Sessions[newId] = session;

        ctx.Response.StatusCode = StatusOk;
        ctx.Response.ContentType = ContentTypeSse;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        McpServerLog.Write($"streamable-http legacy SSE stream opened session={newId}");

        try
        {
            await WriteEndpointEventAsync(ctx, newId).ConfigureAwait(false);

            while (await session.Messages.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (session.Messages.Reader.TryRead(out JsonObject? payload))
                    await WriteMessageEventAsync(ctx, payload).ConfigureAwait(false);
            }
        }
        catch (HttpListenerException ex)
        {
            McpServerLog.Write($"streamable-http legacy SSE disconnect session={newId}: {ex.Message}");
        }
        catch (IOException ex)
        {
            McpServerLog.Write($"streamable-http legacy SSE disconnect session={newId}: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            McpServerLog.Write($"streamable-http legacy SSE stopped session={newId}");
        }
        finally
        {
            Sessions.TryRemove(newId, out _);
            session.Messages.Writer.TryComplete();
        }

        McpServerLog.Write($"streamable-http legacy SSE stream closed session={newId}");
    }

    public static async Task HandleMessagePostAsync(HttpListenerContext ctx, BridgeConnection bridge, McpToolSurface toolSurface)
    {
        string? sessionId = ctx.Request.QueryString[SessionIdQueryParameter];
        if (string.IsNullOrEmpty(sessionId) || !Sessions.TryGetValue(sessionId, out LegacySseSession? session))
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return;
        }

        JsonObject? request = await ReadJsonRequestAsync(ctx).ConfigureAwait(false);
        if (request is null)
            return;

        string requestMethod = request["method"]?.GetValue<string>() ?? string.Empty;
        if (requestMethod.StartsWith("notifications/", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusAccepted;
            return;
        }

        McpServerLog.WriteRequest(request, McpProtocol.WireFormat.RawJson);

        JsonObject? response = await McpServerMode.HandleRequestAsync(request, bridge, controlClient: null, toolSurface)
            .ConfigureAwait(false);

        if (response is not null)
        {
            McpServerLog.WriteResponse(response);
            if (!session.Messages.Writer.TryWrite(response))
            {
                ctx.Response.StatusCode = StatusBadRequest;
                return;
            }
        }

        ctx.Response.StatusCode = StatusAccepted;
    }

    private static async Task<JsonObject?> ReadJsonRequestAsync(HttpListenerContext ctx)
    {
        string body;
        try
        {
            using StreamReader reader = new(ctx.Request.InputStream,
                ctx.Request.ContentEncoding ?? Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            McpServerLog.Write($"streamable-http legacy body read error: {ex.Message}");
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.Write($"streamable-http legacy body read error: {ex.Message}");
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }

        JsonObject? request;
        try
        {
            request = JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            ctx.Response.StatusCode = StatusBadRequest;
            return null;
        }

        if (request is null)
            ctx.Response.StatusCode = StatusBadRequest;

        return request;
    }

    private static Task WriteEndpointEventAsync(HttpListenerContext ctx, string sessionId)
        => WriteRawSseAsync(ctx, $"event: endpoint\ndata: {BuildMessageEndpoint(sessionId)}\n\n");

    private static Task WriteMessageEventAsync(HttpListenerContext ctx, JsonObject payload)
        => WriteRawSseAsync(ctx, $"event: message\ndata: {payload.ToJsonString()}\n\n");

    private static string BuildMessageEndpoint(string sessionId)
        => $"{MessagesPath}?{SessionIdQueryParameter}={Uri.EscapeDataString(sessionId)}";

    private static async Task WriteRawSseAsync(HttpListenerContext ctx, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
    }

    private static bool IsPath(string path, string expected)
        => string.Equals(path, expected, StringComparison.Ordinal)
            || string.Equals(path, expected + "/", StringComparison.Ordinal);

    private static string GenerateSessionId()
        => Guid.NewGuid().ToString("N");

    private sealed class LegacySseSession(string id)
    {
        public string Id { get; } = id;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public Channel<JsonObject> Messages { get; } = Channel.CreateUnbounded<JsonObject>();
    }
}
