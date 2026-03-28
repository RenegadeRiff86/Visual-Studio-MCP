using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Connects to one VS bridge named pipe, sends a request, and reads the response.
// Acquires a per-pipe file lock to prevent concurrent requests on the same pipe.
internal sealed class VsPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly int _timeoutMs;
    private readonly FileStream _gate;

    private static readonly string LockDirectory =
        Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "locks");

    public VsPipeClient(string pipeName, int timeoutMs, int gateTimeoutMs)
    {
        _timeoutMs = Math.Max(1_000, timeoutMs);
        _gate = AcquireGate(pipeName, Math.Max(250, gateTimeoutMs));

        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource cts = new(_timeoutMs);
        _pipe.ConnectAsync(cts.Token).GetAwaiter().GetResult();

        _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public async Task<JsonObject> SendAsync(JsonObject payload)
    {
        using CancellationTokenSource cts = new(_timeoutMs);
        try
        {
            await _writer.WriteLineAsync(payload.ToJsonString().AsMemory(), cts.Token).ConfigureAwait(false);
            string? line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                throw new BridgeException("The VS bridge pipe returned an empty response.");
            return JsonNode.Parse(line) as JsonObject
                ?? throw new BridgeException("The VS bridge pipe returned malformed JSON.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Timed out waiting for VS bridge response after {_timeoutMs} ms. Visual Studio may be blocked.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static FileStream AcquireGate(string pipeName, int gateTimeoutMs)
    {
        Directory.CreateDirectory(LockDirectory);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipeName)));
        string lockFile = Path.Combine(LockDirectory, $"{hash}.lock");
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(gateTimeoutMs);

        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }

        throw new BridgeBusyException(pipeName, gateTimeoutMs);
    }
}

internal sealed class BridgeBusyException(string pipeName, int gateTimeoutMs)
    : BridgeException($"Bridge pipe '{pipeName}' is busy with another request. Try again soon or avoid overlapping bridge calls. Waited {gateTimeoutMs} ms for exclusive access.");

// Thrown for logical bridge errors (not I/O or timeout) so callers can distinguish error types.
internal class BridgeException(string message) : Exception(message);
