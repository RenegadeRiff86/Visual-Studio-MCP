namespace VsIdeBridge.Tooling.Handles;

/// <summary>
/// Handle produced by <c>find_text</c> or <c>search_symbols</c> (prefix "h:").
/// Carries the match location and a content preview.
/// </summary>
public sealed class SearchHitHandle : HandleEntry
{
    public SearchHitHandle(
        string  id,
        string  path,
        int?    line,
        int?    column,
        string? preview = null)
        : base(id, HandleKind.SearchHit, path)
    {
        Line    = line;
        Column  = column;
        Preview = preview;
    }

    public int?    Line    { get; }
    public int?    Column  { get; }
    public string? Preview { get; }
}
