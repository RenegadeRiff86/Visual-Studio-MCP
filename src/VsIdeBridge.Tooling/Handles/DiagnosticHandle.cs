namespace VsIdeBridge.Tooling.Handles;

/// <summary>
/// Handle produced by <c>errors</c>, <c>warnings</c>, or <c>messages</c>.
/// Carries the full diagnostic context so consumers can act without re-querying.
/// </summary>
public sealed class DiagnosticHandle : HandleEntry
{
    public DiagnosticHandle(
        string   id,
        HandleKind kind,
        string   path,
        int?     line,
        int?     column,
        string?  severity = null,
        string?  code     = null,
        string?  message  = null,
        string?  project  = null)
        : base(id, kind, path)
    {
        Line     = line;
        Column   = column;
        Severity = severity;
        Code     = code;
        Message  = message;
        Project  = project;
    }

    public int?    Line     { get; }
    public int?    Column   { get; }
    public string? Severity { get; }
    public string? Code     { get; }
    public string? Message  { get; }
    public string? Project  { get; }
}
