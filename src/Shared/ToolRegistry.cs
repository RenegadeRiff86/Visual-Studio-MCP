using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private readonly ToolDefinition[] _all;
    private readonly Dictionary<string, ToolDefinition> _byNameOrAlias;
    private readonly ToolCategoryDefinition[] _categories;
    private readonly string[] _featuredTools;

    public ToolRegistry(
        IEnumerable<ToolDefinition> tools,
        IEnumerable<ToolCategoryDefinition>? categories = null,
        IEnumerable<string>? featuredTools = null)
    {
        _all = tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
        _categories = (categories ?? DefaultCategoryDefinitions)
            .OrderBy(static category => category.Name, StringComparer.Ordinal)
            .ToArray();
        _featuredTools = featuredTools?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? FeaturedToolNames.ToArray();
        _byNameOrAlias = BuildLookup(_all);
    }

    public IReadOnlyList<ToolDefinition> All => _all;

    public IReadOnlyList<ToolCategoryDefinition> Categories => _categories;

    public bool TryGet(string nameOrAlias, [NotNullWhen(true)] out ToolDefinition? tool)
        => _byNameOrAlias.TryGetValue(nameOrAlias, out tool);

    private static string ChooseReason(string current, string candidate)
        => string.IsNullOrWhiteSpace(current) ? candidate : current;

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (string value in values)
        {
            if (text.Contains(value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

}
