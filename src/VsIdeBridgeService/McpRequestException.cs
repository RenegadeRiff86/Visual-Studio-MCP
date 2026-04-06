using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal sealed class McpRequestException(JsonNode? id, int code, string message) : Exception(message)
{
    public JsonNode? Id { get; } = id;
    public int Code { get; } = code;
}
