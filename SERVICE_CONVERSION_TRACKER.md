# Service Conversion Tracker

## End Goal ✅

**The CLI has been deleted as an MCP host.**

The `VsIdeBridgeCli` project has been removed from the solution and its source deleted.
The Windows service (`VsIdeBridgeService`) is now the sole MCP host.

The CLI binary (`vs-ide-bridge.exe`) still exists as the compiled operator tool — it is built
from the same source but no longer developed or maintained as a product concern.

---

## Completed Phases

### Phase 1 — Bridge tools surfaced in service ✅
All 10 tools registered: `debug_start/stop/continue/break/step_over/step_into/step_out`,
`smart_context`, `file_symbols`, `batch`.

### Phase 2 — Git tools as service-native ✅
`GitTools.cs` registrar added to `VsIdeBridgeService`. `GitRunner.cs` extracted as shared helper.

### Phase 3 — Python tools as service-native ✅
`PythonTools.cs` registrar added. `PythonNativeTools()` registered in `ToolCatalog.cs`.

### Phase 4 — NuGet tools as service-native ✅
`NugetTools.cs` registrar added. `NugetTools()` registered in `ToolCatalog.cs`.

### Phase 5 — shell_exec demoted ✅
`shell_exec` description updated to last-resort framing.

### Phase 6 — CLI retired as MCP host ✅
`VsIdeBridgeCli` project removed from solution and source deleted.
All product tools (search, edit, diagnostics, git, python, nuget, debug) live in the service.

### Phase 7 — Codebase structural cleanup ✅

#### Target 1 — ErrorListService extraction to VsIdeBridge.Diagnostics ✅
- Moved VS-API-free enums and data types from `ErrorListService/Types.cs` to `VsIdeBridge.Diagnostics`
- Pruned `ErrorListService/Constants.cs` — VS-API-free constants moved to `VsIdeBridge.Diagnostics/ErrorListConstants.cs`
- Removed dead analyzer code from `ErrorListService/Analysis.cs` that was already duplicated in `VsIdeBridge.Diagnostics`
- `ErrorListService/TableInfrastructure.cs` retained in VsIdeBridge (VS table API dependency)

#### Target 2 — PatchService partial class split ✅
- `src/VsIdeBridge/Services/PatchService.cs` — class declaration, constructor, `ApplyPatchAsync`
- `src/VsIdeBridge/Services/PatchService/Models.cs` — all private nested types (`FilePatch`, `Hunk`, `HunkLine`, etc.)
- `src/VsIdeBridge/Services/PatchService/Parser.cs` — `ParseEditorPatchFormat`, `ParseUnifiedDiffFormat`, all Parse* helpers
- `src/VsIdeBridge/Services/PatchService/Applicator.cs` — `ApplyFilePatch`, `ApplySearchBlockPatch`, fuzzy matching
- `src/VsIdeBridge/Services/PatchService/PathResolver.cs` — `ResolvePatchPaths`, path helpers
- `src/VsIdeBridge/Services/PatchService/SafetyHelpers.cs` — document open/close safety, validation, utilities

#### Target 3 — SearchService partial class split ✅
- `src/VsIdeBridge/Services/SearchService.cs` — class declaration, constructor, three public API methods
- `src/VsIdeBridge/Services/SearchService/TextSearch.cs` — full-text search, regex execution, hit merging
- `src/VsIdeBridge/Services/SearchService/SymbolSearch.cs` — code model symbol matching, kind normalization
- `src/VsIdeBridge/Services/SearchService/SmartContext.cs` — `BuildSmartContexts`, scoring
- `src/VsIdeBridge/Services/SearchService/ResultHelpers.cs` — serialization, deduplication, path filtering

#### Target 4 — DocumentService partial class split ✅
- `src/VsIdeBridge/Services/DocumentService.cs` — class declaration, constructor, field declarations
- `src/VsIdeBridge/Services/DocumentService/TabEnumeration.cs` — list/enumerate open tabs and documents
- `src/VsIdeBridge/Services/DocumentService/Navigation.cs` — open, goto definition/implementation, navigate to line
- `src/VsIdeBridge/Services/DocumentService/Outline.cs` — document outline and symbol at location
- `src/VsIdeBridge/Services/DocumentService/Resolution.cs` — path resolution, `FindFileAsync`
- `src/VsIdeBridge/Services/DocumentService/ContentHelpers.cs` — read text, line helpers, metadata

#### Target 5 — SolutionProjectCommands review and tighten ✅
- `MutationCommands.cs`: extracted `FileNotFoundCode = "file_not_found"` and `ClassLibraryTemplateName = "ClassLibrary"` (3+ uses each)
- `PythonProjectCommands.cs`: replaced 3× `"file_not_found"` with shared `FileNotFoundCode` from same partial class

#### Target 6 — String literal constants in service registrars ✅
- `DiagnosticsTools.cs` (shared constants home): added 15 new constants shared across all `ToolCatalog` partial files:
  `Git`, `FileArg`, `Line`, `Column`, `Query`, `Documents`, `Message`, `Paths`, `PostCheck`, `Scope`, `Search`,
  `Debug`, `Python`, `Core`, `SystemCategory`
- `GitTools.cs`: 23× `"git"` → `Git`; 8× `"message"` → `Message`; 6× `"paths"` → `Paths`; bare `catch { }` → `catch (System.Text.Json.JsonException)`
- `ProjectTools.cs`: 30+ `"project"` → `Project`; 6× `"file"` → `FileArg`
- `DocumentTools.cs`: 15+ `"file"` → `FileArg`; `"line"` → `Line`; `"column"` → `Column`; `"query"` → `Query`; `"documents"` → `Documents`; `"post_check"` → `PostCheck`; bare `catch { }` → `catch (Exception)`
- `SearchTools.cs`: 12× `"query"` → `Query`; `"path"` → `Path`; `"scope"` → `Scope`; `"project"` → `Project`; `"max"` → `Max`; `"search"` → `Search`
- `DebugTools.cs`: 21× `"debug"` → `Debug`; `"file"` → `FileArg`; `"line"` → `Line`
- `SemanticTools.cs`: `"file"` → `FileArg`; `"line"` → `Line`; `"column"` → `Column`
- `PythonTools.cs`: 8× `"python"` → `Python`; 10× `"path"` → `Path`
- `CoreTools.cs`: 4× `"core"` → `Core`; 3× `"system"` → `SystemCategory`
- `InstanceTools.cs`: 2× bare `catch { }` → `catch (Exception)`

#### Additional best-practice fixes ✅
- `src/VsIdeBridge/VsIdeBridgePackage.cs:215`: bare `catch { }` → specific `IOException`/`UnauthorizedAccessException` handlers
- `src/VsIdeBridgeService/McpServerMode.cs:266`: `catch (Exception) { }` → `ObjectDisposedException`/`HttpListenerException` handlers

---

## Architecture

The solution follows the hierarchy defined in `ARCHITECTURE_HIERARCHY.md`:

- **VsIdeBridge** (VS Extension) — .NET Framework 4.7.2, Visual Studio SDK integration
- **VsIdeBridgeService** (Windows Service) — .NET 8, MCP host, service-native tools
- **Class Libraries** (.NET 8) — Shared domain logic, discovery, diagnostics tooling
- **Shared** — Tool definitions, schemas, registry, recommendation logic

## Next Steps

All seven phases of the roadmap are complete. The codebase is in a clean, well-structured state:
- Full solution builds with 0 errors
- No bare `catch { }` blocks remain in any touched file
- No string literals repeated 3+ times remain in any registrar file
- All large monolithic service files have been split into focused partial class files
- Shared constants are centralized in `DiagnosticsTools.cs` (the partial class constants home)

Future work (as needs arise):
1. Continue pruning `ErrorListService/Analysis.cs` if new rules are added to `VsIdeBridge.Diagnostics`
2. Watch for `DocumentService/ContentHelpers.cs` growth — split further if it exceeds 600 lines
3. Maintain the no-bare-catch and no-repeated-literals standards in all new code
