using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

/// <summary>
/// Persistent named pipe server that eliminates per-call PowerShell overhead (~1500 ms → ~50 ms).
/// Discovery file: %TEMP%\vs-ide-bridge\pipes\bridge-{pid}.json
/// Protocol: newline-delimited JSON, one request per line, one response per line.
/// </summary>
internal sealed class PipeServerService : IDisposable
{
    private readonly VsIdeBridgePackage _package;
    private readonly IdeBridgeRuntime _runtime;
    private readonly string _pipeName;
    private readonly string _discoveryFile;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task? _listenTask;

    public PipeServerService(VsIdeBridgePackage package, IdeBridgeRuntime runtime)
    {
        _package = package;
        _runtime = runtime;
        var pid = Process.GetCurrentProcess().Id;
        _pipeName = $"VsIdeBridge18_{pid}";
        var discoveryDir = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "pipes");
        Directory.CreateDirectory(discoveryDir);
        _discoveryFile = Path.Combine(discoveryDir, $"bridge-{pid}.json");
    }

    public void Start()
    {
        var pid = Process.GetCurrentProcess().Id;
        var discoveryJson = JsonConvert.SerializeObject(new { pid, pipeName = _pipeName });
        File.WriteAllText(_discoveryFile, discoveryJson, new UTF8Encoding(false));
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(PipeServerService), $"Failed to create pipe server instance: {ex.Message}");
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(PipeServerService), $"Pipe accept error: {ex.Message}");
                pipe.Dispose();
                continue;
            }

            // Fire-and-forget: handle each connection on the thread pool
            _ = HandleConnectionAsync(pipe, ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        break; // client disconnected mid-read
                    }

                    if (line == null) break; // clean EOF

                    var responseLine = await ExecuteRequestAsync(line, ct).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(PipeServerService), $"Pipe connection error: {ex.Message}");
            }
        }
    }

    private async Task<string> ExecuteRequestAsync(string requestJson, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        string commandName = "";
        string? requestId = null;

        try
        {
            var request = JsonConvert.DeserializeObject<PipeRequest>(requestJson);
            if (request == null)
                throw new CommandErrorException("invalid_request", "Could not parse request JSON.");

            requestId = request.Id;
            commandName = request.Command ?? "";

            if (!_runtime.TryGetCommand(commandName, out var cmd))
                throw new CommandErrorException("command_not_found", $"Unknown command: '{commandName}'.");

            var args = CommandArgumentParser.Parse(request.Args);

            CommandExecutionResult result = null!;
            await _package.JoinableTaskFactory.RunAsync(async delegate
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                var dte = await _package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
                Assumes.Present(dte);
                var ctx = new IdeCommandContext(_package, dte!, _runtime.Logger, _runtime, ct);
                result = await cmd.ExecuteDirectAsync(ctx, args).ConfigureAwait(true);
            });

            var envelope = BuildEnvelope(commandName, requestId, true, result.Summary, result.Data, result.Warnings, null, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
        catch (CommandErrorException ex)
        {
            var errorObj = new { code = ex.Code, message = ex.Message, details = ex.Details };
            var envelope = BuildEnvelope(commandName, requestId, false, ex.Message, new JObject(), new JArray(), errorObj, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
        catch (Exception ex)
        {
            var errorObj = new { code = "internal_error", message = ex.Message, details = new { exception = ex.ToString() } };
            var envelope = BuildEnvelope(commandName, requestId, false, ex.Message, new JObject(), new JArray(), errorObj, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
    }

    private static CommandEnvelope BuildEnvelope(
        string command,
        string? requestId,
        bool success,
        string summary,
        JToken data,
        JArray warnings,
        object? error,
        DateTimeOffset startedAt)
    {
        return new CommandEnvelope
        {
            SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
            Command = command,
            RequestId = requestId,
            Success = success,
            StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
            FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Summary = summary,
            Warnings = warnings,
            Error = error,
            Data = data,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (File.Exists(_discoveryFile))
                File.Delete(_discoveryFile);
        }
        catch { }
    }
}
