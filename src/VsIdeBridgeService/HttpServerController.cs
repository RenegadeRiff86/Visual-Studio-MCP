using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

/// <summary>
/// Manages the optional HTTP MCP server lifecycle.
///
/// The HTTP listener is owned exclusively by the Windows Service process.
/// The MCP <c>http_enable</c> / <c>http_disable</c> tools may be invoked from a
/// short-lived stdio MCP child process; in that case the call must reconcile state
/// by stop/starting the Windows Service rather than trying to bind the port locally
/// (which would either collide with the service or die when the stdio process exits).
///
/// The flag file at <see cref="HttpServerStatePaths.GetHttpEnabledFlagPath"/> is the
/// shared persistence layer so the enabled state survives restarts and stays in
/// sync with the Visual Studio extension toggle.
///
/// Call <see cref="RestoreState"/> from service startup and
/// <see cref="StopAndWait"/> from service shutdown.
/// </summary>
internal static class HttpServerController
{
    private const int PipeConnectTimeoutMs = 1000;
    private static readonly TimeSpan ListenerStateWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string FlagFilePath = HttpServerStatePaths.GetHttpEnabledFlagPath();

    private static readonly string[] ServerArgs = ["--port", "8080"];

    internal const int DefaultPort = 8080;
    internal static string Url => $"http://localhost:{DefaultPort}/";

    private static readonly object Lock = new();
    private static CancellationTokenSource? _cts;
    private static Task? _serverTask;

    /// <summary>Whether the flag file exists (persisted enabled state).</summary>
    public static bool IsEnabled => System.IO.File.Exists(FlagFilePath);

    /// <summary>Whether the in-process server task is currently running.</summary>
    /// <remarks>
    /// Process-local. From a stdio MCP child this only reports about the child's own
    /// listener task and does NOT reflect whether the Windows Service is hosting one.
    /// Use <see cref="IsPortListening"/> for cross-process truth.
    /// </remarks>
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

    /// <summary>
    /// Whether something is actually accepting TCP connections on
    /// <see cref="DefaultPort"/>. Cross-process truth, used to bridge the gap between
    /// the stdio MCP child and the long-running Windows Service process.
    /// </summary>
    public static bool IsPortListening => ProbePort();

    /// <summary>
    /// Write the enabled flag without starting the server.
    /// Used during service construction so that <see cref="RestoreState"/> starts
    /// the server once OnStart is called.
    /// </summary>
    public static void MarkEnabled()
    {
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(FlagFilePath)!);
        System.IO.File.WriteAllText(FlagFilePath, string.Empty);
    }

    /// <summary>
    /// Start the server if the flag file exists. Called from service OnStart.
    /// Does not modify the flag file.
    /// </summary>
    public static void RestoreState()
    {
        if (IsEnabled)
            StartCore();
    }

    /// <summary>
    /// Enable: write the flag file and ensure a listener is running. When invoked
    /// from outside the service, prefers stop/starting the Windows Service so the
    /// listener lives in the long-running process.
    /// </summary>
    public static JsonObject Enable()
    {
        MarkEnabled();
        string? note = TryReconcileViaService(desiredEnabled: true);
        if (note is null)
        {
            // Service not installed (or call came from inside the service itself);
            // host the listener in this process as a fallback.
            StartCore();
        }
        return BuildStatus(note);
    }

    /// <summary>
    /// Disable: delete the flag file and ensure no listener is running. When invoked
    /// from outside the service, prefers stop/starting the Windows Service so its
    /// listener actually releases the port.
    /// </summary>
    public static JsonObject Disable()
    {
        try
        {
            System.IO.File.Delete(FlagFilePath);
        }
        catch (System.IO.IOException ex)
        {
            McpServerLog.WriteException("HttpServerController.Disable: delete flag", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            McpServerLog.WriteException("HttpServerController.Disable: delete flag", ex);
        }

        string? note = TryReconcileViaService(desiredEnabled: false);
        if (note is null)
        {
            StopCore();
        }
        return BuildStatus(note);
    }

    /// <summary>Current status snapshot (enabled, running, port, url).</summary>
    public static JsonObject GetStatus() => BuildStatus(note: null);

    /// <summary>
    /// Stop the server and wait up to <paramref name="timeout"/> for it to finish.
    /// Does not modify the flag file. Called from service OnStop.
    /// </summary>
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
                return; // Already running with a live token — do nothing.

            oldTask = _serverTask; // May still be winding down after StopCore.
        }

        // Wait for the previous server to release the port before binding again.
        // Runs outside the lock so we don't block concurrent status reads.
        oldTask?.Wait(TimeSpan.FromSeconds(5));

        lock (Lock)
        {
            if (_cts != null)
                return; // Another caller started the server while we waited.

            CancellationTokenSource cts = new();
            _cts = cts;
            _serverTask = Task.Run(() => McpServerMode.RunHttpAsync(ServerArgs, cts.Token));
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

    /// <summary>
    /// Try to reconcile HTTP listener state by stop/starting the Windows Service.
    /// Returns a status note if the service was used, or <c>null</c> if the service
    /// is not installed (or this call is running inside the service itself) and the
    /// caller should fall back to the in-process listener.
    /// </summary>
    private static string? TryReconcileViaService(bool desiredEnabled)
    {
        // The Windows Service runs non-interactively under SCM. If we ARE the
        // service, do not try to talk to ourselves over the pipe; let StartCore/
        // StopCore handle it locally.
        if (!Environment.UserInteractive)
            return null;

        // The service control pipe ACL grants AuthenticatedUsers read/write, so
        // this works without administrator rights — unlike ServiceController,
        // which requires SCM access most users don't have.
        try
        {
            SendControlEvent(desiredEnabled ? "HTTP_ENABLE" : "HTTP_DISABLE");
        }
        catch (TimeoutException)
        {
            // Pipe not accepting connections — service is not running. Caller
            // should fall back to the in-process listener.
            return null;
        }
        catch (IOException ex)
        {
            McpServerLog.WriteException("HttpServerController.TryReconcileViaService", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            McpServerLog.WriteException("HttpServerController.TryReconcileViaService", ex);
            return null;
        }

        // Poll the port until it reaches the desired state or we time out. Polling
        // is necessary because the control pipe is one-way (fire-and-forget).
        bool reached = WaitForListenerState(desiredEnabled, ListenerStateWaitTimeout);
        if (!reached)
        {
            return desiredEnabled
                ? "sent HTTP_ENABLE to service; listener not yet observed on port"
                : "sent HTTP_DISABLE to service; listener still observed on port";
        }

        return desiredEnabled
            ? "service confirmed HTTP listener running"
            : "service confirmed HTTP listener stopped";
    }

    private static void SendControlEvent(string evt)
    {
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

    /// <summary>
    /// Cheap synchronous TCP probe to determine if a listener is actually accepting
    /// connections on <see cref="DefaultPort"/>. Times out after
    /// <see cref="PortProbeTimeout"/> so it never blocks the caller.
    /// </summary>
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
