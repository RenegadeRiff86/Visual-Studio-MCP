using System.Text.Json.Nodes;
using VsIdeBridgeService.Diagnostics;
using static VsIdeBridgeService.BridgeConnectionDefaults;

namespace VsIdeBridgeService;

// Manages a VS bridge connection for one MCP session.
// Caches the discovered instance to avoid repeated discovery on every tool call.
// Thread-safe: multiple concurrent tool calls may use the same connection.
internal sealed class BridgeConnection
{
    private readonly DiscoveryMode _discoveryMode;
    private readonly int? _timeoutOverrideMs;
    private readonly object _gate = new();
    private readonly DocumentDiagnosticsCoordinator _documentDiagnostics;
    private readonly ConnectionState _state = new();

    public BridgeConnection(string[] args)
    {
        _discoveryMode = ResolveMode(args);
        _timeoutOverrideMs = GetOptionalIntArg(args, "timeout-ms");
        _documentDiagnostics = new DocumentDiagnosticsCoordinator(this);
    }

    internal enum ToolTimeoutProfile
    {
        Fast,
        Interactive,
        Heavy,
    }

    private sealed class ConnectionState
    {
        public BridgeInstanceSelector Selector { get; set; } = new();
        public BridgeInstance? Cached { get; set; }
        public string LastSolutionPath { get; set; } = string.Empty;
        public string? PendingBindingNotice { get; set; }
    }

    // ── Public API used by tool handlers ──────────────────────────────────────

    public Task<JsonObject> SendAsync(JsonNode? id, string command, string args)
        => SendCoreAsync(id, command, args, ignoreSolutionHint: false);

    public Task<JsonObject> SendIgnoringSolutionHintAsync(JsonNode? id, string command, string args)
        => SendCoreAsync(id, command, args, ignoreSolutionHint: true);

    public string CurrentSolutionPath
    {
        get { lock (_gate) { return _state.LastSolutionPath; } }
    }

    public BridgeInstance? CurrentInstance
    {
        get { lock (_gate) { return _state.Cached; } }
    }

    public BridgeInstanceSelector CurrentSelector
    {
        get { lock (_gate) { return _state.Selector; } }
    }

    public DiscoveryMode Mode => _discoveryMode;

    public DocumentDiagnosticsCoordinator DocumentDiagnostics => _documentDiagnostics;

    // Bind to a specific instance and return binding info.
    public async Task<JsonObject> BindAsync(JsonNode? id, JsonObject? args)
    {
        BridgeInstanceSelector newSelector = ParseSelector(args);
        lock (_gate)
        {
            _state.Selector = newSelector;
            _state.Cached = null;
            _state.PendingBindingNotice = null;
        }

        try
        {
            BridgeInstance discovered = await GetInstanceAsync(ignoreSolutionHint: false).ConfigureAwait(false);
            RememberSolutionPath(discovered.SolutionPath);
            _documentDiagnostics.QueueRefreshAndGetSnapshot("bind", clearCached: true);
            return new JsonObject
            {
                ["success"] = true,
                ["binding"] = InstanceToJson(discovered),
                ["selector"] = SelectorToJson(CurrentSelector),
            };
        }
        catch (BridgeException ex)
        {
            throw new McpRequestException(id, BridgeError, ex.Message);
        }
    }

    // Prefer a solution for future discover without full rebind.
    public void PreferSolution(string? solutionHint)
    {
        lock (_gate)
        {
            _state.Selector = new BridgeInstanceSelector
            {
                InstanceId = _state.Selector.InstanceId,
                ProcessId = _state.Selector.ProcessId,
                PipeName = _state.Selector.PipeName,
                SolutionHint = solutionHint,
            };
            _state.Cached = null;
            _state.PendingBindingNotice = null;
        }
    }

    // ── Internal send logic ────────────────────────────────────────────────────

    private async Task<JsonObject> SendCoreAsync(JsonNode? id, string command, string args, bool ignoreSolutionHint)
        => await SendCoreAsync(id, command, args, ignoreSolutionHint, SelectTimeoutProfile(command)).ConfigureAwait(false);

    private async Task<JsonObject> SendCoreAsync(
        JsonNode? id,
        string command,
        string args,
        bool ignoreSolutionHint,
        ToolTimeoutProfile timeoutProfile)
    {
        try
        {
            JsonObject response = await SendPipeAsync(command, args, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
            return await FinalizePipeResponseAsync(response, command, args, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
        }
        catch (BridgeException ex) { throw new McpRequestException(id, BridgeError, ex.Message); }
        catch (TimeoutException ex) { throw new McpRequestException(id, TimeoutError, $"Timed out: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) when (ShouldRetry(timeoutProfile))
        {
            return await RetryAfterFailureAsync(id, command, args, ex, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
        }
        catch (IOException ex) when (ShouldRetry(timeoutProfile))
        {
            return await RetryAfterFailureAsync(id, command, args, ex, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex) { throw new McpRequestException(id, CommError, $"VS bridge communication failed: {ex.Message}"); }
        catch (IOException ex) { throw new McpRequestException(id, CommError, $"VS bridge communication failed: {ex.Message}"); }
    }

    private async Task<JsonObject> SendPipeAsync(string command, string args, bool ignoreSolutionHint, ToolTimeoutProfile timeoutProfile)
    {
        BridgeInstance instance = await GetInstanceAsync(ignoreSolutionHint).ConfigureAwait(false);
        await using VsPipeClient client = new(
            instance.PipeName,
            GetCommandTimeoutMs(timeoutProfile),
            GetPipeGateTimeoutMs(timeoutProfile));
        JsonObject request = new()
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["command"] = command,
            ["args"] = args,
        };
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private async Task<JsonObject> RetryAfterFailureAsync(
        JsonNode? id,
        string command,
        string args,
        Exception ex,
        bool ignoreSolutionHint,
        ToolTimeoutProfile timeoutProfile)
    {
        BridgeInstance? evicted = ClearCached();
        _ = evicted ?? throw new McpRequestException(id, CommError, $"VS bridge communication failed: {ex.Message}");

        try
        {
            JsonObject response = await SendPipeAsync(command, args, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
            return await FinalizePipeResponseAsync(response, command, args, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
        }
        catch (BridgeException retryEx) { throw new McpRequestException(id, BridgeError, retryEx.Message); }
        catch (TimeoutException retryEx) { throw new McpRequestException(id, TimeoutError, $"Timed out: {retryEx.Message}"); }
        catch (Exception retryEx) when (retryEx is not null) { throw new McpRequestException(id, CommError, $"VS bridge retry failed: {retryEx.Message}"); }
    }

    private async Task<JsonObject> FinalizePipeResponseAsync(JsonObject response, string command, string args, bool ignoreSolutionHint, ToolTimeoutProfile timeoutProfile)
    {
        response = await RetryImplicitBindingCancellationAsync(response, command, args, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
        AttachPendingBindingNotice(response);
        RememberSolutionPath(response["Data"]?["solutionPath"]?.GetValue<string>());
        return response;
    }

    private async Task<BridgeInstance> GetInstanceAsync(bool ignoreSolutionHint)
    {
        BridgeInstanceSelector selectorSnapshot;
        lock (_gate)
        {
            if (_state.Cached is not null) return _state.Cached;
            selectorSnapshot = _state.Selector;
        }

        BridgeInstanceSelector effectiveSelector = ignoreSolutionHint
            ? new BridgeInstanceSelector
            {
                InstanceId = selectorSnapshot.InstanceId,
                ProcessId = selectorSnapshot.ProcessId,
                PipeName = selectorSnapshot.PipeName,
                SolutionHint = null,
            }
            : selectorSnapshot;

        BridgeInstance discovered = await VsDiscovery.SelectAsync(effectiveSelector, _discoveryMode).ConfigureAwait(false);

        lock (_gate)
        {
            if (_state.Cached is null && ReferenceEquals(_state.Selector, selectorSnapshot))
            {
                _state.Cached = discovered;
                if (!selectorSnapshot.HasAny)
                {
                    _state.PendingBindingNotice = $"Auto-bound to {discovered.Label}.";
                }
            }
        }

        return discovered;
    }

    private BridgeInstance? ClearCached()
    {
        lock (_gate)
        {
            BridgeInstance? prev = _state.Cached;
            _state.Cached = null;
            return prev;
        }
    }

    private void RememberSolutionPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            lock (_gate) { _state.LastSolutionPath = path; }
    }

    private void AttachPendingBindingNotice(JsonObject response)
    {
        string? notice;
        lock (_gate)
        {
            notice = _state.PendingBindingNotice;
            _state.PendingBindingNotice = null;
        }

        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        response["BindingNotice"] = notice;
        response["Binding"] = InstanceToJson(CurrentInstance ?? throw new InvalidOperationException("Current instance should exist when attaching a binding notice."));
    }

    private async Task<JsonObject> RetryImplicitBindingCancellationAsync(
        JsonObject response,
        string command,
        string args,
        bool ignoreSolutionHint,
        ToolTimeoutProfile timeoutProfile)
    {
        bool shouldRetry;
        lock (_gate)
        {
            shouldRetry = !string.IsNullOrWhiteSpace(_state.PendingBindingNotice)
                && IsInterruptedOperationResponse(response);
            if (shouldRetry)
            {
                _state.PendingBindingNotice += " Retried once after the initial command was interrupted.";
            }
        }

        if (!shouldRetry)
        {
            return response;
        }

        return await SendPipeAsync(command, args, ignoreSolutionHint, timeoutProfile).ConfigureAwait(false);
    }

    private static bool IsInterruptedOperationResponse(JsonObject response)
    {
        bool success = response["Success"]?.GetValue<bool>() ?? false;
        if (success)
        {
            return false;
        }

        string? summary = response["Summary"]?.GetValue<string>();
        if (string.Equals(summary, "The operation was canceled.", StringComparison.Ordinal))
        {
            return true;
        }

        string? errorMessage = response["Error"]?["message"]?.GetValue<string>();
        return string.Equals(errorMessage, "Bridge server interrupted: The operation was canceled.", StringComparison.Ordinal);
    }

    // ── JSON helpers ───────────────────────────────────────────────────────────

    private static JsonObject InstanceToJson(BridgeInstance inst) => new()
    {
        ["instanceId"] = inst.InstanceId,
        ["pipeName"] = inst.PipeName,
        ["pid"] = inst.ProcessId,
        ["solutionPath"] = inst.SolutionPath,
        ["solutionName"] = inst.SolutionName,
        ["label"] = inst.Label,
        ["source"] = inst.Source,
    };

    private static JsonObject SelectorToJson(BridgeInstanceSelector sel) => new()
    {
        ["instanceId"] = sel.InstanceId,
        ["pid"] = sel.ProcessId,
        ["pipeName"] = sel.PipeName,
        ["solutionHint"] = sel.SolutionHint,
    };

    // ── Arg parsing ────────────────────────────────────────────────────────────

    private static BridgeInstanceSelector ParseSelector(JsonObject? args) => new()
    {
        InstanceId = GetStr(args, "instance_id") ?? GetStr(args, "instance"),
        ProcessId = args?["pid"]?.GetValue<int?>(),
        PipeName = GetStr(args, "pipe_name") ?? GetStr(args, "pipe"),
        SolutionHint = GetStr(args, "solution") ?? GetStr(args, "solution_hint") ?? GetStr(args, "sln"),
    };

    private static string? GetStr(JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DiscoveryMode ResolveMode(string[] args)
    {
        string? raw = GetArgValue(args, "discovery-mode");
        return raw?.ToLowerInvariant() switch
        {
            "memory-first" => DiscoveryMode.MemoryFirst,
            "json-only" => DiscoveryMode.JsonOnly,
            "hybrid" => DiscoveryMode.Hybrid,
            _ => DiscoveryMode.MemoryFirst,
        };
    }

    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        string? raw = GetArgValue(args, name);
        return raw is not null && int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static int? GetOptionalIntArg(string[] args, string name)
    {
        string? raw = GetArgValue(args, name);
        return raw is not null && int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : null;
    }

    private int GetCommandTimeoutMs(ToolTimeoutProfile timeoutProfile)
    {
        return _timeoutOverrideMs ?? timeoutProfile switch
        {
            ToolTimeoutProfile.Fast => FastTimeoutMs,
            ToolTimeoutProfile.Interactive => InteractiveTimeoutMs,
            ToolTimeoutProfile.Heavy => HeavyTimeoutMs,
            _ => InteractiveTimeoutMs,
        };
    }

    private int GetPipeGateTimeoutMs(ToolTimeoutProfile timeoutProfile)
    {
        int pipeGateTimeoutMs = timeoutProfile switch
        {
            ToolTimeoutProfile.Fast => FastPipeGateTimeoutMs,
            ToolTimeoutProfile.Interactive => InteractivePipeGateTimeoutMs,
            ToolTimeoutProfile.Heavy => HeavyPipeGateTimeoutMs,
            _ => InteractivePipeGateTimeoutMs,
        };

        return _timeoutOverrideMs is int timeoutOverrideMs
            ? Math.Min(pipeGateTimeoutMs, timeoutOverrideMs)
            : pipeGateTimeoutMs;
    }

    private static bool ShouldRetry(ToolTimeoutProfile timeoutProfile)
        => timeoutProfile != ToolTimeoutProfile.Fast;

    private static ToolTimeoutProfile SelectTimeoutProfile(string command)
    {
        return command switch
        {
            "ready" or
            "build" or
            "rebuild" or
            "build-solution" or
            "rebuild-solution" or
            "build-errors" or
            "find-references" or
            "count-references" or
            "call-hierarchy" or
            "smart-context" or
            "open-solution" or
            "create-solution" => ToolTimeoutProfile.Heavy,

            "errors" or
            "warnings" or
            "diagnostics-snapshot" or
            "apply-diff" or
            "write-file" or
            "open-document" or
            "close-file" or
            "close-document" or
            "close-others" or
            "save-document" or
            "reload-document" or
            "activate-document" or
            "list-documents" or
            "list-tabs" or
            "list-windows" or
            "activate-window" or
            "execute-command" or
            "format-document" or
            "quick-info" or
            "peek-definition" or
            "goto-definition" or
            "goto-implementation" or
            "set-build-configuration" or
            "build-configurations" => ToolTimeoutProfile.Interactive,

            _ => ToolTimeoutProfile.Fast,
        };
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], $"--{name}", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
