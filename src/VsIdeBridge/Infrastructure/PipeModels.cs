using Newtonsoft.Json;

namespace VsIdeBridge.Infrastructure;

internal sealed class PipeRequest
{
    [JsonProperty("id")]      public string? Id { get; set; }
    [JsonProperty("command")] public string Command { get; set; } = "";
    [JsonProperty("args")]    public string? Args { get; set; }
}
