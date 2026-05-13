using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text;
using System.IO;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

// Runs the MCP server JSON-RPC stdio loop.
// Launched when the service binary is invoked with the "mcp-server" argument.
internal static class McpServerMode
{
    private static readonly ToolExecutionRegistry Registry = ToolCatalog.Registry;
    private static readonly TimeSpan StdoutWriteTimeout = TimeSpan.FromSeconds(30);
    private const int BadRequestStatusCode = 400;
    private const int StdoutWriteTimeoutExitCode = 2;

    public static async Task RunAsync(string[] args)
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        BridgeConnection bridge = new(args);
        McpToolSurface toolSurface = McpToolSurface.FromArgs(args);
        using StdioHostLease? hostLease = StdioHostLease.TryCreate();
        ServiceControlClient? controlClient = null;
        SemaphoreSlim writeLock = new(1, 1);

        controlClient = await TryConnectControlPipeAsync().ConfigureAwait(false);

        McpServerLog.Write("stdio loop started");

        try
        {
            while (true)
            {
                McpProtocol.IncomingMessage? incoming;
                try
                {
                    incoming = await McpProtocol.ReadAsync(input).ConfigureAwait(false);
                }
                catch (McpRequestException ex)
                {
                    McpServerLog.Write($"read error code={ex.Code} message={ex.Message}");
                    await WriteLockedAsync(output, McpProtocol.ErrorResponse(ex.Id, ex.Code, ex.Message),
                        McpProtocol.WireFormat.HeaderFramed, writeLock).ConfigureAwait(false);
                    continue;
                }

                if (incoming is null)
                {
                    McpServerLog.Write("stdin closed; exiting stdio loop");
                    break;
                }

                StdioHostLease.MarkActivity();
                string incomingMethod = incoming.Request["method"]?.GetValue<string>() ?? string.Empty;

                // Handle notifications immediately without blocking the read loop.
                if (incomingMethod.StartsWith("notifications/", StringComparison.Ordinal))
                {
                    if (incomingMethod == "notifications/cancelled")
                    {
                        // Cancel the matching in-flight request's CTS so its polling loop exits.
                        JsonNode? cancelId = incoming.Request["params"]?["requestId"]?.DeepClone();
                        McpServerLog.Write($"cancel notification requestId={cancelId?.ToJsonString()}");
                        RequestCancellationRegistry.Cancel(cancelId);
                    }

                    continue;
                }

                if (controlClient is not null)
                {
                    try
                    {
                        await controlClient.NotifyRequestAsync().ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        McpServerLog.Write($"control pipe request notify failed: {ex.Message}");
                        await controlClient.DisposeAsync().ConfigureAwait(false);
                        controlClient = null;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        McpServerLog.Write($"control pipe request notify failed: {ex.Message}");
                        await controlClient.DisposeAsync().ConfigureAwait(false);
                        controlClient = null;
                    }
                    catch (InvalidOperationException ex)
                    {
                        McpServerLog.Write($"control pipe request notify failed: {ex.Message}");
                        await controlClient.DisposeAsync().ConfigureAwait(false);
                        controlClient = null;
                    }
                }

                McpServerLog.WriteRequest(incoming.Request, incoming.Format);

                // Dispatch on a background task so this read loop stays live for
                // notifications/cancelled while a long-running tool (e.g. build with
                // wait_for_completion=true) is in flight.
                _ = Task.Run(() => DispatchRequestAsync(incoming, output, bridge, controlClient, writeLock, toolSurface));
            }
        }
        finally
        {
            if (controlClient is not null)
            {
                await controlClient.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Isolated per-request dispatch method so RunAsync stays within line-length budget and
    // nesting depth stays shallow. Owns the CTS lifetime and the write-lock acquire.
    private static async Task DispatchRequestAsync(
        McpProtocol.IncomingMessage incoming,
        Stream output,
        BridgeConnection bridge,
        ServiceControlClient? controlClient,
        SemaphoreSlim writeLock,
        McpToolSurface toolSurface)
    {
        JsonNode? requestId = incoming.Request["id"]?.DeepClone();
        Stopwatch stopwatch = Stopwatch.StartNew();
        using IDisposable leaseActivity = StdioHostLease.BeginActivity();
        using CancellationTokenSource cts = RequestCancellationRegistry.Register(requestId);
        try
        {
            JsonObject? response = await HandleRequestAsync(incoming.Request, bridge, controlClient, toolSurface)
                .ConfigureAwait(false);

            if (response is not null)
            {
                McpServerLog.WriteResponse(response);
                await WriteLockedAsync(output, response, incoming.Format, writeLock)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            McpServerLog.Write($"tool cancelled id={requestId?.ToJsonString()}");
            // No response sent for cancelled requests.
        }
        catch (Exception ex) when (ex is not null) // top-level dispatch boundary — fire-and-forget task
        {
            McpServerLog.Write($"dispatch fatal id={requestId?.ToJsonString()} ex={ex}");
        }
        finally
        {
            McpServerLog.Write($"dispatch finished id={FormatId(requestId)} ms={stopwatch.ElapsedMilliseconds}");
            RequestCancellationRegistry.Unregister(requestId);
        }
    }

    internal static async Task<JsonObject?> HandleRequestAsync(
        JsonObject request,
        BridgeConnection bridge,
        ServiceControlClient? controlClient,
        McpToolSurface? toolSurface = null)
    {
        JsonNode? id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonObject? @params = request["params"] as JsonObject;

        if (method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        try
        {
            JsonNode result = method switch
            {
                "initialize"                => InitializeResult(@params, toolSurface ?? McpToolSurface.Lazy),
                "tools/list"                => new JsonObject { ["tools"] = Registry.BuildToolsList(toolSurface ?? McpToolSurface.Lazy) },
                "tools/call"                => await DispatchToolAsync(id, @params, bridge, controlClient).ConfigureAwait(false),
                "resources/list"            => EmptyResourcesList(),
                "resources/templates/list"  => EmptyResourceTemplatesList(),
                "ping"                      => new JsonObject(),
                _                           => throw new McpRequestException(
                    id, McpErrorCodes.MethodNotFound, $"Unsupported method: {method}"),
            };

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpRequestException ex)
        {
            McpServerLog.Write($"request error method={method} code={ex.Code} message={ex.Message}");
            return McpProtocol.ErrorResponse(ex.Id ?? id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate to the concurrent dispatch handler in RunAsync, which suppresses the response.
        }
        catch (Exception ex) when (ex is not null) // top-level MCP request boundary
        {
            McpServerLog.Write($"request fatal method={method} message={ex}");
            return McpProtocol.ErrorResponse(id, McpErrorCodes.MethodNotFound, ex.Message);
        }
    }

    private static JsonObject InitializeResult(JsonObject? @params, McpToolSurface toolSurface)
    {
        string clientVersion = @params?["protocolVersion"]?.GetValue<string>() ?? string.Empty;
        string negotiated = McpProtocol.SelectProtocolVersion(clientVersion);

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"]    = "vs_ide_bridge",
                ["version"] = "0.1.0",
            },
            ["toolSurface"] = toolSurface.ToJsonObject(),
            ["instructions"] =
                BuildInstructions(toolSurface),
        };
    }

    private static string BuildInstructions(McpToolSurface toolSurface)
    {
        string surfaceText = toolSurface.IsFull
            ? "The full tool surface is exposed in tools/list. "
            : "A compact lazy tool surface is exposed in tools/list. Use list_tools to see every available tool name. ";

        return
            "VS IDE Bridge MCP server. " +
            "These tools are for YOU (the AI assistant) to call autonomously. " +
            "Do not ask the user which tools to use, do not present tool names or tool lists to the user, " +
            "and do not wait for user approval before calling a tool. " +
            "Interpret tool results and respond to the user in plain language. " +
            surfaceText +
            "In lazy mode, most bridge tools are not top-level MCP tools — call them through call_tool. " +
            "Example: call_tool({\"name\":\"read_file\",\"arguments\":{\"file\":\"C:/path/File.cs\",\"start_line\":1}}) " +
            "or call_tool({\"name\":\"errors\",\"arguments\":{}}) " +
            "or call_tool({\"name\":\"git_status\",\"arguments\":{}}). " +
            "Use recommend_tools for task-based discovery and tool_help with name=<tool> for the full schema. " +
            "If you get an unknown-tool error, call list_tools first — do not guess tool names.";
    }

    private static JsonObject EmptyResourcesList()
    {
        return new JsonObject
        {
            ["resources"] = new JsonArray(),
        };
    }

    private static JsonObject EmptyResourceTemplatesList()
    {
        return new JsonObject
        {
            ["resourceTemplates"] = new JsonArray(),
        };
    }

    private static async Task<JsonNode> DispatchToolAsync(
        JsonNode? id, JsonObject? @params, BridgeConnection bridge, ServiceControlClient? controlClient)
    {
        string toolName = @params?["name"]?.GetValue<string>() ?? string.Empty;
        JsonObject? args = @params?["arguments"] as JsonObject;

        if (string.IsNullOrWhiteSpace(toolName))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing 'name' in tools/call params.");

        McpServerLog.Write($"dispatch tool={toolName}");

        if (controlClient is null)
        {
            return await Registry.DispatchAsync(id, toolName, args, bridge).ConfigureAwait(false);
        }

        await controlClient.NotifyCommandStartAsync().ConfigureAwait(false);
        try
        {
            return await Registry.DispatchAsync(id, toolName, args, bridge).ConfigureAwait(false);
        }
        finally
        {
            await controlClient.NotifyCommandEndAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ServiceControlClient?> TryConnectControlPipeAsync()
    {
        try
        {
            return await ServiceControlClient.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // Serialize stdout writes across concurrent request tasks.
    private static async Task WriteLockedAsync(
        Stream output, JsonObject response, McpProtocol.WireFormat format, SemaphoreSlim writeLock)
    {
        string responseId = FormatId(response["id"]);
        Stopwatch stopwatch = Stopwatch.StartNew();
        McpServerLog.Write($"response write queued id={responseId} format={format}");

        bool lockTaken = await writeLock.WaitAsync(StdoutWriteTimeout).ConfigureAwait(false);
        if (!lockTaken)
        {
            McpServerLog.Write(
                $"response write lock timed out id={responseId} after {StdoutWriteTimeout.TotalSeconds:F0}s; exiting stdio host");
            Environment.Exit(StdoutWriteTimeoutExitCode);
            return;
        }

        try
        {
            McpServerLog.Write($"response write start id={responseId}");
            Task writeTask = McpProtocol.WriteAsync(output, response, format);
            Task timeoutTask = Task.Delay(StdoutWriteTimeout);
            Task completedTask = await Task.WhenAny(writeTask, timeoutTask).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, writeTask))
            {
                McpServerLog.Write(
                    $"response write timed out id={responseId} after {StdoutWriteTimeout.TotalSeconds:F0}s; exiting stdio host because stdout did not drain");
                Environment.Exit(StdoutWriteTimeoutExitCode);
                return;
            }

            await writeTask.ConfigureAwait(false);
            McpServerLog.Write($"response write complete id={responseId} ms={stopwatch.ElapsedMilliseconds}");
        }
        catch (IOException ex)
        {
            McpServerLog.WriteException($"response write failed id={responseId}; exiting stdio host", ex);
            Environment.Exit(StdoutWriteTimeoutExitCode);
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.WriteException($"response write failed id={responseId}; exiting stdio host", ex);
            Environment.Exit(StdoutWriteTimeoutExitCode);
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException($"response write failed id={responseId}; exiting stdio host", ex);
            Environment.Exit(StdoutWriteTimeoutExitCode);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static string FormatId(JsonNode? id)
    {
        return id is null ? "<null>" : id.ToJsonString();
    }

    public static async Task RunHttpAsync(string[] args, CancellationToken cancellationToken = default)
    {
        int port = HttpServerDefaults.HttpPort;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(args[i + 1], out int parsedPort) && parsedPort > 0 && parsedPort < 65536)
                    port = parsedPort;
                break;
            }
        }

        string prefix = $"http://localhost:{port}/";
        BridgeConnection bridge = new(args);
        McpToolSurface toolSurface = McpToolSurface.FromArgs(args);

        McpServerLog.Write($"MCP HTTP server starting on {prefix}");

        using HttpListener listener = new();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            McpServerLog.Write("HTTP listener started successfully");
        }
        catch (HttpListenerException ex)
        {
            McpServerLog.Write($"Failed to start listener: {ex}");
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.Write($"Failed to start listener: {ex}");
            throw;
        }

        // When the cancellationToken fires, stop the listener so GetContextAsync throws
        // HttpListenerException with IsListening == false and the accept loop exits cleanly.
        using CancellationTokenRegistration stopReg = cancellationToken.Register(
            static state => ((HttpListener)state!).Stop(), listener);

        while (true)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
                await HandleHttpRequestAsync(context, bridge, toolSurface).ConfigureAwait(false);
            }
            catch (HttpListenerException) when (listener.IsListening == false)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                McpServerLog.Write($"HTTP accept error: {ex}");
            }
            catch (IOException ex)
            {
                McpServerLog.Write($"HTTP accept error: {ex}");
            }
            catch (ObjectDisposedException ex)
            {
                McpServerLog.Write($"HTTP accept error: {ex}");
            }
        }
    }

    private static async Task HandleHttpRequestAsync(HttpListenerContext context, BridgeConnection bridge, McpToolSurface toolSurface)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            McpServerLog.Write($"HTTP {method} {path}");

            if (method == "GET")
            {
                await WriteHealthResponseAsync(context).ConfigureAwait(false);
                return;
            }

            if (method != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.ContentType = "text/plain";
                await context.Response.OutputStream.WriteAsync("Method not allowed"u8.ToArray()).ConfigureAwait(false);
                return;
            }

            JsonObject? request = await ReadHttpRequestAsync(context).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            McpServerLog.WriteRequest(request, McpProtocol.WireFormat.RawJson);

            JsonObject? response = await HandleRequestAsync(request, bridge, controlClient: null, toolSurface).ConfigureAwait(false);

            if (response is not null)
            {
                McpServerLog.WriteResponse(response);
                string jsonResponse = response.ToJsonString();
                byte[] bytes = Encoding.UTF8.GetBytes(jsonResponse);
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 204; // no content for notifications
            }
        }
        catch (McpRequestException ex)
        {
            McpServerLog.Write($"HTTP MCP error code={ex.Code}: {ex.Message}");
            await WriteErrorResponse(context, ex.Message, ex.Code);
        }
        catch (Exception ex) when (ex is not null) // top-level HTTP request boundary
        {
            McpServerLog.Write($"HTTP request fatal: {ex}");
            await WriteErrorResponse(context, ex.Message);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException ex) { McpServerLog.WriteException("HTTP response close skipped because the response was already disposed", ex); }
            catch (HttpListenerException ex) { McpServerLog.WriteException("HTTP response close skipped because the listener was already stopping", ex); }
        }
    }

    private static async Task WriteHealthResponseAsync(HttpListenerContext context)
    {
        JsonObject info = new()
        {
            ["name"] = "vs-ide-bridge",
            ["version"] = "0.1.0",
            ["protocolVersions"] = new JsonArray { "2025-03-26", "2024-11-05" },
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["status"] = "ok"
        };

        await WriteJsonResponseAsync(context, info).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadHttpRequestAsync(HttpListenerContext context)
    {
        string body = await ReadRequestBodyAsync(context.Request).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            context.Response.StatusCode = BadRequestStatusCode;
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(body);
            return node as JsonObject ?? throw new InvalidOperationException("Not an object");
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = BadRequestStatusCode;
            await WriteErrorResponse(context, "Invalid JSON").ConfigureAwait(false);
            return null;
        }
        catch (FormatException)
        {
            context.Response.StatusCode = BadRequestStatusCode;
            await WriteErrorResponse(context, "Invalid JSON").ConfigureAwait(false);
            return null;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = BadRequestStatusCode;
            await WriteErrorResponse(context, "Invalid JSON").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, JsonObject payload)
    {
        string json = payload.ToJsonString();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteErrorResponse(HttpListenerContext context, string message, int code = -32603)
    {
        JsonObject errorResponse = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        context.Response.StatusCode = 200;
        await WriteJsonResponseAsync(context, errorResponse).ConfigureAwait(false);
    }
}
