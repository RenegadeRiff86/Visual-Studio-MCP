using System.Text.Json.Nodes;

namespace VsIdeBridgeService.Diagnostics;

internal sealed class DocumentDiagnosticsSnapshot
{
    public string Status { get; init; } = "idle";

    public string? Reason { get; init; }

    public string? LastError { get; init; }

    public string? LastQueuedUtc { get; init; }

    public string? LastStartedUtc { get; init; }

    public string? LastCompletedUtc { get; init; }

    public JsonObject? Errors { get; init; }

    public JsonObject? Warnings { get; init; }

    public JsonObject ToJson()
    {
        JsonObject json = new()
        {
            ["status"] = Status,
        };

        if (!string.IsNullOrWhiteSpace(Reason))
        {
            json["reason"] = Reason;
        }

        if (!string.IsNullOrWhiteSpace(LastError))
        {
            json["lastError"] = LastError;
        }

        if (!string.IsNullOrWhiteSpace(LastQueuedUtc))
        {
            json["lastQueuedUtc"] = LastQueuedUtc;
        }

        if (!string.IsNullOrWhiteSpace(LastStartedUtc))
        {
            json["lastStartedUtc"] = LastStartedUtc;
        }

        if (!string.IsNullOrWhiteSpace(LastCompletedUtc))
        {
            json["lastCompletedUtc"] = LastCompletedUtc;
        }

        if (Errors is not null)
        {
            json["errors"] = Errors.DeepClone();
        }

        if (Warnings is not null)
        {
            json["warnings"] = Warnings.DeepClone();
        }

        return json;
    }
}
