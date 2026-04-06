namespace VsIdeBridge.Shared;

public sealed class ToolCategoryDefinition(string name, string summary, string description)
{
    public string Name { get; } = name;

    public string Summary { get; } = summary;

    public string Description { get; } = description;
}
