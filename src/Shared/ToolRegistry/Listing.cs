using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    public IReadOnlyList<ToolDefinition> GetByCategory(string category)
    {
        return
        [..
            _all
                .Where(tool => string.Equals(tool.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
        ];
    }
}
