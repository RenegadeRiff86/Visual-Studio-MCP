using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Infrastructure;

internal sealed class CommandExecutionResult(string summary, JToken? data = null, JArray? warnings = null)
{
    public string Summary { get; } = summary;

    public JToken Data { get; } = data ?? new JObject();

    public JArray Warnings { get; } = warnings ?? [];
}

internal sealed record CommandTimeoutDetails(
    [property: JsonProperty("timeoutMs")] int TimeoutMs,
    [property: JsonProperty("durationMs")] long DurationMs,
    [property: JsonProperty("reason")] string Reason);

internal sealed record CommandTimeoutError(
    [property: JsonProperty("code")] string Code,
    [property: JsonProperty("message")] string Message,
    [property: JsonProperty("details")] object? Details);

internal sealed record CommandEnvelope
{
    public int SchemaVersion { get; set; }

    public string Command { get; set; } = string.Empty;

    public string? RequestId { get; set; }

    public bool Success { get; set; }

    public string StartedAtUtc { get; set; } = string.Empty;

    public string FinishedAtUtc { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public JArray Warnings { get; set; } = [];

    public object? Error { get; set; }

    public JToken Data { get; set; } = new JObject();
}
