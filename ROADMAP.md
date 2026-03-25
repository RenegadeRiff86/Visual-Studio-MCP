# VS IDE Bridge — Roadmap

## Goal

Make every tool that an LLM needs available as a named, well-described MCP tool on the
**Windows service** (`VsIdeBridgeService`). The LLM should never need to reach for
`shell_exec` for routine tasks. The CLI is no longer a target for new feature work.

**End goal: delete the CLI as an MCP host entirely, then clean up the codebase.**
The phases below are the migration path to get there. Once Phase 6 is complete, the CLI
project (`VsIdeBridgeCli`) survives only as a thin operator command-line tool — all MCP
hosting, tool registration, and product logic is gone from it. After that, Phase 7 begins:
a structural cleanup of the remaining codebase (large classes, oversized files, namespace
mismatches) that the best-practice warnings have been flagging throughout.

---

## Architecture Reminder: Two Kinds of Service Tools

### 1. Bridge tools — require a live VS instance

Route through the VS named pipe via `bridge.SendAsync(id, "pipe-command", args)`.
Use the `BridgeTool(...)` helper in `ToolCatalog/Infrastructure.cs`.
These fail with a clear error when no VS instance is bound.

Examples: `read_file`, `apply_diff`, `errors`, `debug_stack`, `find_text`

### 2. Service-native tools — run entirely in the service process

Execute subprocesses or do in-process work with no VS dependency.
Use `new ToolEntry(name, description, schema, category, async (id, args, bridge) => { ... })`.
`bridge` is available for context (e.g. `ServiceToolPaths.ResolveSolutionDirectory(bridge)`)
but no pipe call is made.

Examples: `shell_exec`, `set_version` (already service-native)
Future: `git_*`, `python_*`, `nuget_*`

The `ShellExecTool.cs` in `src/VsIdeBridgeService/SystemTools/` is the canonical
reference implementation for a service-native subprocess tool.

---

## Work Queue (priority order)

### Phase 1 — Surface missing bridge tools in the service ✅ Pattern established
**Status: Ready to implement. Pure registration work — bridge commands already exist in VS.**

### Phase 1 — Surface missing bridge tools in the service ✅ Complete
All 10 tools registered: debug_start/stop/continue/break/step_over/step_into/step_out,
smart_context, file_symbols, batch.

These bridge commands exist in the VS extension (`tool_help` lists them) but have no
MCP surface in the service yet. Each is a one-line `BridgeTool(...)` call:

| MCP tool name       | Bridge command      | Category  | Notes |
|---------------------|---------------------|-----------|-------|
| `smart_context`     | `smart-context`     | search    | Best LLM-oriented context tool; highest priority |
| `file_symbols`      | `file-symbols`      | search    | More targeted than `file_outline` |
| `batch`             | `batch`             | core      | Lets LLM combine commands in one round-trip |
| `debug_start`       | `debug-start`       | debug     | Start debugger |
| `debug_stop`        | `debug-stop`        | debug     | Stop debugger |
| `debug_continue`    | `debug-continue`    | debug     | Continue after break |
| `debug_break`       | `debug-break`       | debug     | Break into debugger |
| `debug_step_over`   | `debug-step-over`   | debug     | Step over |
| `debug_step_into`   | `debug-step-into`   | debug     | Step into |
| `debug_step_out`    | `debug-step-out`    | debug     | Step out |

Add to the appropriate registrar file in `src/VsIdeBridgeService/ToolCatalog/Registrars/`.
`debug_*` controls go in `DebugTools.cs`. `smart_context` and `file_symbols` go in
`SearchTools.cs`. `batch` goes in `CoreTools.cs`.

---

### Phase 2 — Git tools as service-native MCP tools
**Status: Ready to implement. No VS dependency — pure subprocess calls.**

Create `src/VsIdeBridgeService/ToolCatalog/Registrars/GitTools.cs` as a new
`private static IEnumerable<ToolEntry> GitTools()` partial method, registered in
`ToolCatalog.cs`.

Working directory: always `ServiceToolPaths.ResolveSolutionDirectory(bridge)` (repo root).
Process execution: copy the pattern from `ShellExecTool.cs` — set `GIT_TERMINAL_PROMPT=0`
and `GIT_NO_PAGER=1`, use `--no-pager -c safe.directory=<path>` prefix.
Git executable: resolve from PATH then known install locations (port from CLI's
`ResolveGitExecutable` in `McpServer\GitTools.cs`).

Tools to implement (all category `"git"`):

| Tool name          | Git command                             | Required args    | Optional args |
|--------------------|-----------------------------------------|------------------|---------------|
| `git_status`       | `status --porcelain=v1 --branch`        | —                | — |
| `git_current_branch` | `branch --show-current`               | —                | — |
| `git_branch_list`  | `branch --all --verbose --no-abbrev`    | —                | — |
| `git_log`          | `log --max-count=N --date=iso-strict ...` | —              | `max_count` (default 20) |
| `git_show`         | `show --no-color <revision>`            | `revision`       | — |
| `git_diff_unstaged`| `diff --no-color --unified=N`           | —                | `context` (default 3) |
| `git_diff_staged`  | `diff --cached --no-color --unified=N`  | —                | `context` (default 3) |
| `git_remote_list`  | `remote --verbose`                      | —                | — |
| `git_tag_list`     | `tag --list --sort=version:refname`     | —                | — |
| `git_stash_list`   | `stash list`                            | —                | — |
| `git_add`          | `add -- <paths>`                        | `paths` (array)  | — |
| `git_restore`      | `restore --source=HEAD --worktree -- <paths>` | `paths`    | — |
| `git_commit`       | `commit -m <message>`                   | `message`        | — |
| `git_commit_amend` | `commit --amend [-m msg\|--no-edit]`    | —                | `message`, `no_edit` |
| `git_reset`        | `reset [-- paths]`                      | —                | `paths` (array) |
| `git_checkout`     | `checkout <target>`                     | `target`         | — |
| `git_create_branch`| `checkout -b <name> [start_point]`      | `name`           | `start_point` |
| `git_fetch`        | `fetch [--all] [--prune] [remote]`      | —                | `remote`, `all`, `prune` |
| `git_pull`         | `pull [remote] [branch]`                | —                | `remote`, `branch` |
| `git_push`         | `push [--set-upstream] [remote] [branch]` | —              | `remote`, `branch`, `set_upstream` |
| `git_merge`        | `merge [--ff-only\|--no-ff] [--squash] [-m msg] <source>` | `source` | `ff_only`, `no_ff`, `squash`, `message` |
| `git_stash_push`   | `stash push [--include-untracked] [-m msg]` | —           | `message`, `include_untracked` |
| `git_stash_pop`    | `stash pop`                             | —                | — |

Timeouts: 120 000 ms for `git_push`, `git_pull`, `git_fetch`, `git_merge`.
All others: 30 000 ms.

Also create a shared git runner helper — either inline in `GitTools.cs` or extract to
`src/VsIdeBridgeService/SystemTools/GitRunner.cs` if it gets large.

---

### Phase 3 — Python tools as service-native MCP tools
**Status: Ready to implement after Phase 2. No VS dependency for env/package management.**

Create `src/VsIdeBridgeService/ToolCatalog/Registrars/PythonTools.cs`.
Port from `src/VsIdeBridgeCli/McpServer/PythonTools.cs` and `CondaTools.cs`.

Tools to implement (category `"python"`):

| Tool name                | Mechanism                        | Notes |
|--------------------------|----------------------------------|-------|
| `python_list_envs`       | subprocess: bridge managed-python + PATH scan | Enumerate available interpreters |
| `python_env_info`        | subprocess: `python --version`, site-packages | Required: `path` |
| `python_list_packages`   | subprocess: `pip list --format=json`         | Required: `path` |
| `python_install_package` | subprocess: `pip install`        | Required: `path`, `packages` |
| `python_remove_package`  | subprocess: `pip uninstall -y`   | Required: `path`, `packages` |
| `python_create_env`      | subprocess: `python -m venv`     | Required: `path` (target dir) |
| `conda_install`          | subprocess: `conda install`      | Required: `packages`; Optional: `env` |
| `conda_remove`           | subprocess: `conda remove`       | Required: `packages`; Optional: `env` |

Note: `python_set_project_env`, `python_set_startup_file`, `python_get_startup_file`,
`python_sync_env` already exist in the service (they go through the VS bridge).
Only the env/package management tools listed above need to be added as service-native.

---

### Phase 4 — NuGet tools as service-native MCP tools
**Status: Blocked on Phase 2/3 pattern being established.**

Create `src/VsIdeBridgeService/ToolCatalog/Registrars/NugetTools.cs`.
Port from `src/VsIdeBridgeCli/McpServer.cs` (`nuget_add_package`, `nuget_remove_package`).
Mechanism: subprocess `dotnet add package` / `dotnet remove package` using solution directory.
Category: `"project"` (these are project-management operations).

---

### Phase 5 — Demote `shell_exec`
**Status: After Phases 2–4 are done.**

Once git, python, and nuget have named tools, update `shell_exec`:
- Change its description to make it explicit this is a last-resort escape hatch
- Add a `nudge` or `note` field steering LLMs to named tools first
- Consider adding a `destructiveHint: true` annotation so clients can warn before use
- Update `AGENTS.md` and `README.md` to document when `shell_exec` is appropriate

---

### Phase 6 — Retire the CLI as a product concern
**Status: After Phases 1–5. The CLI project can stay for operator use but is no longer maintained as an MCP host.**

Once the service exposes all tools:
- Remove git/python/nuget/document/search/diagnostics/debug tool registrations from `McpServer.cs`
  (they only need the 10-tool bootstrap catalog that `McpToolCatalog.cs` already has)
- Update `README.md` to reflect service-first
- Update `AGENTS.md` to remove CLI references
- Leave `Program.cs` CLI verbs intact for human operator use (not LLM use)

---

### Phase 7 — Codebase cleanup
**Status: After Phase 6. The CLI noise is gone; now fix the structural debt in what remains.**

The best-practice warnings in the Error List are the work queue. Priority targets:

- **`SolutionProjectCommands`** — 242 methods across multiple partial files. Extract into
  focused command classes or a dedicated class library using `create_project`.
- **`ErrorListService`** — 3243 lines, 510 methods. The single biggest cleanup target.
  Extract into `VsIdeBridge.Diagnostics` class library.
- **`DocumentService`** / **`PatchService`** / **`SearchService`** — all 1500+ lines.
  Extract into `VsIdeBridge.Documents` or similar class libraries.
- **`McpServer.cs`** in the CLI — 2276 lines. Once Phase 6 strips product tools this shrinks
  dramatically; finish the job by extracting whatever remains into `VsIdeBridge.Discovery`.
- **Namespace mismatches** — `VsIdeBridgeCli` namespace used in `McpToolCatalog\` subfolders.
  Align to folder structure after CLI cleanup.
- **String literal constants** — repeated pipe command names, arg keys, category strings.
  Extract to `PipeCommandNames`, `ArgKeys`, or category-constant classes.

Use `create_project` to create new class libraries under `src\`. Follow `ARCHITECTURE_HIERARCHY.md`
for where each extracted class belongs. Check `BUGS.md` for any remaining open issues to fold in
alongside structural work.

---

## What the Service Can Handle Without VS

These tools work even when no VS instance is bound:

- All git tools (Phase 2)
- All python env/package tools (Phase 3)
- All nuget tools (Phase 4)
- `shell_exec` (already service-native)
- `set_version` (already service-native)
- `list_instances`, `bridge_health` (discovery only)

These tools require a live VS instance (named pipe):

- Everything in search, documents, diagnostics, debug, project, core (VS binding)
- `smart_context`, `file_symbols`, `batch` (Phase 1 additions)

---

## Files To Touch Per Phase

### Phase 1
- `src/VsIdeBridgeService/ToolCatalog/Registrars/DebugTools.cs` — add debug controls
- `src/VsIdeBridgeService/ToolCatalog/Registrars/SearchTools.cs` — add `smart_context`, `file_symbols`
- `src/VsIdeBridgeService/ToolCatalog/Registrars/CoreTools.cs` — add `batch`

### Phase 2
- `src/VsIdeBridgeService/ToolCatalog/Registrars/GitTools.cs` — create new file
- `src/VsIdeBridgeService/ToolCatalog.cs` — add `GitTools()` to `CreateEntries()`
- `src/VsIdeBridgeService/SystemTools/GitRunner.cs` — create if extracted

### Phase 3
- `src/VsIdeBridgeService/ToolCatalog/Registrars/PythonTools.cs` — create new file (add env/package tools)
- `src/VsIdeBridgeService/ToolCatalog.cs` — add `PythonNativeTools()` call

### Phase 4
- `src/VsIdeBridgeService/ToolCatalog/Registrars/NugetTools.cs` — create new file
- `src/VsIdeBridgeService/ToolCatalog.cs` — add `NugetTools()` call

### Phase 5
- `src/VsIdeBridgeService/ToolCatalog/Registrars/SystemTools.cs` or wherever `shell_exec` is registered
- `AGENTS.md`, `README.md`

### Phase 6
- `src/VsIdeBridgeCli/McpServer.cs` — strip product tools, keep 10-tool bootstrap only
- `src/VsIdeBridgeCli/McpServer/GitTools.cs` — delete
- `src/VsIdeBridgeCli/McpServer/PythonTools.cs` — delete
- `src/VsIdeBridgeCli/McpServer/CondaTools.cs` — delete
- `src/VsIdeBridgeCli/McpToolCatalog/Registrars/` — delete all registrar files
- `README.md`, `AGENTS.md`
