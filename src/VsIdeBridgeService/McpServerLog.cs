using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static class McpServerLog
{
    private static readonly object SyncRoot = new();
    private static readonly string LogPath = ResolveLogPath();

    public static void Write(string message)
    {
        try
        {
            lock (SyncRoot)
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:O} [pid:{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.Write failed: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.Write failed: {ex}");
        }
        catch (NotSupportedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.Write failed: {ex}");
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"{context}: {ex}");
    }

    public static void WriteRequest(JsonObject request, McpProtocol.WireFormat format)
    {
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonNode? id = request["id"];
        JsonObject? @params = request["params"] as JsonObject;
        string toolName = @params?["name"]?.GetValue<string>() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(toolName))
        {
            Write($"request format={format} id={FormatId(id)} method={method}");
            return;
        }

        // When the outer call is call_tool, also surface the inner tool name so the
        // log shows which catalog tool was actually invoked, not just "call_tool".
        if (string.Equals(toolName, "call_tool", StringComparison.Ordinal))
        {
            JsonObject? arguments = @params?["arguments"] as JsonObject;
            string innerName = arguments?["name"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(innerName))
            {
                Write($"request format={format} id={FormatId(id)} method={method} tool={toolName} inner={innerName}");
                return;
            }
        }

        Write($"request format={format} id={FormatId(id)} method={method} tool={toolName}");
    }

    public static void WriteResponse(JsonObject response)
    {
        JsonNode? id = response["id"];
        JsonObject? result = response["result"] is JsonObject resultObject ? resultObject : null;
        JsonObject? error = response["error"] as JsonObject;

        if (error is not null)
        {
            string code = error["code"]?.ToJsonString() ?? string.Empty;
            string message = error["message"]?.GetValue<string>() ?? string.Empty;
            Write($"response id={FormatId(id)} errorCode={code} errorMessage={message}");
            return;
        }

        if (result is null)
        {
            Write($"response id={FormatId(id)} result=<null>");
            return;
        }

        bool isError = result["isError"]?.GetValue<bool>() ?? false;
        bool hasStructuredContent = result["structuredContent"] is not null;
        bool hasContent = result["content"] is JsonArray;
        Write(
            $"response id={FormatId(id)} isError={isError} hasContent={hasContent} hasStructuredContent={hasStructuredContent}");
    }

    private static string ResolveLogPath()
    {
        string directory = BridgeLogPaths.GetSharedLogDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            string logPath = BridgeLogPaths.GetMcpServerLogPath();
            BridgeLogPaths.RotateMcpServerLog(logPath);
            BridgeLogPaths.CleanupLegacyLogs(directory);
            return logPath;
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.ResolveLogPath failed for '{directory}': {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.ResolveLogPath failed for '{directory}': {ex}");
        }
        catch (NotSupportedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.ResolveLogPath failed for '{directory}': {ex}");
        }

        string fallbackDirectory = BridgeLogPaths.GetTempLogDirectory();
        Directory.CreateDirectory(fallbackDirectory);
        return BridgeLogPaths.GetMcpServerTempLogPath();
    }

    private static string FormatId(JsonNode? id)
    {
        return id is null ? "<null>" : id.ToJsonString();
    }
}
