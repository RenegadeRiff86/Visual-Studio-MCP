using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

/// <summary>
/// Manages HTTP server state for VS IDE Bridge.
///
/// The flag file at <see cref="HttpServerStatePaths.GetHttpEnabledFlagPath"/> is
/// the persisted intent shared with VsIdeBridgeService.HttpServerController. The
/// reconciliation methods additionally signal the running Windows Service over the
/// <see cref="NamedPipeAccessDefaults.ServiceControlPipeName"/> pipe so the actual
/// HTTP listener (owned by the service) starts/stops without waiting for a service
/// restart, then poll the port to confirm the listener reached the desired state.
/// </summary>
internal static class HttpServerStateManager
{
    private static readonly string FlagFilePath = HttpServerStatePaths.GetHttpEnabledFlagPath();

    public const int DefaultPort = 8080;
    public static string Url => $"http://localhost:{DefaultPort}/";

    private const int PipeConnectTimeoutMs = 1000;
    private static readonly TimeSpan ListenerStateWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PortProbePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether the HTTP server is marked as enabled (flag file exists).</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                return File.Exists(FlagFilePath);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
            catch (NotSupportedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Enable the HTTP server by writing the flag file.</summary>
    public static void Enable()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FlagFilePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(FlagFilePath, string.Empty);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to enable HTTP server: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to enable HTTP server: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to enable HTTP server: {ex.Message}", ex);
        }
    }

    /// <summary>Disable the HTTP server by removing the flag file.</summary>
    public static void Disable()
    {
        try
        {
            if (File.Exists(FlagFilePath))
            {
                File.Delete(FlagFilePath);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to disable HTTP server: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to disable HTTP server: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to disable HTTP server: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Enable the HTTP server and reconcile with the running service.
    ///
    /// Writes the flag file (so state survives a service restart), then signals the
    /// service over the control pipe to start its <see cref="System.Net.HttpListener"/>
    /// immediately, then polls port <see cref="DefaultPort"/> until the listener is
    /// observed accepting connections (or <see cref="ListenerStateWaitTimeout"/> elapses).
    ///
    /// If the service is not running the pipe send is skipped silently — the flag
    /// file ensures the next service start will pick up the enabled state.
    /// </summary>
    public static async Task EnableAndReconcileAsync(CancellationToken cancellationToken = default)
    {
        Enable();
        await TrySendControlEventAsync("HTTP_ENABLE", cancellationToken).ConfigureAwait(false);
        await WaitForListenerStateAsync(listening: true, ListenerStateWaitTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disable the HTTP server and reconcile with the running service. Mirror of
    /// <see cref="EnableAndReconcileAsync(CancellationToken)"/>.
    /// </summary>
    public static async Task DisableAndReconcileAsync(CancellationToken cancellationToken = default)
    {
        Disable();
        await TrySendControlEventAsync("HTTP_DISABLE", cancellationToken).ConfigureAwait(false);
        await WaitForListenerStateAsync(listening: false, ListenerStateWaitTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort signal to the service. Swallows expected failures (service not
    /// running, pipe ACL refusal) so the menu toggle never throws purely because the
    /// service can't be reached — the flag file still records intent.
    /// </summary>
    private static async Task<bool> TrySendControlEventAsync(string eventName, CancellationToken cancellationToken)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(
                ".",
                NamedPipeAccessDefaults.ServiceControlPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);

            using StreamWriter writer = new(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 256, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(eventName).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Service control pipe unreachable for {eventName}: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Service control pipe IO error for {eventName}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Service control pipe access denied for {eventName}: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    /// <summary>
    /// Probe TCP <see cref="DefaultPort"/> on localhost. Returns true if a listener
    /// accepts the connection within <see cref="PortProbeTimeout"/>.
    /// </summary>
    private static bool TryProbePort()
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            IAsyncResult result = client.BeginConnect("127.0.0.1", DefaultPort, null, null);
            bool completed = result.AsyncWaitHandle.WaitOne(PortProbeTimeout);
            if (!completed)
            {
                return false;
            }
            client.EndConnect(result);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            client?.Close();
        }
    }

    /// <summary>
    /// Poll the port until it matches <paramref name="listening"/> or the timeout
    /// elapses. Returns true if the desired state was observed.
    /// </summary>
    private static async Task<bool> WaitForListenerStateAsync(bool listening, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryProbePort() == listening)
            {
                return true;
            }
            try
            {
                await Task.Delay(PortProbePollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        return TryProbePort() == listening;
    }
}
