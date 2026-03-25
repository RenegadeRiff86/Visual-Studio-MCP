# VS IDE Bridge

Visual Studio extension that exposes scriptable IDE commands for external automation over a named pipe. The bridge starts automatically when Visual Studio starts, and the native CLI can enumerate, launch, and target specific live IDE instances.

## Disclaimer

This project is experimental. Use it at your own risk.

## Purpose

Lets you drive a running Visual Studio instance from outside the IDE: search code, navigate to symbols, slice documents, apply diffs, control the debugger, and capture build output - all without touching the keyboard.

Commands are invoked through simple pipe names like `state`, `search-symbols`, and `quick-info`. The legacy `Tools.Ide*` names still work for compatibility. The native CLI can print `json`, `summary`, or `keyvalue` to stdout and can also write envelopes to a caller-specified output file.

The installed Windows service (`VsIdeBridgeService`) is the primary MCP host. The CLI remains available as a backup path and for direct terminal fallback commands, but client MCP configs should prefer the service executable so discovery and editing stay on the bridge-native path.

## Getting Started

1. **Close Visual Studio and your MCP client**, then download and run `vs-ide-bridge-setup-<version>.exe` from [GitHub Releases](https://github.com/RenegadeRiff86/vs-ide-bridge/releases/latest). The installer sets everything up automatically.

2. **Add the installed bridge to your client config.** Prefer the installed service host for MCP clients:

   `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe mcp-server`

   If your app asks for separate UI fields, use:

   - `Server name`: `vs-ide-bridge`
   - `Command`: `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
   - `Arguments`: `mcp-server`

   For clients that support HTTP-type custom MCP servers (such as GitHub Copilot Chat):
   - Launch with `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe mcp-http --port 8080`
   - In the dialog:
     - **Server ID**: `vs-ide-bridge` (or any unique identifier)
     - **Type**: HTTP
     - **URL**: `http://localhost:8080/`
     - **Headers** (optional): use the "+ Add" button (e.g. `Content-Type: application/json`, `Authorization: Bearer <token>`, or `X-MCP-Session: <id>`). The server currently ignores incoming headers but logs requests for debugging.

   The HTTP mode re-uses the same tool registry, bridge connection logic, and protocol versions as the stdio mode.

## Quick Start

1. **Close Visual Studio and your MCP client** before installing.
2. **Download the installer** (`vs-ide-bridge-setup-<version>.exe`) from [GitHub Releases](https://github.com/RenegadeRiff86/vs-ide-bridge/releases/latest) and run it. The installer sets everything up automatically.
3. **Set up your client.** Codex and Claude are both supported first-class here. Pick the config format your client expects (see MCP client examples below).
4. Open Visual Studio, then restart your MCP client.
5. Start by binding to the solution and checking health with `bind_solution` and `bridge_health`.
6. If the MCP client ever can't connect, use the installed `vs-ide-bridge` command from a terminal as a fallback.

## MCP Configuration Tips

If your client supports both stdio and HTTP, prefer stdio for lower latency. Use HTTP when the client UI only offers the HTTP type or for remote/shared server scenarios.

## MCP Configuration Examples

**Codex**: add this block to `%USERPROFILE%\.codex\config.toml`:

```toml
[mcp_servers.vs-ide-bridge]
command = "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe"
args = ["mcp-server"]
```

**Claude Code / Claude Desktop / other JSON-based clients**: add this MCP config:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

**Continue**: use this YAML workspace block (.continue/mcpServers/vs-ide-bridge.yaml):

```yaml
name: VS IDE Bridge
version: 0.0.1
schema: v1
mcpServers:
  - name: vs-ide-bridge
    command: C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe
    args:
      - mcp-server
```

**Cursor / Roo Code**: add this to your Cursor or Roo Code MCP config:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

**Generic JSON-based clients**: for tools that expect a JSON MCP config, use:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

## MCP Command Catalog

The MCP facade is the preferred interface for agent workflows. Use `tool_help` for the authoritative live schema and examples. Current MCP commands:

```text
activate_document
activate_window
add_file_to_project
add_project
apply_diff
bind_instance
bind_solution
bridge_health
bridge_state
build
build_configurations
build_errors
call_hierarchy
clear_breakpoints
close_document
close_file
close_others
conda_install
conda_remove
count_references
create_solution
debug_exceptions
debug_locals
debug_modules
debug_stack
debug_threads
debug_watch
diagnostics_snapshot
disable_all_breakpoints
disable_breakpoint
enable_all_breakpoints
enable_breakpoint
errors
execute_command
file_outline
find_files
find_references
find_text
find_text_batch
format_document
git_add
git_branch_list
git_checkout
git_commit
git_commit_amend
git_create_branch
git_current_branch
git_diff_staged
git_diff_unstaged
git_fetch
git_log
git_merge
git_pull
git_push
git_remote_list
git_reset
git_restore
git_show
git_stash_list
git_stash_pop
git_stash_push
git_status
git_tag_list
github_issue_close
github_issue_search
goto_definition
goto_implementation
help
list_breakpoints
list_documents
list_instances
list_projects
list_tabs
list_windows
nuget_add_package
nuget_remove_package
nuget_restore
open_file
open_solution
peek_definition
python_create_env
python_env_info
python_install_package
python_list_envs
python_list_packages
python_remove_package
python_repl
python_run_file
python_set_active_env
python_set_project_env
python_set_startup_file
python_get_startup_file
python_sync_env
python_install_requirements
query_project_configurations
query_project_items
query_project_outputs
query_project_properties
query_project_references
read_file
read_file_batch
reload_document
remove_breakpoint
remove_file_from_project
remove_project
save_document
search_solutions
search_symbols
set_breakpoint
set_build_configuration
set_startup_project
set_version
shell_exec
symbol_info
tool_help
ui_settings
vs_close
vs_open
wait_for_instance
wait_for_ready
warnings
write_file
```

## Command Surface

The tables below list the preferred simple pipe names. The live `catalog` command returns a standardized payload for LLM callers:

- `schemaVersion` and `generatedAtUtc` at the top level
- `catalog.commands[]` as the canonical list
- per-command fields: `name`, `canonicalName`, `description`, `example`, `aliases`

Compatibility fields (`commands[]`, `legacyCommands[]`, `commandDetails[]`) are still emitted.

### Core

| Command | Description |
|---------|-------------|
| `help` | List all registered commands (`catalog` is an alias) |
| `state` | Snapshot of IDE state (solution path, active document, etc.) — MCP name: `bridge_state` |
| `ready` | Block until IntelliSense is available — MCP name: `wait_for_ready` |
| `open-solution` | Open a solution file |
| `create-solution` | Create and open a new `.sln` solution |
| `close` | Close the targeted Visual Studio instance gracefully |
| `batch` | Execute multiple commands in one pipe round-trip |
| `ui-settings` | Read or change IDE Bridge UI settings (allow edits, go-to-edited-parts, etc.) |

### Search and Navigation

| Command | Description |
|---------|-------------|
| `find-text` | Text search across solution or project |
| `find-files` | Find files by name |
| `parse` | Parse saved JSON locally with slash-path selection |
| `document-slice` | Fetch lines around a location |
| `document-slices` | Fetch multiple document ranges from a JSON file or inline JSON |
| `file-symbols` | Symbol list for a file, with optional kind filtering |
| `file-outline` | Alias for `file-symbols` |
| `search-symbols` | Search symbols by name across the current scope |
| `quick-info` | Resolve symbol/definition info at a location — MCP name: `symbol_info` |
| `smart-context` | Multi-context gather for agent queries |
| `find-references` | Semantic find-all-references |
| `call-hierarchy` | Callers and callees |
| `goto-definition` | Navigate to definition (F12) |
| `peek-definition` | Peek definition inline |
| `goto-implementation` | Navigate to implementation |
| `open-document` | Open a file at a line/column |
| `list-documents` | List open documents |
| `list-tabs` | List open editor tabs |
| `activate-document` | Activate a document by name |
| `close-document` | Close a document by name |
| `close-file` | Close a document by full path |
| `close-others` | Close all tabs except the active one |
| `save-document` | Save one document or all open documents |
| `activate-window` | Activate a tool window |
| `list-windows` | List tool windows |
| `execute-command` | Execute any native VS command by name |
| `format-document` | Format the current document or a specific file |
| `apply-diff` | Apply a unified diff through the live VS editor |
| `write-file` | Write or overwrite a file through the live VS editor |
| `reload-document` | Reload a document from disk inside Visual Studio |

### Breakpoints

| Command | Description |
|---------|-------------|
| `set-breakpoint` | Set a breakpoint at file/line |
| `list-breakpoints` | List all breakpoints |
| `remove-breakpoint` | Remove a breakpoint |
| `clear-breakpoints` | Remove all breakpoints |
| `enable-breakpoint` | Enable a breakpoint at file/line |
| `disable-breakpoint` | Disable a breakpoint at file/line |
| `enable-all-breakpoints` | Enable every breakpoint |
| `disable-all-breakpoints` | Disable every breakpoint |

### Debugger

> These are pipe commands, not first-class CLI verbs. Use `send --command <name>` to invoke them.

| Command | Description |
|---------|-------------|
| `debug-state` | Current debugger state |
| `debug-start` | Start debugging |
| `debug-stop` | Stop debugging |
| `debug-break` | Break into the debugger |
| `debug-continue` | Continue execution |
| `debug-step-over` | Step over |
| `debug-step-into` | Step into |
| `debug-step-out` | Step out |

### Build and Diagnostics

| Command | Description |
|---------|-------------|
| `build` | Build the solution |
| `build-errors` | Build then capture diagnostics in one call |
| `warnings` | Capture warnings from the Error List, or Build Output fallback, as JSON |
| `errors` | Capture errors from the Error List, or Build Output fallback, as JSON |
| `build-configurations` | List available build configurations/platforms |
| `set-build-configuration` | Activate one build configuration/platform pair |
| `diagnostics-snapshot` | IDE state + debugger + build + errors/warnings in one call |

### Solution and Projects

| Command | Description |
|---------|-------------|
| `search-solutions` | Search for `.sln`/`.slnx` files on disk under a root directory |
| `open-solution` | Open a solution file in the current VS instance |
| `create-solution` | Create and open a new solution in the current VS instance |
| `list-projects` | List all projects in the open solution |
| `add-project` | Add an existing project file to the solution |
| `remove-project` | Remove a project from the solution |
| `set-startup-project` | Set the solution startup project |
| `add-file-to-project` | Add an existing file to a project |
| `remove-file-from-project` | Remove a file from a project |
| `query-project-items` | List items in a project with paths and item types |
| `query-project-properties` | Read MSBuild project properties |
| `query-project-configurations` | List project configurations and platforms |
| `query-project-references` | List project references |
| `query-project-outputs` | Resolve primary output artifact and output directory |

### Python

| Command | Description |
|---------|-------------|
| `set-python-project-env` | Set the active Python interpreter for the open `.pyproj` project or open-folder workspace in Visual Studio (affects IntelliSense and debugging) |
| `python-list-envs` | List available Python environments |
| `python-env-info` | Get details about a Python environment |
| `python-set-active-env` | Set the active Python environment |
| `python-list-packages` | List installed packages in the active environment |
| `python-install-package` | Install a Python package |
| `python-install-requirements` | Install packages from a requirements file |
| `python-remove-package` | Remove a Python package |
| `python-create-env` | Create a new Python environment |
| `python-sync-env` | Sync environment with requirements |
| `python-repl` | Execute Python code in the VS REPL |
| `python-run-file` | Run a Python file in VS |
| `python-get-startup-file` | Get the current Python startup file |
| `python-set-startup-file` | Set the Python startup file |

## Argument Contract

- `--out "C:\path\result.json"` - output path (falls back to `%TEMP%\vs-ide-bridge\<command>.json`)
- `--request-id "abc123"` - optional correlation id echoed in the envelope
- `--timeout-ms 120000` - timeout on wait/build commands
- boolean flags: `--flag` (bare, implies `true`) or `--flag true` / `--flag false`
- enum values: lowercase kebab-case

## Command Examples

```text
ready --timeout-ms 120000 --out "C:\temp\ready.json"
state --out "C:\temp\state.json"
open-solution --solution "C:\path\to\your.sln" --out "C:\temp\open.json"
create-solution --directory "C:\path\to\scratch" --name "ScratchApp" --out "C:\temp\create.json"

find-files --query "GUI_App.cpp" --out "C:\temp\files.json"
find-text --query "OnInit" --scope solution --path "src\libslic3r" --out "C:\temp\find.json"
parse --json-file "C:\temp\errors.json" --select "/Data/errors/rows/0/message"
parse --json-file "C:\temp\errors.json" --select "/Data/errors/rows/*/file" --format lines
document-slice --file "C:\repo\src\foo.cpp" --line 42 --context-before 8 --context-after 24 --out "C:\temp\slice.json"
file-outline --file "C:\repo\src\foo.cpp" --max-depth 2 --out "C:\temp\outline.json"
file-symbols --file "C:\repo\src\foo.cpp" --kind function --out "C:\temp\file-symbols.json"
search-symbols --query "propose_export_file_name_and_path" --kind function --out "C:\temp\symbols.json"
quick-info --file "C:\repo\src\foo.cpp" --line 42 --column 13 --out "C:\temp\quick-info.json"
document-slices --ranges-file "C:\temp\ranges.json" --out "C:\temp\slices.json"
smart-context --query "where is OnInit called" --out "C:\temp\smart-context.json"
find-references --file "C:\repo\src\foo.cpp" --line 42 --column 13 --out "C:\temp\refs.json"
call-hierarchy --file "C:\repo\src\foo.cpp" --line 42 --column 13 --out "C:\temp\hierarchy.json"
goto-definition --file "C:\repo\src\foo.cpp" --line 42 --column 13 --out "C:\temp\goto.json"

open-document --file "C:\repo\src\foo.cpp" --line 42 --column 1 --out "C:\temp\open-doc.json"
list-documents --out "C:\temp\documents.json"
list-tabs --out "C:\temp\tabs.json"
activate-document --query "foo.cpp" --out "C:\temp\activate.json"
close-document --query "foo.cpp" --out "C:\temp\close.json"
close-file --file "C:\repo\src\foo.cpp" --out "C:\temp\close-file.json"
close-others --out "C:\temp\close-others.json"
list-windows --query "Error List" --out "C:\temp\windows.json"
execute-command --command "View.SolutionExplorer" --out "C:\temp\exec.json"
apply-diff --patch-file "C:\temp\change.diff" --out "C:\temp\apply.json"

set-breakpoint --file "C:\repo\src\foo.cpp" --line 42 --condition "count == 12" --out "C:\temp\bp.json"
list-breakpoints --out "C:\temp\breakpoints.json"
send --command debug-start --args "--wait-for-break true --timeout-ms 120000" --out "C:\temp\debug-start.json"
send --command debug-continue --args "--wait-for-break true --timeout-ms 30000" --out "C:\temp\continue.json"

errors --wait-for-intellisense false --quick --out "C:\temp\errors.json"
build --configuration Debug --platform x64 --out "C:\temp\build.json"
build-errors --timeout-ms 600000 --out "C:\temp\build-errors.json"
```

### `apply-diff` format

`apply-diff` accepts either standard unified diff text with `---` / `+++` file headers and `@@` hunks, or editor patch text with `*** Begin Patch` / `*** End Patch` blocks. `apply-patch` is an alias for the same command.

**Editor patch is the preferred format for LLM callers.** It uses content-based matching instead of line numbers, so it is immune to the line-number drift that causes unified diffs to fail when multiple hunks shift line counts. Always call `read_file` first to get the current file content before writing a patch.

```diff
--- a/src/foo.cpp
+++ b/src/foo.cpp
@@ -1 +1 @@
-old text
+new text
```

```text
*** Begin Patch
*** Update File: src/foo.cpp
@@
-old text
+new text
*** End Patch
```

### `errors` flags

- `--wait-for-intellisense true` (default) - waits for IntelliSense to finish loading before reading
- `--quick` - reads the current diagnostics snapshot immediately; skips the stability polling loop (use after a build has finished)

On large C++ solutions, `errors` and `warnings` may return diagnostics from Build Output when the Error List is empty or too slow to enumerate.

### Error List prerequisites

The VS Error List is **only populated after at least one file from the project is open in the editor.** If `errors` returns an empty list after switching solutions or opening VS fresh, open a source file first:

```text
open-document --file "src\slic3r\GUI\GUI_App.cpp"
```

Then wait for IntelliSense to finish before reading the error list. The CLI `errors` command does this automatically via `--wait-for-intellisense`. When using the MCP `errors` tool or calling `errors --quick` directly, call `ready` first:

```text
ready --timeout-ms 120000
errors --quick
```

`ready` polls `IVsOperationProgressStatusService` for the IntelliSense stage completion and falls back to watching the status bar for "Ready". It returns immediately if IntelliSense is already done.

### `batch`

Execute multiple commands in a single bridge request:

```json
[
  { "command": "state" },
  { "command": "find-files", "args": "--query \"GUI_App.cpp\"" },
  { "command": "document-slice", "args": "--file \"C:\\repo\\src\\GUI_App.cpp\" --line 1384 --context-before 10 --context-after 30" }
]
```

```text
batch --file "C:\temp\batch.json" --out "C:\temp\batch-result.json"
```

Add `--stop-on-error` to halt on first failure. The result envelope contains a `results[]` array with per-step `success`, `summary`, `data`.

### `IdeGoToDefinition`

Positions the cursor at `--file`/`--line`/`--column`, posts `Edit.GoToDefinition` through the VS shell dispatcher (same path as F12), and returns both the source location and the resolved definition location. Works on any language with a VS language service. `definitionFound` is `true` when the definition is at a different file or line.

### `IdeGetFileOutline`

Returns symbols in a file (functions, classes, structs, enums, namespaces) using VS's FileCodeModel. C# and VB support is complete; C++ support is partial and depends on VS having a code model for the file.

### `IdeGetFileSymbols`

Compatibility alias over `IdeGetFileOutline` for agents that naturally ask for file symbols. Supports the same `--file`, `--kind`, and `--max-depth` arguments.

### `IdeApplyUnifiedDiff`

Accepts `--patch-file` (path) or `--patch-text-base64` (inline). Existing-file edits are applied through the live Visual Studio editor buffer first, so the change is visible immediately and VS can re-evaluate syntax. By default the edited document stays unsaved; pass `--save-changed-files` if you want the bridge to save it.

### `IdeExecuteVsCommand`

Supports optional `--file`, `--document`, `--line`, `--column` args to position the editor before dispatching the native command. Useful for VS commands that act on the caret position.

### `IdeWriteFile`

Writes or overwrites a file through the live Visual Studio editor so the change is immediately visible. Accepts `--file` (path) and `--content` (text) or `--content-base64` (inline). The file is opened in VS and saved automatically. MCP name: `write_file`.

### `IdeReloadDocument`

Reloads a document from disk inside Visual Studio, forcing IntelliSense to re-analyze it. Use this after an external tool or shell command modifies a file outside the editor, or to clear stale diagnostics. Accepts `--file` (path). MCP name: `reload_document`.

### `IdeSetPythonProjectEnv`

Sets the active Python interpreter for the open `.pyproj` project or open-folder workspace in Visual Studio. Accepts `--path` (interpreter path) and optional `--project` (project name or path). When `--path` is omitted, auto-detects a conda environment whose name matches the project name, falling back to the solution name. MCP name: `python_set_project_env`.

## JSON Output

Every command writes a JSON envelope:

```json
{
  "SchemaVersion": 1,
  "Command": "Tools.IdeGetState",
  "RequestId": null,
  "Success": true,
  "StartedAtUtc": "2026-01-01T12:00:00.0000000Z",
  "FinishedAtUtc": "2026-01-01T12:00:00.0100000Z",
  "Summary": "IDE state captured.",
  "Warnings": [],
  "Error": null,
  "Data": {}
}
```

Failures use the same envelope shape with `Success: false` and a populated `Error` object containing `code` and `message`.

Bridge failures also include IDE context in `Data` when available:

- `state` - current solution, active document, caret, and bridge identity
- `openTabs` - currently open document tabs
- `errorList` - quick Error List snapshot
- `errorSymbolContext` - nearby symbols for files/lines mentioned in current error rows

For flat output, use the native CLI:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe state --sln VsIdeBridge.sln --format keyvalue
```

## Named Pipe Server

The VSIX automatically starts a **persistent named pipe server** when Visual Studio loads the extension. Connecting directly over the pipe eliminates PowerShell process startup and COM mutex overhead on every call.

Use `scripts\start_bridge.ps1` for bootstrap only. The low-overhead runtime interface is the native C# CLI in `src\VsIdeBridgeCli\bin\<Configuration>\net8.0\vs-ide-bridge.exe`.

### Discovery

When the VSIX auto-loads, it writes:

```
%TEMP%\vs-ide-bridge\pipes\bridge-{pid}.json
```

```json
{
  "instanceId": "vs18-12345-20260303T040244Z",
  "pid": 12345,
  "startedAtUtc": "2026-03-03T04:02:44.0000000Z",
  "pipeName": "VsIdeBridge18_12345",
  "solutionPath": "C:\path\to\Your.sln",
  "solutionName": "Your.sln"
}
```

The file is deleted when Visual Studio exits. A missing file means VS is not running or the extension failed to load.

### Native CLI (`vs-ide-bridge.exe`)

List live bridge instances:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe instances --format summary
```

Resolve the current instance:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe current
"C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe" current --format keyvalue
```

Built-in help and command catalog:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe help
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe prompts
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe help parse
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe help send
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe catalog --instance vs18-12345-20260303T040244Z
```

Single command:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe ready --instance vs18-12345-20260303T040244Z
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe state --sln VsIdeBridge.sln --format summary
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe state --instance vs18-12345-20260303T040244Z --format summary
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe state --pid 12345 --format summary
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe find-files --instance vs18-12345-20260303T040244Z --query GUI_App.cpp
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe parse --json-file output\errors.json --select /Data/errors/rows/0/message
```

> **Git Bash note:** Git Bash converts arguments that start with `/` to Windows paths. If `--select` fails with a Windows-path error, either omit the leading slash (`Data/foo` instead of `/Data/foo`) or prefix the command with `MSYS_NO_PATHCONV=1`.

Batch request:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe batch --file output\pipe-test-batch.json --format summary
```

Raw request object or array:

```bat
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe request --json "{ ""batch"": [ { ""command"": ""state"" } ] }"
src\VsIdeBridgeCli\bin\Debug\net8.0\vs-ide-bridge.exe request --json-file output\pipe-test-batch.json
```

Supported verbs:

- `help`
- `prompts` (alias: `recipes`)
- `current`
- `instances`
- `ensure`
- `ready`
- `state`
- `catalog` (alias: `commands`)
- `parse`
- `find-files`
- `find-text`
- `search-symbols` (alias: `symbols`)
- `goto-definition`
- `peek-definition` (alias: `peek`)
- `goto-implementation`
- `find-references`
- `call-hierarchy`
- `quick-info`
- `document-slice` (alias: `slice`)
- `document-slices` (alias: `slices`)
- `file-symbols`
- `file-outline`
- `open-document`
- `list-documents`
- `list-tabs`
- `activate-document`
- `close-document`
- `close-file`
- `close-others`
- `list-windows`
- `activate-window`
- `apply-diff` (alias: `patch`)
- `set-breakpoint`
- `list-breakpoints`
- `remove-breakpoint`
- `clear-breakpoints`
- `enable-breakpoint`
- `disable-breakpoint`
- `enable-all-breakpoints`
- `disable-all-breakpoints`
- `build`
- `build-errors`
- `warnings` (alias: `warning`)
- `errors`
- `search-solutions`
- `open-solution`
- `create-solution`
- `shell-exec`
- `set-version`
- `vs-open`
- `vs-close`
- `nuget-restore`
- `nuget-add-package`
- `nuget-remove-package`
- `send` (alias: `call`)
- `batch`
- `request`
- `mcp-server`

Common options:

- `--instance ID`
- `--pid PID`
- `--pipe NAME`
- `--sln HINT`
- `--timeout-ms 10000`
- `--out FILE`
- `--format json|summary|keyvalue`
- `--verbose`

Selection behavior:

- If exactly one live instance matches, the CLI uses it.
- If multiple live instances match, the CLI fails and tells you to run `instances`.
- Use `--instance` when you need an exact target across multiple open Visual Studio windows.

Recommended agent pattern:

- run `help` when you need to re-learn the interface
- run `prompts` for task-oriented examples you can copy directly
- use `ensure` first when you know the solution path
- otherwise use `current`
- use `catalog` to retrieve the live command list from Visual Studio
- use `parse` when you already have a JSON result and only need one field or list
- use `--instance` for all follow-up commands in the same task
- only fall back to `instances` when `current` says more than one IDE is live

### MCP server (`mcp-server`)

Run the installed service-hosted MCP server on Windows next to Visual Studio:

```bat
"C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe" mcp-server
```

Use the installed CLI only for fallback/recovery workflows like `ensure`, `current`, or `instances`. Add `--instance <instanceId>` only when you intentionally want to pin one client to one specific live Visual Studio instance.

Exposed MCP tools use simple names. See the MCP Command Catalog below or call `tool_help` for the live installed list and schemas:

**IDE state and binding**: `bridge_state`, `wait_for_ready`, `tool_help`, `help`, `bridge_health`, `list_instances`, `bind_instance`, `bind_solution`, `ui_settings`

**Diagnostics**: `errors`, `warnings`, `diagnostics_snapshot`

**Editing and navigation**: `apply_diff`, `write_file`, `read_file`, `read_file_batch`, `reload_document`, `find_text`, `find_text_batch`, `find_files`, `search_symbols`, `find_references`, `count_references`, `peek_definition`, `goto_definition`, `goto_implementation`, `call_hierarchy`, `symbol_info`, `file_outline`, `format_document`, `execute_command`

**Documents and tabs**: `open_file`, `list_tabs`, `list_documents`, `activate_document`, `close_document`, `close_file`, `close_others`, `save_document`, `list_windows`, `activate_window`

**Build**: `build`, `build_errors`, `build_configurations`, `set_build_configuration`

**Debugger**: `debug_threads`, `debug_stack`, `debug_locals`, `debug_modules`, `debug_watch`, `debug_exceptions`

**Breakpoints**: `set_breakpoint`, `list_breakpoints`, `remove_breakpoint`, `clear_breakpoints`, `enable_breakpoint`, `disable_breakpoint`, `enable_all_breakpoints`, `disable_all_breakpoints`

**Solution**: `search_solutions`, `open_solution`, `create_solution`, `list_projects`, `add_project`, `remove_project`, `set_startup_project`, `add_file_to_project`, `remove_file_from_project`

**Project query**: `query_project_items`, `query_project_properties`, `query_project_configurations`, `query_project_references`, `query_project_outputs`

**Python**: `python_list_envs`, `python_env_info`, `python_set_active_env`, `python_set_project_env`, `python_list_packages`, `python_repl`, `python_run_file`, `python_install_package`, `python_install_requirements`, `python_remove_package`, `python_create_env`, `python_sync_env`, `python_get_startup_file`, `python_set_startup_file`

**VS lifecycle**: `vs_open`, `vs_close`, `wait_for_instance`

**Version**: `set_version`

**Shell**: `shell_exec`

**NuGet**: `nuget_restore`, `nuget_add_package`, `nuget_remove_package`

**Conda**: `conda_install`, `conda_remove`

**Git**: `git_status`, `git_current_branch`, `git_remote_list`, `git_tag_list`, `git_stash_list`, `git_diff_unstaged`, `git_diff_staged`, `git_log`, `git_show`, `git_branch_list`, `git_checkout`, `git_create_branch`, `git_merge`, `git_add`, `git_restore`, `git_commit`, `git_commit_amend`, `git_reset`, `git_fetch`, `git_stash_push`, `git_stash_pop`, `git_pull`, `git_push`

**GitHub**: `github_issue_search`, `github_issue_close`

Use `tool_help` to retrieve descriptions, schemas, examples, and bridge command metadata (`bridgeCommand`, `bridgeExample`) for every MCP tool in one call.

Exposed MCP resources:

- `bridge://current-solution`
- `bridge://active-document`
- `bridge://open-tabs`
- `bridge://error-list-snapshot`

Exposed MCP prompts:

- `help`
- `fix_current_errors`
- `open_solution_and_wait_ready`
- `git_review_before_commit`
- `git_sync_with_remote`
- `github_issue_triage`

The MCP layer is intentionally thin: it forwards to the bridge command surface and keeps edit approval/safety enforcement inside the existing Visual Studio bridge flow.

Implementation note: the server advertises MCP `tools` capability only. `resources/*` and `prompts/*` methods are still implemented, but not advertised, to avoid eager startup probes from MCP clients that can trigger Visual Studio automation calls before the IDE is fully ready.

Example MCP client registration. Codex, Claude Code, Claude Desktop, Continue, and other MCP clients should point at the installed Windows service:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": [
        "mcp-server"
      ]
    }
  }
}
```

Only add `--instance <instanceId>` if you intentionally want to pin one client to one specific live Visual Studio instance.

### Cross-platform clients

The bridge runtime itself is Windows-only because it automates Visual Studio and talks to the `VsIdeBridgeService` Windows service. Other client platforms are still supported when they treat Windows as the host machine:

- Windows host: install the VSIX, service, and CLI with `vs-ide-bridge-setup-<version>.exe`
- macOS/Linux/WSL client: start the MCP server on that Windows host and connect to it from your agent workflow
- keep the Visual Studio instance and `VsIdeBridgeService` on the same Windows machine

Recommended pattern:

1. Install the bridge on the Windows box.
2. Verify the service is running: `sc.exe query VsIdeBridgeService`
3. Start or reuse the target IDE session: `"C:\ Program Files\VsIdeBridge\cli\vs-ide-bridge.exe" ensure --solution "C:\path\to\Your.sln"`
4. Run the MCP server on that Windows box: `"C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe" mcp-server`
5. Point your non-Windows client at that Windows command through your normal remote-shell path.

Example using `ssh` from macOS/Linux/WSL to a Windows host with OpenSSH enabled:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "ssh",
      "args": [
        "your-windows-host",
        "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
        "mcp-server"
      ]
    }
  }
}
```

Notes:

- the Windows machine still needs Visual Studio 18 and the installed bridge stack
- the remote client does not need the VSIX; it only needs a way to launch the installed Windows service host
- if you target a specific IDE instance from a remote client, add `--instance <instanceId>` after confirming the instance with `current` or `instances` on Windows

### MCP client examples

All of the examples below use the same installed Windows service host as the MCP server path:

`C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe mcp-server`

Before using any client:

1. Install the bridge with `vs-ide-bridge-setup-<version>.exe` on Windows.
2. Verify the service is running: `sc.exe query VsIdeBridgeService`
3. Start or reuse the Visual Studio session you want to target: `"C:\ Program Files\VsIdeBridge\cli\vs-ide-bridge.exe" ensure --solution "C:\path\to\Your.sln"`

#### Codex

`%USERPROFILE%\.codex\config.toml`:

```toml
[mcp_servers.vs-ide-bridge]
command = "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe"
args = ["mcp-server"]
```

If Codex asks for UI fields instead of TOML, enter:

- `Server name`: `vs-ide-bridge`
- `Command`: `C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
- `Arguments`: `mcp-server`

If you keep a compatibility alias around for older sessions, point it at the same installed service host:

```toml
[mcp_servers.vs-ide-bridge-pr]
command = "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe"
args = ["mcp-server"]
```

Codex does not read project-local `.mcp.json`. Use `%USERPROFILE%\.codex\config.toml`, restart Codex, then start with `bind_solution`, `bridge_health`, and `tool_help`.

Do not register both `vs-ide-bridge` and `vs-ide-bridge-pr` unless you really need the compatibility alias. Duplicate bridge registrations can confuse MCP clients and models.

#### Continue

Workspace block file at `.continue/mcpServers/vs-ide-bridge.yaml`:

```yaml
name: VS IDE Bridge
version: 0.0.1
schema: v1
mcpServers:
  - name: vs-ide-bridge
    command: C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe
    args:
      - mcp-server
```

If Continue shows separate UI fields instead of a YAML file, use:

- `Server name`: `vs-ide-bridge`
- `Command`: `C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
- `Arguments`: `mcp-server`

Continue can also ingest JSON MCP configs copied into `.continue/mcpServers/` if you prefer the same JSON shape used by Claude Desktop, Cursor, or Cline. Do not add `--instance` unless you intentionally want Continue pinned to one live Visual Studio window.

#### Claude Code / Claude Desktop

Project-local `.mcp.json`:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

Claude Desktop uses the same JSON `mcpServers` shape in `claude_desktop_config.json`.

If you use Claude Code, the easiest setup is a project-local `.mcp.json` in the repo root. Claude can create that file for you if you paste the JSON block above.

If Claude asks for UI fields instead of JSON, enter:

- `Server name`: `vs-ide-bridge`
- `Command`: `C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
- `Arguments`: `mcp-server`

#### Cursor

Cursor uses the same JSON `mcpServers` shape. Add this to your Cursor MCP config:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

If Cursor shows UI fields instead of raw JSON, enter:

- `Server name`: `vs-ide-bridge`
- `Command`: `C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
- `Arguments`: `mcp-server`

#### Roo Code

Roo Code uses the same JSON `mcpServers` shape. Add this to your Roo Code MCP config:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

If Roo Code shows UI fields instead of raw JSON, enter:

- `Server name`: `vs-ide-bridge`
- `Command`: `C:\ Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
- `Arguments`: `mcp-server`

#### Generic JSON-based clients

For tools that expect a JSON MCP config, use:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

#### macOS, Linux, or WSL client talking to a Windows host

If the client is not running on Windows, keep the bridge installed on the Windows machine that is running Visual Studio and launch the installed service host remotely:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "ssh",
      "args": [
        "your-windows-host",
        "C:\\ Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
        "mcp-server"
      ]
    }
  }
}
```

### Pipe protocol

Each request is one JSON object terminated by `\n`:

```json
{ "id": "req-1", "command": "state", "args": "--out \"C:\\temp\\state.json\"" }
```

Batch requests can also send multiple logical commands in one envelope:

```json
{
  "id": "req-batch-1",
  "command": "batch",
  "stopOnError": false,
  "batch": [
    { "id": "ready", "command": "ready", "args": "--timeout-ms 120000" },
    { "id": "state", "command": "state", "args": "" }
  ]
}
```

Each response is one JSON envelope terminated by `\n`, using the same shape as the file-based command output.
For batched requests, the envelope `Data.results[]` array contains per-step `id`, `command`, `success`, `summary`, `warnings`, `data`, and `error`.

Use the discovery file to find the current pipe name, then send newline-delimited UTF-8 JSON over that named pipe. `args` uses the same command-line argument format as the bridge commands already use inside Visual Studio.

## Scripts

| Script | Purpose |
|--------|---------|
| `scripts\build.bat` | Build the solution |
| `VsIdeBridgeInstaller.csproj -t:RegisterInstallTask` | One-time: register elevated scheduled task so Release builds auto-install |
| `scripts\install.ps1` | Install built Release artifacts to `C:\Program Files\VsIdeBridge` (run elevated; close VS and MCP client first) |
| `scripts\start_bridge.ps1` | Thin PowerShell wrapper over `vs-ide-bridge ensure` |

## Repo Layout

```
src/VsIdeBridge/          VSIX package, commands, services, infrastructure
scripts/                  Build and startup entry points only
output/                   Local smoke-test artifacts (git-ignored)
```

## Notes

- The Tools menu exposes **IDE Bridge > Help**, **Allow Bridge Edits**, and **Go To Edited Parts**. `Help` opens the repo README when the current solution resolves to this repo and points you to `help` and `catalog` for the full command catalog. All other commands remain `CommandWellOnly` (available in the Command Window and via DTE, not in menus).
- Search scans files on disk; unsaved in-memory editor content is not included in `find-text` results.
- Symbol commands rely on VS language services, not bridge-side parsing.
- `execute-command` is the escape hatch for native VS commands that have no first-class bridge equivalent.
- Simple pipe names are the preferred public contract. The legacy `Tools.Ide*` names remain supported for compatibility.

## Third-Party Notices

See `THIRD_PARTY_NOTICES.md` for third-party attributions used by the build and packaging workflow.

