namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string FileDesc =
        "Absolute path, solution-relative path, or handle ID returned by bridge results. " +
        "If a row includes a handle (e:<n>, w:<n>, m:<n>, f:<n>, h:<n>), pass the handle directly instead of copying the full path; " +
        "PathResolver resolves it through IdeBridgeRuntime.HandleService.";

    /// <summary>
    /// File argument description for apply_diff — accepts handles (preferred) or plain paths.
    /// </summary>
    private const string ApplyDiffFileDesc =
        "File to edit: a handle (h:N, f:N, e:N, w:N, m:N) from a prior bridge result, " +
        "or a plain path (absolute or solution-relative). " +
        "Prefer a handle — pass the h:/f: handle returned by find_text, find_files, errors, or read_file " +
        "so the bridge resolves the file without ambiguity. " +
        "Plain paths work too: 'CHANGELOG.md', 'src/Foo.cs', or a full absolute path.";

    private const string LineDesc = "1-based line number.";
    private const string ColumnDesc = "1-based column number.";
    private const string ProjectFilterDesc = "Optional project name or path filter.";
    private const string ProjectDesc = "Project name or path.";
}
