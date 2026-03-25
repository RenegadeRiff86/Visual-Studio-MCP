using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace VsIdeBridgeService.SystemTools;

internal static class GlobTool
{
    public static Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string pattern = args?["pattern"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pattern))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'pattern'.");

        string root = args?["path"]?.GetValue<string>()
            ?? ServiceToolPaths.ResolveSolutionDirectory(bridge);
        int max = args?["max"]?.GetValue<int?>() ?? 200;

        Matcher matcher = new();
        matcher.AddInclude(pattern);

        DirectoryInfoWrapper dirWrapper = new(new System.IO.DirectoryInfo(root));
        PatternMatchingResult result = matcher.Execute(dirWrapper);

        List<string> files = [];
        bool truncated = false;

        foreach (FilePatternMatch match in result.Files)
        {
            if (files.Count >= max)
            {
                truncated = true;
                break;
            }
            files.Add(match.Path.Replace('\\', '/'));
        }

        JsonObject payload = new()
        {
            ["count"] = files.Count,
            ["truncated"] = truncated,
            ["root"] = root,
            ["files"] = new JsonArray(files.Select(f => JsonValue.Create(f)).ToArray<JsonNode?>()),
        };

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() },
            },
            ["isError"] = false,
            ["structuredContent"] = payload,
        });
    }
}
