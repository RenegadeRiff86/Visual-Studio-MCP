using System.Text.Json.Nodes;

namespace VsIdeBridgeService.Diagnostics;

internal sealed class DocumentDiagnosticsCoordinator
{
    private readonly BridgeConnection _bridge;
    private readonly object _gate = new();

    private Task? _refreshTask;
    private bool _refreshRequested;
    private string _status = "idle";
    private string? _reason;
    private string? _lastError;
    private DateTimeOffset? _lastQueuedUtc;
    private DateTimeOffset? _lastStartedUtc;
    private DateTimeOffset? _lastCompletedUtc;
    private JsonObject? _lastErrors;
    private JsonObject? _lastWarnings;

    public DocumentDiagnosticsCoordinator(BridgeConnection bridge) => _bridge = bridge;

    public JsonObject QueueRefreshAndGetSnapshot(string reason)
    {
        lock (_gate)
        {
            _refreshRequested = true;
            _reason = reason;
            _lastQueuedUtc = DateTimeOffset.UtcNow;

            if (_refreshTask is null || _refreshTask.IsCompleted)
            {
                _status = "queued";
                _refreshTask = Task.Run(RefreshLoopAsync);
            }

            return CreateSnapshotLocked().ToJson();
        }
    }

    public bool TryGetCachedErrors(JsonObject? args, out JsonObject response)
    {
        lock (_gate)
        {
            if (!CanServeCachedDiagnostics(args) || _lastErrors is null)
            {
                response = [];
                return false;
            }

            response = CreateCachedResponseLocked(_lastErrors, "errors");
            return true;
        }
    }

    public bool TryGetCachedWarnings(JsonObject? args, out JsonObject response)
    {
        lock (_gate)
        {
            if (!CanServeCachedDiagnostics(args) || _lastWarnings is null)
            {
                response = [];
                return false;
            }

            response = CreateCachedResponseLocked(_lastWarnings, "warnings");
            return true;
        }
    }

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_refreshRequested)
                {
                    _refreshTask = null;
                    return;
                }

                _refreshRequested = false;
                _status = "running";
                _lastStartedUtc = DateTimeOffset.UtcNow;
                _lastError = null;
            }

            try
            {
                JsonObject errors = await _bridge.SendAsync(
                    null,
                    "errors",
                    "--quick true --wait-for-intellisense false --severity Error")
                    .ConfigureAwait(false);
                JsonObject warnings = await _bridge.SendAsync(
                    null,
                    "warnings",
                    "--quick true --wait-for-intellisense false")
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _lastErrors = errors;
                    _lastWarnings = warnings;
                    _status = "completed";
                    _lastCompletedUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _status = "failed";
                    _lastError = ex.Message;
                    _lastCompletedUtc = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    private static bool CanServeCachedDiagnostics(JsonObject? args)
    {
        if (args is null)
        {
            return true;
        }

        // quick and wait_for_intellisense are timing hints, not content filters.
        // Only bypass cache when content-filtering params are present.
        return args["severity"] is null
            && args["code"] is null
            && args["project"] is null
            && args["path"] is null
            && args["text"] is null
            && args["group_by"] is null;
    }

    private DocumentDiagnosticsSnapshot CreateSnapshotLocked()
    {
        return new DocumentDiagnosticsSnapshot
        {
            Status = _status,
            Reason = _reason,
            LastError = _lastError,
            LastQueuedUtc = FormatUtc(_lastQueuedUtc),
            LastStartedUtc = FormatUtc(_lastStartedUtc),
            LastCompletedUtc = FormatUtc(_lastCompletedUtc),
            Errors = _lastErrors,
            Warnings = _lastWarnings,
        };
    }

    private JsonObject CreateCachedResponseLocked(JsonObject response, string kind)
    {
        JsonObject clone = response.DeepClone().AsObject();
        clone["Cache"] = new JsonObject
        {
            ["source"] = "service-memory",
            ["kind"] = kind,
            ["snapshot"] = CreateSnapshotLocked().ToJson(),
        };
        return clone;
    }

    private static string? FormatUtc(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("O");
    }
}
