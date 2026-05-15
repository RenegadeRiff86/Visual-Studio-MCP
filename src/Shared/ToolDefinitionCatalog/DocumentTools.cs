using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition ApplyDiff(JsonObject parameterSchema)
        => CreateMutatingTool(
            "apply_diff",
            "documents",
            "Edit a file through VS — requires a handle (h:N or f:N) as 'file'; full paths are rejected.",
            "Bridge catalog tool — must be called through call_tool: " +
            "call_tool({\"name\":\"apply_diff\",\"arguments\":{\"file\":\"h:1\",\"old_content\":\"exact old text\",\"new_content\":\"replacement\"}}) " +
            "REQUIRED WORKFLOW — the 'file' argument MUST be a handle (e.g. h:3, f:1, e:2); full paths are rejected. " +
            "Get a handle first: (1) find_text or search_symbols → h: handle, (2) find_files or glob → f: handle, " +
            "(3) errors/warnings/messages → e:/w:/m: handle, (4) read_file with full path → registers and returns f: handle. " +
            "Then read_file(handle) to get the exact current text, copy that text verbatim as old_content, write your replacement as new_content, and call apply_diff. " +
            "For a single edit use file + old_content + new_content. " +
            "For multiple edits in one call use the edits array — each element is { file, old_content, new_content } and each edit runs as its own command instance: " +
            "call_tool({\"name\":\"apply_diff\",\"arguments\":{\"edits\":[{\"file\":\"h:1\",\"old_content\":\"...\",\"new_content\":\"...\"},{\"file\":\"h:2\",\"old_content\":\"...\",\"new_content\":\"...\"}]}}) " +
            "For multi-file or structural changes only (add/move/delete files), use the diff argument with the *** Begin Patch format. " +
            "NEVER use Claude's built-in Edit or Write tools for files open in VS; always use this bridge tool instead.",
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
            "Write or overwrite a file through the live editor. This REPLACES the entire file contents; it does not append or " +
            "preserve omitted text. Prefer apply_diff for targeted edits, and use write_file only when creating a new file or " +
            "intentionally replacing the whole file with complete content. " +
            "The file argument accepts a handle (f:N from find_files or glob, h:N from find_text or search_symbols) in place of a full path.",
            parameterSchema,
            bridgeCommand: "write-file",
            title: "Write File",
            aliases: ["write", "create", "create_file", "overwrite_file", "replace_file", "write_new_file", "write-file"],
            tags: ["edit", "write", "file", "create", "overwrite", "replace"],
            destructive: true);

    public static ToolDefinition ReadFile(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file",
            "search",
            "Read one slice from one file — always a windowed range, never the full file.",
            "Read ONE slice from ONE file. There is no full-file mode — the response is always a windowed range. " +
            "If a previous bridge result includes a handle such as h:2 or f:1, pass that handle as file instead of copying the full path. " +
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
            "Each range file may be a bridge handle such as h:2 or f:1; use handles from prior results instead of copying full paths. " +
            "Each item must include file plus one of: (a) start_line + end_line for a fixed range, or (b) line + context_before + context_after for an anchor-based slice. " +
            "Use this whenever you need more than one slice, whether from the same file or different files — do not loop read_file.",
            parameterSchema,
            bridgeCommand: "document-slice",
            title: "Read File Slices",
            aliases: ["read_many_files", "read_many", "multi_read", "read_multiple_files", "read_code_batch", "read_source_batch", "open_file_slices"],
            tags: ["code", NavigationTag, "read", "file", "slice", "batch"]);
}
