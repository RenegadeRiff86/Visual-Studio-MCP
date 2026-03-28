---
name: vs-bridge-workflow
description: Use when working inside an active Visual Studio solution through VS IDE Bridge tools, especially for binding, code discovery, apply_diff-based edits, diagnostics, builds, and debugger-driven investigation.
---

# VS Bridge Workflow

Use this skill for files and tasks that belong to the active Visual Studio solution and should stay on the VS IDE Bridge path.

## Tool Map

Use these bridge tools together instead of treating `apply_diff` in isolation.

- Session setup:
  - `list_instances`
  - `bind_solution` or `bind_instance`
  - `bridge_health`
  - `diagnostics_snapshot`
- Code discovery:
  - `find_files`
  - `find_text`
  - `find_text_batch`
  - `search_symbols`
  - `smart_context`
  - `read_file`
  - `read_file_batch`
  - `file_outline`
  - `symbol_info`
  - `peek_definition`
- Editing:
  - `apply_diff` as the default targeted edit tool
  - `write_file` only for true full-file replacement
  - `reload_document` after non-patch editor writes when needed
  - `save_document` if the workflow needs an explicit save
- Diagnostics:
  - `errors`
  - `warnings`
  - `diagnostics_snapshot`
  - `build`
  - `build_solution`
  - `rebuild`
  - `rebuild_solution`
- Project and solution context:
  - `list_projects`
  - `query_project_items`
  - `query_project_references`
  - `query_project_outputs`
  - `set_startup_project`
- Debugging:
  - `debug_start`
  - `debug_break`
  - `debug_continue`
  - `debug_step_over`
  - `debug_step_into`
  - `debug_step_out`
  - `debug_stop`
  - `debug_stack`
  - `debug_locals`
  - `debug_watch`
  - `debug_threads`
  - `set_breakpoint`
  - `list_breakpoints`
  - `remove_breakpoint`
- Tool discovery:
  - `list_tool_categories`
  - `list_tools`
  - `list_tools_by_category`
  - `tool_help`
  - `recommend_tools`

## Workflow

1. Confirm the file or task is in the active Visual Studio solution.
2. Refresh context with `diagnostics_snapshot` and the smallest read/search tools that answer the question.
3. Prefer bridge-native editing. Use MCP `apply_diff` first.
4. Keep patches narrow. Edit only the lines that need to change.
5. Re-read the file or diagnostics after the patch lands.
6. If the bridge edit path fails, treat that as the bug to fix instead of falling back to shell or direct filesystem writes.

## Default Behavior

- Assume `apply_diff` is the right edit tool for any targeted in-solution change.
- Do not choose direct writes just because they are faster or easier to generate.
- If the model is about to edit an in-solution file without `apply_diff`, stop and reconsider the bridge path first.
- When in doubt, prefer a smaller `apply_diff` patch and iterate.

## Patch Rules

- Prefer editor patch format over whole-file rewrites.
- Keep one file per patch when practical.
- Preserve the file's existing line endings and formatting style.
- Avoid touching unrelated whitespace or reflowing the whole file.
- If a patch becomes large or ambiguous, split it into smaller sequential patches.
- Use `write_file` only when the bridge path specifically requires a full replacement and the file is still being edited through the bridge.
- Avoid non-bridge writes for active-solution files because they can desync Visual Studio state and hide diagnostics until reload.

## Quick Recipe

Use this checklist:

- Identify the smallest editable region.
- Build a minimal `*** Begin Patch` block.
- Apply through bridge `apply_diff`.
- Reload or re-read the file if needed.
- Run `errors` and `warnings` after meaningful edits.
- Use debug tools when behavior is unclear instead of guessing from static reads.

## Escalation Rule

If the file is in the active solution, do not switch to shell edits or direct file writes just because patching is inconvenient. Only leave the bridge path when the user explicitly approves it or the task is clearly outside the solution.
