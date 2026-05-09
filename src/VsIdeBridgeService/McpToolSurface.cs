using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal sealed class McpToolSurface
{
    public const string EnvironmentVariableName = "VS_IDE_BRIDGE_TOOL_SURFACE";

    private static readonly string[] LazyToolNames =
    [
        "call_tool",
        "recommend_tools",
        "tool_help",
        "list_tools",
        "list_tool_categories",
        "bridge_health",
        "vs_state",
    ];

    private McpToolSurface(string name, IReadOnlyList<string>? visibleToolNames)
    {
        Name = name;
        VisibleToolNames = visibleToolNames;
    }

    public static McpToolSurface Lazy { get; } = new("lazy", LazyToolNames);

    public static McpToolSurface Full { get; } = new("full", null);

    public string Name { get; }

    public IReadOnlyList<string>? VisibleToolNames { get; }

    public bool IsFull => VisibleToolNames is null;

    public static McpToolSurface FromArgs(string[] args)
    {
        string? raw = GetArgValue(args, "tool-surface")
            ?? Environment.GetEnvironmentVariable(EnvironmentVariableName);

        return raw?.Trim().ToLowerInvariant() switch
        {
            "full" or "all" => Full,
            "lazy" or "minimal" or "compact" => Lazy,
            _ => Lazy,
        };
    }

    public JsonObject ToJsonObject() => new()
    {
        ["mode"] = Name,
        ["visibleTools"] = VisibleToolNames is null
            ? null
            : new JsonArray([.. VisibleToolNames.Select(static name => JsonValue.Create(name))]),
    };

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], $"--{name}", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
