using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal sealed class ToolExecutionRegistry
{
    private readonly ToolEntry[] _all;
    private readonly Dictionary<string, ToolEntry> _byLookupName;
    private readonly ToolRegistry _definitions;

    public ToolExecutionRegistry(IEnumerable<ToolEntry> entries)
    {
        _all = [.. entries];
        _byLookupName = BuildLookup(_all);
        _definitions = new ToolRegistry(_all.Select(entry => entry.Definition));
    }

    public IReadOnlyList<ToolEntry> All => _all;

    public ToolRegistry Definitions => _definitions;

    public bool TryGet(string name, [NotNullWhen(true)] out ToolEntry? entry)
        => _byLookupName.TryGetValue(name, out entry);

    public bool TryGetDefinition(string name, [NotNullWhen(true)] out ToolDefinition? definition)
        => _definitions.TryGet(name, out definition);

    public JsonArray BuildToolsList()
    {
        JsonArray result = [];
        foreach (ToolEntry entry in _all)
            result.Add(entry.Definition.BuildToolObject());

        return result;
    }

    public async Task<JsonNode> DispatchAsync(JsonNode? id, string name, JsonObject? args, BridgeConnection bridge)
    {
        if (!_byLookupName.TryGetValue(name, out ToolEntry? entry))
        {
            // Return a soft error so the model sees helpful guidance instead of a hard JSON-RPC failure.
            // This dramatically reduces hallucination spirals when the model guesses wrong tool names.
            McpServerLog.Write($"tool unknown name={name}");
            return BuildUnknownToolResult(name);
        }

        try
        {
            return await entry.Handler(id, args, bridge).ConfigureAwait(false);
        }
        catch (McpRequestException ex) when (ex.Code == McpErrorCodes.InvalidParams)
        {
            // Convert InvalidParams into a content-level isError result so the model can
            // self-correct using the embedded input schema rather than receiving a JSON-RPC error.
            McpServerLog.Write($"tool invalid-params tool={name} message={ex.Message}");
            return BuildInvalidParamsResult(ex.Message, entry);
        }
    }

    private static JsonObject BuildInvalidParamsResult(string message, ToolEntry entry)
    {
        string schema = entry.Definition.ParameterSchema?.ToJsonString() ?? "{}";
        string text =
            $"{message}\n\n" +
            $"Input schema for '{entry.Name}':\n{schema}\n\n" +
            $"Call tool_help with name=\"{entry.Name}\" for full documentation.";
        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = true,
        };
    }

    private JsonObject BuildUnknownToolResult(string attemptedName)
    {
        string? closest = FindClosestToolName(attemptedName);
        string suggestion = closest != null
            ? $"You called '{attemptedName}' but the closest matching tool is '{closest}'.\n\n"
            : "";

        string text =
            $"{suggestion}Unknown tool '{attemptedName}'.\n\n" +
            "To discover the correct tool name and see its exact schema + usage, call:\n\n" +
            "tool_help with name=\"shell_exec\"   ← replace with the real tool name you want\n\n" +
            "Example of the correct JSON call:\n" +
            "{\n" +
            "  \"name\": \"tool_help\",\n" +
            "  \"arguments\": { \"name\": \"shell_exec\" }\n" +
            "}\n\n" +
            "After calling tool_help you will get the full documentation and parameter schema.\n" +
            "Then use the exact tool name it confirms.";

        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = true,
        };
    }

    private string? FindClosestToolName(string attempted)
    {
        if (string.IsNullOrWhiteSpace(attempted))
            return null;

        // Common obvious mistakes first (fast path)
        if (attempted.Equals("shell", StringComparison.OrdinalIgnoreCase))
            return "shell_exec";

        // Simple similarity: prefer names that contain the guess or start with it
        List<string> candidates = _byLookupName.Keys
            .Where(k => k.Contains(attempted, StringComparison.OrdinalIgnoreCase) ||
                        attempted.Contains(k, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => Math.Abs(k.Length - attempted.Length))
            .ThenBy(k => k)
            .Take(3)
            .ToList();

        if (candidates.Count > 0)
            return candidates[0];

        // Fallback: pick the first tool that shares any characters (very loose)
        return _byLookupName.Keys
            .OrderBy(k => LevenshteinDistance(k.ToLowerInvariant(), attempted.ToLowerInvariant()))
            .FirstOrDefault();
    }

    // Minimal Levenshtein distance implementation (no external dependencies)
    private static int LevenshteinDistance(string s, string target)
    {
        if (string.IsNullOrEmpty(s)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return s.Length;

        int[,] d = new int[s.Length + 1, target.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= target.Length; j++) d[0, j] = j;

        for (int i = 1; i <= s.Length; i++)
            for (int j = 1; j <= target.Length; j++)
            {
                int cost = s[i - 1] == target[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        return d[s.Length, target.Length];
    }

    private static Dictionary<string, ToolEntry> BuildLookup(IEnumerable<ToolEntry> entries)
    {
        ToolEntry[] materialized = entries as ToolEntry[] ?? [.. entries];
        Dictionary<string, ToolEntry> lookup = new(StringComparer.Ordinal);

        // Pass 1: canonical names + explicit aliases must be unique. A
        // collision here is a real authoring bug in the registrars and
        // should fail loudly so it gets fixed before shipping.
        foreach (ToolEntry entry in materialized)
        {
            AddLookupEntry(lookup, entry.Name, entry);
            foreach (string alias in entry.Definition.Aliases)
                AddLookupEntry(lookup, alias, entry);
        }

        // Pass 2: BridgeCommand is a wire-level VS pipe address, not a
        // unique MCP dispatch key. Convenience wrapper tools may target the
        // same bridge command as a lower-level tool. Add the shortcut only
        // when it does not conflict with another tool's name, alias, or
        // earlier bridge-command shortcut. Silently dropping collisions keeps
        // the tool catalog usable instead of crashing the entire MCP server
        // and starving every client of every tool.
        foreach (ToolEntry entry in materialized)
        {
            string? bridgeCommand = entry.Definition.BridgeCommand;
            if (string.IsNullOrWhiteSpace(bridgeCommand))
                continue;
            if (lookup.ContainsKey(bridgeCommand))
                continue;
            lookup[bridgeCommand] = entry;
        }

        return lookup;
    }

    private static void AddLookupEntry(
        Dictionary<string, ToolEntry> lookup,
        string key,
        ToolEntry entry)
    {
        if (lookup.TryGetValue(key, out ToolEntry? existing))
        {
            if (ReferenceEquals(existing, entry))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Duplicate MCP tool lookup key '{key}' for '{existing.Name}' and '{entry.Name}'.");
        }

        lookup[key] = entry;
    }
}