# Changelog

## 2.3.0

- Changed the default local MCP HTTP ports away from the common `8080` default: shared HTTP now uses `http://localhost:43117/`, and Streamable HTTP now uses `http://localhost:43118/mcp`.
- Centralized HTTP port defaults in `HttpServerDefaults` so the VSIX, Windows service, foreground HTTP hosts, tool descriptions, and setup docs do not drift apart.
- Fixed `apply_diff` verification for project and MSBuild files (`.csproj`, `.vcxproj`, `.props`, `.targets`, and `.sln`) by treating disk-backed patch files as disk source-of-truth instead of reading stale open Visual Studio project-file buffers.
- Added the lazy MCP tool surface so default `tools/list` stays compact while `call_tool`, `recommend_tools`, `tool_help`, and `list_tools` still expose the full bridge surface on demand.
- Improved tool recommendations for local coding models by recognizing common search/read/edit/run wording plus Claude, Codex, Qwen, Gemini, Grok, and DeepSeek provider cues.
- Documented the Visual Studio `Tools → VS IDE Bridge → Toggle Streamable HTTP MCP Server` path for enabling the service-owned Streamable HTTP MCP endpoint, and renamed the root menu command from `IdeBridgeMenu` to `VsIdeBridgeMenu`.
- Fixed explicit build/rebuild waits to use the Visual Studio build completion path and longer build timeout instead of falling back into courtesy polling loops.
- Fixed post-filtered search results so path and project filters accept either forward slashes or backslashes, keeping `find_text` and `find_text_batch` counts aligned with returned rows.
- Added an explicit VSCT `MenuText` for the Tools menu so Visual Studio displays `VS IDE Bridge` instead of a command/symbol name.
- Moved bridge file logs to `C:\ProgramData\VsIdeBridge\logs` and documented the service and extension log files.

## 2.2.13

- Added paged diagnostics retrieval so large warning, error, and message sets can be read in chunks with counts, offsets, filters, and sorting instead of flooding the model with every row at once.
- Added Visual Studio Output window capture support, including build-output panes, so diagnostics can fall back to structured output chunks when the Error List is empty, stale, or too large.
- Updated installer Visual Studio discovery to locate `VSIXInstaller.exe` with `vswhere` across installed and prerelease VS versions, with fallback Program Files scanning for older layouts.
- Fixed `read_file` returning a wall of JSON — the redundant per-line `lines` array and `queue` scheduling metadata are now stripped from the tool result so only the human-readable slice text is surfaced.
- Fixed `read_file_batch` returning only "Captured N slice(s)." — the summary now renders each slice's file header and content so the model can read code without an extra round-trip.
- Fixed `read_file_batch` silently ignoring `context_before` and `context_after` in anchor (`line`) mode when parameters were sent with underscore names, causing slices to collapse to a single line.
- Updated `read_file_batch` tool description to explicitly document both range modes: `start_line`+`end_line` for a fixed range, and `line`+`context_before`+`context_after` for an anchor-based slice.
- Narrowed broad `Exception` catches to `COMException` in COM enumerator loops (solution project list, project items, open documents) to satisfy the BP1034 analyzer and avoid masking unrelated errors.
- Improved best-practice comment analyzer: XML documentation comments (`///`) are no longer flagged as low-value, and redundant-prefix detection (`this method`, `gets the`, etc.) now only fires when the phrase opens the comment rather than appearing anywhere in it.

## 2.2.12

- Made `http_enable` and `http_disable` reconcile the service-owned HTTP listener instead of only toggling local process state. When called from a short-lived stdio MCP child the toggle now reaches the long-running service over the control pipe, so the listener on `localhost:8080` actually starts or stops and the persisted enable flag stays in sync. Falls back to the in-process listener when the service is not installed.
- Fixed the service control pipe accepting only one client at a time. A single long-lived control client (such as the stdio MCP child sending `MCP_REQUEST`) was blocking every other process — including `http_enable`/`http_disable` — from connecting. The accept loop now hands each connection to a per-client task and immediately loops back to accept the next.
- Made `http_status` cross-process honest. It now reports the live `localhost:8080` listener probe alongside the persisted enable flag, and surfaces a `note` describing what reconciliation actually did (service confirmed listener up / down, or fell back to in-process).
- Added `HttpServerController.IsPortListening` as the cross-process truth source so callers can tell whether a listener is really accepting connections versus only consulting the in-process `IsRunning` flag (which is process-local and now documented as such).
- Hardened `HttpServerController.Disable()` to handle `IOException` and `UnauthorizedAccessException` when deleting the flag file instead of letting them bubble up and abort the disable path.
- Centralized the service control pipe name in `NamedPipeAccessDefaults.ServiceControlPipeName` so the VSIX, the service, and the control client share a single source of truth, and added `NotifyHttpEnableAsync` / `NotifyHttpDisableAsync` to `ServiceControlClient` for callers that prefer the typed wrapper.
- Refreshed the `http_enable`, `http_disable`, and `http_status` tool descriptions in the catalog to describe the service-reconciliation behavior.

## 2.2.11

- Switched the installer and installed runtime fully to the service-backed MCP layout. The release removes the stale `cli` payload, fixes uninstall metadata, and adds installer support for enabling the local HTTP MCP endpoint.
- Fixed bridge discovery and command dispatch issues that could hide tools or break batched requests. This includes better discovery defaults, `glob` visibility, typed `batch` step arguments, and safer `BridgeCommand` registration behavior.
- Improved diagnostics and build behavior for large solutions. Passive warning and message reads now avoid unnecessary UI refreshes by default, quick fallback paths are stronger, and long-running solution builds report background progress without burning tokens.
- Reduced UI-thread stalls during search and investigation. `find_text_batch` now captures the search snapshot once per batch, and watchdog telemetry now records the active command when a probe timeout is detected.
- Cleaned analyzer and message-level issues in the bridge repo instead of suppressing them, and updated MCP setup documentation for both Codex and Claude Code with the current installed service path and config shapes.
