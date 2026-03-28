# VS IDE Bridge Repo Instructions

This file is for LLMs and coding agents.

- For product and operator docs, read [README.md](README.md).
- For current runtime gaps, read [BUGS.md](BUGS.md).
- For code ownership and extraction rules, read [ARCHITECTURE_HIERARCHY.md](ARCHITECTURE_HIERARCHY.md) before adding files or moving logic.

## Runtime Truth

- `VsIdeBridgeService` is the primary installed runtime.
- Bridge tools require a live Visual Studio instance and route through the VS named pipe.
- Service-native tools run in the Windows service and can work without Visual Studio.
- `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe mcp-server` starts a separate foreground MCP host process.
- That stdio host does not attach to the already-running SCM-managed Windows service.
- The optional HTTP listener is the only verified in-process reuse path for the existing Windows service.
- Do not assume the installed CLI is a working attach client unless you verify the installed behavior first.

## Source Of Truth

- Treat the installed bridge catalog as authoritative for runtime tool names and schemas.
- Treat repo source as authoritative for implementation intent.
- If installed behavior and repo docs disagree, call out the mismatch explicitly.

## Session Start

1. Refresh the live tool surface with `list_tool_categories`, `list_tools`, or `tool_help`.
2. Check `list_instances` and bind to the intended Visual Studio instance.
3. Use `state`, `bridge_health`, and `diagnostics_snapshot` before changing code.
4. Read [BUGS.md](BUGS.md) before trusting `vs_open`, client-hosting assumptions, or older setup instructions.

## Editing Rules

- For files inside the active Visual Studio solution, prefer bridge-native editing.
- Use `apply_diff` first for targeted edits.
- Re-read the file or diagnostics after edits land.
- If bridge editing fails, treat that as the bug to fix. Do not silently fall back to shell or direct filesystem writes for in-solution files.
- Preserve existing formatting and line-ending style for VS-managed files.

## Tool Priorities

- Read code: `read_file`, `read_file_batch`, `file_outline`, `search_symbols`, `find_text`
- Diagnose state: `diagnostics_snapshot`, `errors`, `warnings`, `state`, `bridge_health`
- Edit code: `apply_diff`, then `reload_document` and diagnostics checks when needed
- Build: `build`, `build_errors`, `set_build_configuration`
- Runtime discovery: `list_instances`, `bind_instance`, `bind_solution`, `wait_for_instance`
- Last resort only: `shell_exec`

## Known Behavior To Remember

- `vs_open` is currently unsafe; treat it as a failed/disabled path unless the user is explicitly helping test it.
- Project-local `.mcp.json` should not auto-launch a second foreground host by default.
- Older docs or sessions may refer to the CLI as an MCP host. Verify before repeating that claim.

## Key Files

- [README.md](README.md) - main product and operator guide
- [BUGS.md](BUGS.md) - current runtime gaps and workarounds
- [ARCHITECTURE_HIERARCHY.md](ARCHITECTURE_HIERARCHY.md) - ownership rules
- [ROADMAP.md](ROADMAP.md) - structural cleanup targets
- [SERVICE_CONVERSION_TRACKER.md](SERVICE_CONVERSION_TRACKER.md) - historical conversion record, not current runtime truth
