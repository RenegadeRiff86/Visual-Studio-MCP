using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition ApplyDiff(JsonObject parameterSchema)
        => CreateMutatingTool(
            "apply_diff",
            "documents",
            "Edit file(s) through VS — file + old_content + new_content for a targeted change, or diff for multi-file patches.",
            "Edit a file through the live VS editor. Preferred form: pass file + old_content + new_content. " +
            "Read the target section with read_file first, then supply that exact text as old_content and your replacement as new_content. " +
            "Open files reload automatically before matching, so content is always current — no stale-buffer problem. " +
            "NEVER use Claude's built-in Edit or Write tools for files open in VS; always use this bridge tool instead. " +
            "For multi-file or structural changes (add/move/delete files), use the diff patch form: *** Begin Patch / *** Update File / @@ / -old / +new / *** End Patch. " +
            "Supports *** Add File, *** Delete File, and *** Update File blocks. Multiple files apply atomically.",
            parameterSchema,
            bridgeCommand: "apply-diff",
            title: "Apply Diff",
            aliases: ["edit", "edit_file", "apply_patch", "patch_file", "patch_code", "replace", "replace_text", "multi_edit", "multiedit"],
            tags: ["edit", "patch", "diff", "replace", "modify", "code", "file"],
            destructive: true);

    public static ToolDefinition WriteFile(JsonObject parameterSchema)
        => CreateMutatingTool(
            "write_file",
            "documents",
            "Replace one file through the editor.",
            "Write or overwrite a file through the live editor. This REPLACES the entire file contents; it does not append or preserve omitted text. Prefer apply_diff for targeted edits, and use write_file only when creating a new file or intentionally replacing the whole file with complete content.",
            parameterSchema,
            bridgeCommand: "write-file",
            title: "Write File",
            aliases: ["write", "create", "create_file", "overwrite_file", "replace_file", "write_new_file"],
            tags: ["edit", "write", "file", "create", "overwrite", "replace"],
            destructive: true);

    public static ToolDefinition ReadFile(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file",
            "search",
            "Read one file slice.",
            "Take one code slice from a file. Use search_symbols, find_text, file_outline, or peek_definition first to narrow what you need. For in-solution edits, use this before apply_diff so the patch targets the current code. Use start_line/end_line for a range, or line with context_before/context_after for an anchor. For multiple slices use read_file_batch.",
            parameterSchema,
            bridgeCommand: "document-slice",
            title: "Read File Slice",
            aliases: ["read", "view_file", "cat", "read_code", "read_source", "open_file_slice"],
            tags: ["code", NavigationTag, "read", "file", "slice"]);

    public static ToolDefinition ReadFileBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file_batch",
            "search",
            "Read several file slices.",
            "Take multiple code slices in one bridge request. Each range item must include file plus one of: (a) start_line + end_line for a fixed range, or (b) line + context_before + context_after for an anchor-based slice. Use search_symbols, find_text, file_outline, or peek_definition first to get line numbers, then call this instead of repeated read_file calls when you need several slices before apply_diff or a larger refactor.",
            parameterSchema,
            bridgeCommand: "document-slices",
            title: "Read File Slices",
            aliases: ["read_many_files", "read_many", "multi_read", "read_multiple_files", "read_code_batch", "read_source_batch", "open_file_slices"],
            tags: ["code", NavigationTag, "read", "file", "slice", "batch"]);
}
