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
/// Manages Streamable HTTP server state for VS IDE Bridge (MCP spec 2025-03-26).
///
/// Mirrors <see cref="HttpServerStateManager"/> but drives the Streamable HTTP
/// transport on its default port, sending <c>STREAMABLE_HTTP_ENABLE</c> /
/// <c>STREAMABLE_HTTP_DISABLE</c> over the service control pipe.
/// </summary>
internal static class StreamableHttpServerStateManager
{
    private static readonly string FlagFilePath = HttpServerStatePaths.GetStreamableHttpEnabledFlagPath();

    public const int DefaultPort = HttpServerDefaults.StreamableHttpPort;
    public static string Url => HttpServerDefaults.StreamableHttpUrl;

    private const int PipeConnectTimeoutMs = 1000;
    private static readonly TimeSpan ListenerStateWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PortProbePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether the Streamable HTTP server is marked as enabled (flag file exists).</summary>
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
                System.Diagnostics.Debug.WriteLine($"[StreamableHttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamableHttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
            catch (NotSupportedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamableHttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Enable the Streamable HTTP server by writing the flag file.</summary>
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
            throw new InvalidOperationException($"Failed to enable Streamable HTTP server: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to enable Streamable HTTP server: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to enable Streamable HTTP server: {ex.Message}", ex);
        }
    }

    /// <summary>Disable the Streamable HTTP server by removing the flag file.</summary>
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
            throw new InvalidOperationException($"Failed to disable Streamable HTTP server: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to disable Streamable HTTP server: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to disable Streamable HTTP server: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Enable the server, reconcile with the running service over the control pipe,
    /// then poll until the listener is observed on <see cref="DefaultPort"/>.
    /// </summary>
    public static async Task EnableAndReconcileAsync(CancellationToken cancellationToken = default)
    {
        Enable();
        await TrySendControlEventAsync("STREAMABLE_HTTP_ENABLE", cancellationToken).ConfigureAwait(false);
        await WaitForListenerStateAsync(listening: true, ListenerStateWaitTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mirror of <see cref="EnableAndReconcileAsync"/>.</summary>
    public static async Task DisableAndReconcileAsync(CancellationToken cancellationToken = default)
    {
        Disable();
        await TrySendControlEventAsync("STREAMABLE_HTTP_DISABLE", cancellationToken).ConfigureAwait(false);
        await WaitForListenerStateAsync(listening: false, ListenerStateWaitTimeout, cancellationToken).ConfigureAwait(false);
    }

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
            System.Diagnostics.Debug.WriteLine($"[StreamableHttpServerStateManager] Service control pipe unreachable for {eventName}: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamableHttpServerStateManager] Service control pipe IO error for {eventName}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamableHttpServerStateManager] Service control pipe access denied for {eventName}: {ex.Message}");
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
