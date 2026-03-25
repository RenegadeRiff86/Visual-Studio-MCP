# Bridge Bugs

## Open

No open bugs.

## Fixed

- `build` MCP tool did not expose a `require_clean_diagnostics` parameter, so the pre-build
  dirty-diagnostics guard (which blocks builds when the Error List contains any warnings or
  messages) could not be bypassed through the MCP surface — only via the CLI `--require-clean-diagnostics false`.
  Fixed in `src/VsIdeBridgeService/ToolCatalog/Registrars/DiagnosticsTools.cs`: the `build` tool
  now accepts `require_clean_diagnostics` (boolean, default `false`) and forwards it as
  `--require-clean-diagnostics` to the bridge. The default of `false` means builds are no longer
  blocked by BP-style warnings out of the box.

- `wait_for_instance` hang fixed (2.2.4+): each discovery poll now runs inside `Task.WhenAny`
  with a 5 s per-poll deadline so a hung mutex or file scan cannot block past the outer
  `timeout_ms`. The `wait_for_instance` workaround (use `list_instances` loop + `bind_instance`)
  is no longer needed.
- `TrimApplyDiffResponse` shim removed from `DocumentTools.cs`. The VSIX-side `apply-diff` and
  `write-file` commands no longer emit `bestPracticeWarnings` by default and already return
  `changedRangeCount` directly, so the service-layer post-processing was dead code.
- Installer `PrepareToInstall` now calls `sc stop VsIdeBridgeService` before taskkill so the SCM
  cannot restart the service process mid-copy, fixing the stale-binary-after-install race.
- `EnsureServiceRunning` added to `vs_open` and `list_instances`: if the Windows service was
  stopped (e.g. idle auto-shutdown), these tools now restart it automatically before proceeding.
- `ResolveDevenvPath` now checks both `ProgramFilesX86` and `ProgramFiles` for `vswhere.exe`,
  fixing `vs_open` failures on machines where VS 18 installs under `Program Files`.
- `SearchService.EnumerateSolutionFiles` threw `NullReferenceException` when the solution
  contained an unloaded project (`IdeBridgeJsonProbe.vcxproj` reproduced this). The outer
  `foreach (Project project in dte.Solution.Projects)` loop was not null-guarded, so
  `EnumerateProjectFiles` never got the chance to skip it. Added `if (project is null) continue;`
  matching the pattern already used in `SolutionFileLocator`. This caused `find_text`,
  `search_symbols`, and every operation that enumerates solution files to crash with an
  `internal_error` response.
- `ProjectToJson` in `SolutionProjectCommands` threw `E_NOTIMPL` (via `GetFileName()`) when
  serializing an unloaded/external project such as a `.vcxproj` that VS cannot fully load.
  Wrapped `p.FullName` in a try/catch and falls back to `string.Empty`, so `list_projects` no
  longer crashes when the solution contains unloaded entries.

- `add_project` MCP tool was miswired: the service registrar passed `--path` to the bridge command
  but `IdeAddProjectCommand` reads `args.GetRequiredString("project")`. Fixed in
  `src/VsIdeBridgeService/ToolCatalog/Registrars/ProjectTools.cs` — the arg key is now `"project"`.
- `warnings` was flaky for `group_by` queries. The bridge-side `warnings` command does not support
  `--group-by`, so passing it caused intermittent `The operation was canceled.` errors. Fixed in
  `src/VsIdeBridgeService/ToolCatalog/Registrars/DiagnosticsTools.cs` — `group-by` is no longer
  forwarded to the bridge call for warnings, and `group_by` is no longer treated as a narrowing
  argument that triggers `--quick` (which could also fail on older installed bridge versions).
- `SolutionFileLocator.EnumerateSolutionFiles` and `EnumerateProjectFiles` threw `NullReferenceException`
  when the solution contained unloaded or external projects (null `Project` entries or null `ProjectItems`).
  Fixed with null guards in both iterators. This also fixed intermittent crashes in `find_text` and
  `apply_diff` which both call into the same code paths.
- `create_project` was not wired into `CommandRegistrar`, so the bridge returned `Unknown command: 'create-project'.` The registrar now registers `IdeCreateProjectCommand`, and `PipeCommandNames` now carries an explicit `create-project` alias.
- `CommandRegistrar.cs` no longer references the nonexistent `PythonCommands.*` types, so those stale compile errors are gone.
- Python project commands are restored under `SolutionProjectCommands`: `set-python-project-env`, `set-python-startup-file`, and `get-python-startup-file` now have real VS-side implementations and registrar entries.
- The service project registrar now exposes `python_set_startup_file`, `python_get_startup_file`, and `python_sync_env`, so the canonical MCP surface no longer lags behind the CLI fallback for Python project helpers.
- `apply_diff` and `write_file` no longer block on synchronous `ready` plus `errors` post-check work in source. The service now queues a background diagnostics refresh and serves warm `errors` / `warnings` snapshots from memory when available.

## Notes

- These are bridge-tool wiring bugs, not user workflow mistakes.
- Structural cleanup should continue, but project-management tool fixes need to stay visible because they block class-library extraction work.
