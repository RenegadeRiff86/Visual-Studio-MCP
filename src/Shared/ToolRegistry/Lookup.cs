namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private static Dictionary<string, ToolDefinition> BuildLookup(IEnumerable<ToolDefinition> tools)
    {
        Dictionary<string, ToolDefinition> lookup = new(StringComparer.Ordinal);
        foreach (ToolDefinition tool in tools)
        {
            AddLookupEntry(lookup, tool.Name, tool);
            foreach (string alias in tool.Aliases)
                AddLookupEntry(lookup, alias, tool);
        }

        return lookup;
    }

    private static void AddLookupEntry(Dictionary<string, ToolDefinition> lookup, string key, ToolDefinition tool)
    {
        if (lookup.TryGetValue(key, out ToolDefinition? existing))
        {
            throw new InvalidOperationException(
                $"Duplicate tool lookup key '{key}' for '{existing.Name}' and '{tool.Name}'.");
        }

        lookup[key] = tool;
    }
}
