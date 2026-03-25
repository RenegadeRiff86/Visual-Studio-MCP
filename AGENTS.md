# VS IDE Bridge Repo Instructions

## Architecture Rule — Read Before Touching Code

Before adding, moving, or creating any file or class, read `ARCHITECTURE_HIERARCHY.md`.
It defines exactly where code belongs. If you are unsure which project or folder owns a
piece of logic, use the Decision Test at the bottom of that file before proceeding.
Every new registrar, tool, helper, and class library must satisfy that hierarchy.

## MCP Entry Point

- MCP server name: `vs-ide-bridge`
- Installed CLI: `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe`
- Start with: `mcp-server --tools-only`
- `vs-ide-bridge-pr` is a compatibility alias only — prefer `vs-ide-bridge` when available.

## Primary Host: The Windows Service

`VsIdeBridgeService` is the canonical MCP host. It runs as a Windows service and exposes
all tools. The CLI is a bootstrap shim only — do not use it for product work.

There are two kinds of service tools:
- **Bridge tools** — route through the VS named pipe. Require a live VS instance.
- **Service-native tools** — run entirely in the service process (subprocess or in-memory).
  Work even without a VS instance. Examples: `git_*`, `set_version`, `shell_exec`.

## Start of Session Checklist

1. `vs_open` — open VS **without a solution** (no `solution_hint`) to get an instance ID immediately
2. `list_instances` — grab the instance ID once the pipe appears
3. `open_solution` — open `VsIdeBridge.sln` by absolute path
4. `diagnostics_snapshot` — get current IDE state, errors, and warnings before starting any work
5. `tool_help` — get the schema and examples for any tool before using it

## Tool Priority — Use Named Tools Before shell_exec

Always prefer a named MCP tool over `shell_exec`. Use `recommend_tools` or `tool_help`
if you are unsure which tool fits. `shell_exec` is a last-resort escape hatch for cases
where no named tool exists.

| Task | Preferred tool |
|------|----------------|
| Read code | `read_file`, `read_file_batch` |
| Search code | `find_text`, `find_text_batch`, `search_symbols` |
| Get focused LLM context | `smart_context` |
| Edit code | Native `Edit` / `Write` tools, then `reload_document` + `errors` (hook reminds you) |
| Build | `build`, `build_errors` |
| Check errors/warnings | `errors`, `warnings`, `diagnostics_snapshot` |
| Git operations | `git_status`, `git_commit`, `git_push`, etc. |
| Python env/packages | `python_list_envs`, `python_install_package`, etc. |
| NuGet packages | `nuget_add_package`, `nuget_remove_package` |
| Build installer | `set_build_configuration` config=Release, then `build` (builds solution; ISCC runs automatically) |

## Deferred Tool Discovery (Claude Code)

All bridge MCP tools are deferred in Claude Code. Before you can call any bridge tool,
you must fetch its schema with `ToolSearch`. Batch-fetch tools you expect to need:
`select:mcp__vs-ide-bridge__list_instances,mcp__vs-ide-bridge__read_file,mcp__vs-ide-bridge__reload_document,mcp__vs-ide-bridge__errors,mcp__vs-ide-bridge__warnings,mcp__vs-ide-bridge__find_text,mcp__vs-ide-bridge__find_text_batch,mcp__vs-ide-bridge__build`

## Key Tools for This Repo

| Task | Tool |
|------|------|
| Take one code slice | `read_file` (reveals in VS editor) |
| Take several code slices | `read_file_batch` |
| Get focused context for a query | `smart_context` |
| Search code | `find_text`, `search_symbols` |
| Bulk search (many queries) | `find_text_batch` — one round-trip for multiple queries |
| Find files by name | `find_files` |
| Check errors/warnings | `diagnostics_snapshot` |
| Make code changes | Native `Edit` / `Write` tools, then `reload_document` + `errors` |
| Build | `build` or `build_errors` |
| Bump version | `set_version` |
| Build installer | `set_build_configuration` config=Release, then `build` (builds solution; ISCC runs automatically) |
| Open VS if closed | `vs_open` (no solution), then `list_instances`, then `open_solution` |

## Bulk Refactoring Workflow

When making many similar changes across a large file:
1. Use `find_text_batch` with all the target strings to get every location at once
2. Read the relevant sections with `read_file` (tight line ranges)
3. Apply changes with the native `Edit` tool — the PostToolUse hook reloads the file in VS automatically
4. Check `warnings` and `errors` after all edits to verify

## Code Slice Guidance

- When a human says "take a slice of the code," use `read_file` or `read_file_batch`, not `open_file` alone.
- Use `read_file` for one anchor or one range. Use `read_file_batch` for multiple ranges or files simultaneously.

## Source Of Truth

- The installed bridge stack under `C:\Program Files\VsIdeBridge\...` is the **runtime** source of truth.
- The repo source may be ahead of the installed version. If they disagree, say so explicitly.
- After building and installing a new version, MCP tools may disappear until the client session restarts.

## Installing the Bridge Interrupts the Session

**Running the installer ends the current Claude Code session.** The installer stops and replaces the
MCP server binary, which kills the active MCP connection. Claude Code cannot continue after the
installer runs — the session is dead.

Workflow to handle this:
1. Make all code changes and build (`build` tool, Release, `VsIdeBridgeInstaller` project).
2. Tell the user the installer is ready at `installer\output\vs-ide-bridge-setup-2.2.5.exe`.
3. **Stop here.** Ask the user to run the installer manually, then start a new Claude Code session.
4. Do NOT attempt to run the installer via `shell_exec` — it will kill the session mid-run and
   leave the tool call hanging until it times out.

## Known Bugs and Workarounds

See `BUGS.md` before starting any work. No critical open issues currently.

## CLI vs Service Split

The CLI has been deleted as an MCP host (Phase 6 complete). `VsIdeBridgeService` is the sole
MCP server. The binary at `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe` is the service
shim — running it directly prints "Cannot start service from the command line." Do not use
it for tool calls or queries. Do not add any code to the CLI.

## Roadmap and Ongoing Work

Phases 1–6 are complete. Current work is **Phase 7 — structural cleanup:**

- `ErrorListService` (3000+ lines) → extract to `VsIdeBridge.Diagnostics` class library
- `SolutionProjectCommands` (240+ methods) → split into focused classes
- `DocumentService` / `PatchService` / `SearchService` (1500+ lines each) → extract to class libraries
- Fix namespace mismatches, eliminate repeated string literals

See `ROADMAP.md` for the full target list.

## Local Config Files

- Codex global MCP config: `%USERPROFILE%\.codex\config.toml`
- Claude Code / JSON-based MCP config: `.mcp.json`
- Continue workspace MCP config: `.continue/mcpServers/vs-ide-bridge.yaml`
- MCP server command: `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe` with `mcp-server --tools-only`
- Only add `--instance <instanceId>` when you intentionally want to pin one client to one specific Visual Studio window
