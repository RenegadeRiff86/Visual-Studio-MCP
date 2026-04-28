# Changelog

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
