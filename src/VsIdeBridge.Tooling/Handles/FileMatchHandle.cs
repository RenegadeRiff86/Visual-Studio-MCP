namespace VsIdeBridge.Tooling.Handles;

/// <summary>
/// Handle produced by <c>find_files</c>, <c>glob</c>, or <c>read_file</c> (prefix "f:").
/// Represents a located file with optional project membership.
/// </summary>
public sealed class FileMatchHandle : HandleEntry
{
    public FileMatchHandle(
        string  id,
        string  path,
        string  name,
        string? project = null)
        : base(id, HandleKind.FileMatch, path)
    {
        Name    = name;
        Project = project;
    }

    /// <summary>File name without directory, e.g. "HandleService.cs".</summary>
    public string  Name    { get; }

    /// <summary>Project the file belongs to, or <see langword="null"/> if not in a project.</summary>
    public string? Project { get; }
}
