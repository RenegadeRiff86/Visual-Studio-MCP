namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string FileDesc =
        "Absolute path, solution-relative path, or handle ID returned by bridge results. " +
        "If a row includes a handle (e:<n>, w:<n>, m:<n>, f:<n>, h:<n>), pass the handle directly instead of copying the full path; " +
        "PathResolver resolves it through IdeBridgeRuntime.HandleService.";

    /// <summary>
    /// File argument description for apply_diff and write_file — handles are required,
    /// full paths are rejected.
    /// </summary>
    private const string ApplyDiffFileDesc =
        "REQUIRED: a handle ID (e.g. h:3 or f:1) returned by a prior bridge result — NOT a full path. " +
        "Full paths are rejected. To get a handle: " +
        "(1) run find_text or search_symbols to get h: handles, " +
        "(2) run find_files or glob to get f: handles, " +
        "(3) run errors, warnings, or messages to get e:/w:/m: handles, or " +
        "(4) run read_file with the full path once — it registers and returns an f: handle you can then pass here.";

    private const string LineDesc = "1-based line number.";
    private const string ColumnDesc = "1-based column number.";
    private const string ProjectFilterDesc = "Optional project name or path filter.";
    private const string ProjectDesc = "Project name or path.";
}
