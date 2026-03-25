using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition ApplyDiff(JsonObject parameterSchema)
        => CreateMutatingTool(
            "apply_diff",
            "documents",
            "Patch code through the live editor.",
            "Patch code through the live editor. PREFER editor patch format (*** Begin Patch / *** Update File / *** End Patch) — matches by context lines, tolerates prior edits, and works reliably even after line number shifts. Use unified diff (--- +++ / @@) only when adding or deleting entire files. Changed files open automatically.",
            parameterSchema,
            bridgeCommand: "apply-diff",
            title: "Apply Editor Patch",
            aliases: ["apply_patch", "patch_file", "patch_code"],
            tags: ["edit", "patch", "diff", "code", "file"],
            destructive: true);

    public static ToolDefinition WriteFile(JsonObject parameterSchema)
        => CreateMutatingTool(
            "write_file",
            "documents",
            "Write one file through the editor.",
            "Write or overwrite a file through the live editor. Prefer apply_diff for targeted edits, and use this when a new file or large replacement makes patching impractical.",
            parameterSchema,
            bridgeCommand: "write-file",
            title: "Write File",
            aliases: ["create_file", "overwrite_file", "replace_file"],
            tags: ["edit", "write", "file", "create", "replace"],
            destructive: true);

    public static ToolDefinition ReadFile(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file",
            "search",
            "Read one file slice.",
            "Take one code slice from a file. Use search_symbols, find_text, file_outline, or peek_definition first to narrow what you need. Use start_line/end_line for a range, or line with context_before/context_after for an anchor. For multiple slices use read_file_batch.",
            parameterSchema,
            bridgeCommand: "document-slice",
            title: "Read File Slice",
            aliases: ["read_code", "read_source", "open_file_slice"],
            tags: ["code", NavigationTag, "read", "file", "slice"]);

    public static ToolDefinition ReadFileBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file_batch",
            "search",
            "Read several file slices.",
            "Take multiple code slices in one bridge request. Use search_symbols, find_text, file_outline, or peek_definition first, then use this instead of repeated read_file calls when you need several slices.",
            parameterSchema,
            bridgeCommand: "document-slices",
            title: "Read File Slices",
            aliases: ["read_code_batch", "read_source_batch", "open_file_slices"],
            tags: ["code", NavigationTag, "read", "file", "slice", "batch"]);
}
