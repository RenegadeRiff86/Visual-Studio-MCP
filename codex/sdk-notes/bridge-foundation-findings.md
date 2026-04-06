# Bridge Foundation Findings

This note captures the current foundation-level findings that should stay visible even if the session context is gone.

## `vs_open` / Hidden `devenv`

- The current `vs_open` path reports success too early.
- In `InstanceTools.cs`, the service launches VS and only later waits for bridge registration.
- In `VsIdeBridgeLauncher`, the helper reports success as soon as `Process.Start(...)` returns a PID.
- That means "launch request returned a PID" is being treated as success even though Visual Studio may still fail, stay headless, or never register the bridge.

### Main design weakness

- The launch path is a double hop:
  - service -> helper
  - helper -> `devenv.exe`
- Cleanup later only has one PID to kill.
- That is too weak for reliable process ownership and cleanup.

### Safer direction

- Keep `vs_open` disabled by default until the launch path is redesigned.
- Treat bridge registration as the real success signal, not just PID creation.
- Make one component own the entire launch lifetime and kill the whole tree on failure.
- Avoid reporting success before VS is visible, responsive, or registered.

## Project-System File Access

- The bridge still assumes that reading or editing file content means opening the file in the VS text editor.
- That works for normal text files such as `.cs`, `.cpp`, `.vcxproj.filters`, `.props`, and `.targets`.
- It fails for project-owned files such as `.csproj` and `.vcxproj`, because VS treats them as project-system artifacts rather than normal editor documents.

### Practical impact

- `read_file` can fail on project-owned files.
- `apply_diff` and `write_file` inherit the same weakness because they depend on the same document/content path.
- This is a first-class bug for C++ and project metadata workflows, not just a C# edge case.

### Safer direction

- Split content access from editor reveal.
- Let read/edit operations work without requiring editor activation.
- Treat project-owned files as a supported category:
  - `.csproj`
  - `.vcxproj`
  - `.vcxproj.filters`
  - `.props`
  - `.targets`
  - Python project metadata where applicable

## Debugger / Breakpoints

- `EnvDTE.Debugger.Breakpoints.Add(...)` creates a breakpoint request, but that does not guarantee the breakpoint is already bound.
- A breakpoint can validly be pending, bound, or in error.
- The bridge should not treat "not immediately resolved" as the same thing as "failed to create".

### Current bug

- `BreakpointService.SetBreakpointAsync(...)` adds the breakpoint and then immediately tries to re-find it by exact `file + line`.
- That is brittle for real VS behavior, especially for C++ and multi-line statements where VS can snap to the first executable line or defer binding.

### Safer direction

- Report `pending`, `bound`, or `error` explicitly.
- Do not throw `internal_error` just because immediate exact-line readback failed.
- If production-grade status is needed, move from pure DTE readback to the debugger SDK event model.

## Long-Running Commands

- `rebuild_solution` can succeed inside VS after the MCP client has already timed out.
- That means the build path and the transport timeout budget are out of sync.
- The command result contract should not tell the client a long-running command failed when VS later reports success.

### Safer direction

- Separate long-running command budgets from normal quick request budgets.
- Preserve success/failure reporting across longer command durations.
- Prefer a progress-aware or pollable contract for builds and rebuilds.

## Call Hierarchy

- `call_hierarchy` currently opens the Visual Studio Call Hierarchy tool window, but the bridge still fails to return usable managed hierarchy data for symbols like `EvaluateWatchAsync` in `DebuggerService.cs`.
- The current implementation in `src/VsIdeBridge/Services/SearchService/ManagedCallHierarchy.cs` is still based on Roslyn/document resolution heuristics.
- That approach is the wrong default for this feature because the Visual Studio SDK already exposes a native Call Hierarchy API.

### Confirmed native SDK surface

- Installed assembly:
  - `C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CallHierarchy\Microsoft.VisualStudio.Language.CallHierarchy.dll`
- Public types present:
  - `Microsoft.VisualStudio.Language.CallHierarchy.ICallHierarchyUIFactory`
  - `Microsoft.VisualStudio.Language.CallHierarchy.ICallHierarchyToolWindowUI`
  - `Microsoft.VisualStudio.Language.CallHierarchy.ICallHierarchyMemberItem`
  - `Microsoft.VisualStudio.Language.CallHierarchy.ICallHierarchyItemDetails`
  - `Microsoft.VisualStudio.Language.CallHierarchy.ICallHierarchySearchCallback`
  - `Microsoft.VisualStudio.Language.CallHierarchy.CallHierarchyPredefinedSearchCategoryNames`
  - `Microsoft.VisualStudio.Language.CallHierarchy.CallHierarchySearchScope`

### Confirmed method shapes

- `ICallHierarchyUIFactory.CreateToolWindowUI()`
- `ICallHierarchyToolWindowUI.AddRootItem(ICallHierarchyMemberItem)`
- `ICallHierarchyToolWindowUI.ClearAllItems()`
- `ICallHierarchyMemberItem.StartSearch(string categoryName, CallHierarchySearchScope searchScope, ICallHierarchySearchCallback callback)`
- `ICallHierarchySearchCallback.AddResult(...)`
- `ICallHierarchySearchCallback.SearchSucceeded()`
- `ICallHierarchySearchCallback.SearchFailed(string)`

### Important missing piece

- The repo does not yet use `Microsoft.VisualStudio.Language.CallHierarchy` directly.
- The missing step is obtaining the starting `ICallHierarchyMemberItem` for the symbol under the caret.
- A likely clue exists in the editor commanding layer:
  - `Microsoft.VisualStudio.Text.Editor.Commanding.Commands.ViewCallHierarchyCommandArgs`
- That suggests the native provider path may come from the editor command pipeline rather than the Call Hierarchy window API alone.

### Safer direction

- Stop extending the Roslyn reconstruction path as the primary implementation.
- Add a native Call Hierarchy SDK integration path in `VsIdeBridge`.
- Use the real VS hierarchy objects as the source of truth, and keep any Roslyn/document fallback strictly secondary.
