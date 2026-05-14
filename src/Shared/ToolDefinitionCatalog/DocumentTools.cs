using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition ApplyDiff(JsonObject parameterSchema)
        => CreateMutatingTool(
            "apply_diff",
            "documents",
            "Edit a file through VS — call via call_tool with file + old_content + new_content.",
            "Bridge catalog tool — must be called through call_tool: " +
            "call_tool({\"name\":\"apply_diff\",\"arguments\":{\"file\":\"C:/path/File.cs\",\"old_content\":\"exact old text\",\"new_content\":\"replacement\"}}). " +
            "For a single file change always pass file + old_content + new_content — do NOT use the diff argument for single files. " +
            "Read the target section with read_file first, then supply that exact text as old_content and your replacement as new_content. " +
            "Open files reload automatically before matching — no stale-buffer problem. " +
            "NEVER use Claude's built-in Edit or Write tools for files open in VS; always use this bridge tool instead. " +
            "For multi-file or structural changes only, use the diff argument with the *** Begin Patch patch format.",
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
            "Read one slice from one file — always a windowed range, never the full file.",
            "Read ONE slice from ONE file. There is no full-file mode — the response is always a windowed range. " +
            "Always provide start_line+end_line; omitting them starts at line 1 with a short default window, not the whole file. " +
            "To continue past what you have seen: call again with start_line = (last line shown + 1). " +
            "For multiple slices or multiple files in one round-trip use read_file_batch. " +
            "Use file_outline first to identify the line range you need. " +
            "For in-solution edits use this before apply_diff so the patch targets current content.",
            parameterSchema,
            bridgeCommand: "document-slice",
            title: "Read File Slice",
            aliases: ["read", "view_file", "cat", "read_code", "read_source", "open_file_slice"],
            tags: ["code", NavigationTag, "read", "file", "slice"]);

    public static ToolDefinition ReadFileBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file_batch",
            "search",
            "Read multiple slices across one or more files.",
            "Read multiple slices in one call — each range item specifies its own file, so you can pull slices from different files simultaneously. " +
            "Each item must include file plus one of: (a) start_line + end_line for a fixed range, or (b) line + context_before + context_after for an anchor-based slice. " +
            "Use this whenever you need more than one slice, whether from the same file or different files — do not loop read_file.",
            parameterSchema,
            bridgeCommand: "document-slices",
            title: "Read File Slices",
            aliases: ["read_many_files", "read_many", "multi_read", "read_multiple_files", "read_code_batch", "read_source_batch", "open_file_slices"],
            tags: ["code", NavigationTag, "read", "file", "slice", "batch"]);
}
