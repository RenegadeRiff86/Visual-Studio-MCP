using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

/// <summary>
/// Manages the optional Streamable HTTP MCP server lifecycle (MCP spec 2025-03-26).
///
/// Mirrors <see cref="HttpServerController"/> but drives
/// <see cref="StreamableHttpMode.RunAsync"/> on <see cref="DefaultPort"/>
/// so both HTTP transports can run simultaneously without port conflicts.
///
/// The flag file at <see cref="HttpServerStatePaths.GetStreamableHttpEnabledFlagPath"/>
/// is the shared persistence layer so the enabled state survives restarts and stays in
/// sync with the Visual Studio extension toggle.
///
/// Call <see cref="RestoreState"/> from service startup and
/// <see cref="StopAndWait"/> from service shutdown.
/// </summary>
internal static class StreamableHttpServerController
{
    private const int PipeConnectTimeoutMs = 1000;
    private static readonly TimeSpan ListenerStateWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string FlagFilePath = HttpServerStatePaths.GetStreamableHttpEnabledFlagPath();

    private static readonly string[] ServerArgs = ["--port", HttpServerDefaults.StreamableHttpPortText];

    internal const int DefaultPort = HttpServerDefaults.StreamableHttpPort;
    internal static string Url => HttpServerDefaults.StreamableHttpUrl;

    private static readonly object Lock = new();
    private static CancellationTokenSource? _cts;
    private static Task? _serverTask;

    /// <summary>Whether the flag file exists (persisted enabled state).</summary>
    public static bool IsEnabled => System.IO.File.Exists(FlagFilePath);

    /// <summary>Whether the in-process server task is currently running.</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Lock)
            {
                return _serverTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>Cross-process truth: whether something is accepting connections on <see cref="DefaultPort"/>.</summary>
    public static bool IsPortListening => ProbePort();

    /// <summary>Write the enabled flag without starting the server. Used by installer.</summary>
    public static void MarkEnabled()
    {
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(FlagFilePath)!);
        System.IO.File.WriteAllText(FlagFilePath, string.Empty);
    }

    /// <summary>Start the server if the flag file exists. Called from service OnStart.</summary>
    public static void RestoreState()
    {
        if (IsEnabled)
            StartCore();
    }

    /// <summary>Enable: write the flag file and ensure a listener is running.</summary>
    public static JsonObject Enable()
    {
        MarkEnabled();
        string? note = TryReconcileViaService(desiredEnabled: true);
        if (note is null)
            StartCore();
        return BuildStatus(note);
    }

    /// <summary>Disable: delete the flag file and ensure no listener is running.</summary>
    public static JsonObject Disable()
    {
        try
        {
            System.IO.File.Delete(FlagFilePath);
        }
        catch (System.IO.IOException ex)
        {
            McpServerLog.WriteException("StreamableHttpServerController.Disable: delete flag", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            McpServerLog.WriteException("StreamableHttpServerController.Disable: delete flag", ex);
        }

        string? note = TryReconcileViaService(desiredEnabled: false);
        if (note is null)
            StopCore();
        return BuildStatus(note);
    }

    /// <summary>Current status snapshot.</summary>
    public static JsonObject GetStatus() => BuildStatus(note: null);

    /// <summary>Stop the server and wait up to <paramref name="timeout"/>. Called from service OnStop.</summary>
    public static void StopAndWait(TimeSpan timeout)
    {
        Task? task;
        lock (Lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            task = _serverTask;
        }
        task?.Wait(timeout);
    }

    private static void StartCore()
    {
        Task? oldTask;
        lock (Lock)
        {
            if (_cts != null && _serverTask is { IsCompleted: false })
                return;

            oldTask = _serverTask;
        }

        oldTask?.Wait(TimeSpan.FromSeconds(5));

        lock (Lock)
        {
            if (_cts != null)
                return;

            CancellationTokenSource cts = new();
            _cts = cts;
            _serverTask = Task.Run(() => StreamableHttpMode.RunAsync(ServerArgs, cts.Token));
        }
    }

    private static void StopCore()
    {
        lock (Lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string? TryReconcileViaService(bool desiredEnabled)
    {
        // If we ARE the service (non-interactive), let StartCore/StopCore handle it locally.
        if (!Environment.UserInteractive)
            return null;

        try
        {
            SendControlEvent(desiredEnabled ? "STREAMABLE_HTTP_ENABLE" : "STREAMABLE_HTTP_DISABLE");
        }
        catch (TimeoutException)
        {
            return null; // Service not running — fall back to in-process.
        }
        catch (IOException ex)
        {
            McpServerLog.WriteException("StreamableHttpServerController.TryReconcileViaService", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            McpServerLog.WriteException("StreamableHttpServerController.TryReconcileViaService", ex);
            return null;
        }

        bool reached = WaitForListenerState(desiredEnabled, ListenerStateWaitTimeout);
        if (!reached)
        {
            return desiredEnabled
                ? "sent STREAMABLE_HTTP_ENABLE to service; listener not yet observed on port"
                : "sent STREAMABLE_HTTP_DISABLE to service; listener still observed on port";
        }

        return desiredEnabled
            ? "service confirmed Streamable HTTP listener running"
            : "service confirmed Streamable HTTP listener stopped";
    }

    private static void SendControlEvent(string evt)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("VsIdeBridgeService control pipes are Windows-only.");
        }

        using NamedPipeClientStream pipe = new(
            ".", NamedPipeAccessDefaults.ServiceControlPipeName, PipeDirection.Out, PipeOptions.None);
        pipe.Connect(PipeConnectTimeoutMs);
        using StreamWriter writer = new(pipe, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        writer.WriteLine(evt);
    }

    private static bool WaitForListenerState(bool listening, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ProbePort() == listening)
                return true;
            Thread.Sleep(100);
        }
        return ProbePort() == listening;
    }

    private static bool ProbePort()
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            IAsyncResult ar = client.BeginConnect("127.0.0.1", DefaultPort, null, null);
            bool signalled = ar.AsyncWaitHandle.WaitOne(PortProbeTimeout, exitContext: false);
            if (!signalled)
                return false;

            try
            {
                client.EndConnect(ar);
                return client.Connected;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        catch (SocketException)
        {
            return false;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
        finally
        {
            client?.Close();
        }
    }

    private static JsonObject BuildStatus(string? note)
    {
        bool listening = ProbePort();
        bool enabled = IsEnabled;
        JsonObject result = new()
        {
            ["enabled"] = enabled,
            ["running"] = listening,
            ["flagInSync"] = enabled == listening,
            ["port"] = DefaultPort,
            ["url"] = Url,
        };
        if (!string.IsNullOrEmpty(note))
            result["note"] = note;
        return result;
    }
}
