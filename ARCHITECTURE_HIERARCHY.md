# Architecture Hierarchy

Purpose: define where code belongs so service-first cleanup has a stable target instead of growing from one-off fixes.

## Current Cleanup Snapshot

- `VsIdeBridge.Discovery` and `VsIdeBridge.Tooling` are now real project boundaries in this clone.
- `ToolDefinitionCatalog` has started splitting by capability inside `VsIdeBridge.Discovery`.
- `VsIdeBridgeService/ToolCatalog.cs` has already shed most capability registrars into `src/VsIdeBridgeService/ToolCatalog/Registrars/`.
- `ToolRegistry` recommendation logic now lives in `src/Shared/ToolRegistry/Recommendations.cs`, which makes the next structural decisions more about the remaining lookup/listing code and then the largest VS-host monoliths.

## Top-Level Rule

- `VsIdeBridgeService` is the canonical MCP runtime.
- `VsIdeBridge` owns Visual Studio integration, VS APIs, and the bridge pipe runtime inside the IDE.
- `VsIdeBridgeCli` is backup hosting and transport glue only.
- Class libraries should own reusable domain logic instead of leaving it trapped inside app projects.
- `Shared` holds metadata, schemas, and host-agnostic coordination code.
- Installer code packages and installs the product, but should not become product runtime logic.

## Folder And Namespace Rule

- Each namespace or extracted type group should live in a matching folder. Do not use dotted filenames as a substitute for folder structure.
- When a partial type is split across files, create a folder named after the owning type and place simple filenames inside it, such as `McpServer/Transport.cs` instead of `McpServer.Transport.cs`.
- When a project links shared files, keep the linked path shape aligned with the source folder shape so Solution Explorer mirrors the real code layout.
- If a file cannot satisfy IDE0130 cleanly because it is still trapped inside a nested partial type, treat that as a refactor target instead of adding more dotted partial filenames.

## Runtime Hierarchy

### 1. Visual Studio Extension Layer

Project:
- `src/VsIdeBridge/VsIdeBridge.csproj`

Owns:
- Visual Studio SDK integration
- DTE and IDE automation
- document, editor, search, build, debug, and solution interactions inside Visual Studio
- named bridge commands exposed over the IDE bridge pipe
- readiness that depends on the live VS instance

Should contain:
- `Commands/*`
- `Services/*` that directly use VS APIs or the bridge pipe runtime
- diagnostics and best-practice analysis tied to the IDE state

Should not contain:
- MCP transport logic
- stdio or HTTP hosting
- generic MCP discovery catalogs
- CLI fallback policy

### 2. Service MCP Layer

Project:
- `src/VsIdeBridgeService/VsIdeBridgeService.csproj`

Owns:
- the always-ready Windows service runtime
- MCP protocol hosting for the primary product path
- service-native tools that should run in memory without shelling out
- cached service state, readiness, diagnostics, and recommendation behavior
- event-driven orchestration that reacts to edits, file changes, and VS lifecycle changes without making clients poll for follow-up state
- composition of shared tool definitions into the canonical MCP surface

Should contain:
- MCP host wiring
- service-only helpers such as cached status, diagnostics, and structured local tools
- background refresh, debounce, and cache invalidation logic for diagnostics and readiness snapshots
- thin bridge-command adapters when the real work happens inside `VsIdeBridge`

Should not contain:
- raw product behavior copied from CLI
- giant mixed catalogs with unrelated tool families in one file
- shell or script wrappers for workflows that deserve named service tools
- synchronous post-edit polling flows when the service can react to the edit and keep warm state in memory

### Service-Native Tools (subprocess, no VS dependency)

Some service tools run entirely in the service process — they spawn a subprocess (git,
python, dotnet) and return structured output. These do NOT route through the VS named pipe
and do NOT require a live VS instance. They still belong in ``VsIdeBridgeService``.

Register them with a direct ``new ToolEntry(...)`` handler instead of ``BridgeTool(...)``.
Reference implementation: ``src/VsIdeBridgeService/SystemTools/ShellExecTool.cs``.
Registrar files go in: ``src/VsIdeBridgeService/ToolCatalog/Registrars/``.

Current and planned service-native tool families:
- ``shell_exec``, ``set_version`` — already implemented in ``SystemTools/``
- ``git_*`` — ``GitTools.cs`` registrar (see ROADMAP.md Phase 2)
- ``python_list_envs``, ``python_install_package``, etc. — ``PythonTools.cs`` (Phase 3)
- ``nuget_add_package``, ``nuget_remove_package`` — ``NugetTools.cs`` (Phase 4)

The rule: if a tool needs git, python, dotnet, or any external process but does NOT need
VS IDE state, it is service-native. Do not put it in the CLI and do not make the LLM
call ``shell_exec`` for it — give it a named service tool with a proper schema.

See ``ROADMAP.md`` for the full implementation plan and per-phase file breakdown.

### 3. Class Library Layer

Project direction:
- add focused class libraries under `src/` as the cleanup progresses

Owns:
- reusable business logic that should not live in executable projects
- capability-specific services that do not require direct VS SDK access
- models, contracts, and orchestration that are too substantial to leave embedded in `ToolCatalog.cs`, `McpServer.cs`, or similar host files

Should contain:
- extracted capability libraries such as discovery, git, packaging, diagnostics projection, or tool orchestration
- code that needs to be shared by service and CLI without turning `Shared` into a dumping ground
- code that should be unit-testable without the full service or VS runtime

Should not contain:
- MCP host entrypoints
- Visual Studio SDK calls
- installer-specific packaging scripts
- transport-specific glue

Examples of good future candidates:
- service-native tooling helpers currently trapped in `src/VsIdeBridgeService/ToolCatalog.cs`
- git provider logic currently trapped in `src/VsIdeBridgeCli/McpServer.cs` and `src/VsIdeBridgeCli/Git/*`
- packaging orchestration that outgrows `BuildInstallerTool.cs`

### 4. Shared Model Layer

Project area:
- `src/Shared/*`

Owns:
- tool definitions
- categories, aliases, safety metadata, schemas, and nudges
- recommendation and discovery logic that is host-agnostic
- bridge command metadata only as execution detail

Should contain:
- `ToolDefinition`
- `ToolRegistry`
- shared catalog metadata
- data contracts used by both service and CLI hosts

Should not contain:
- service-specific state machines
- CLI transport details
- Visual Studio SDK logic
- architecture decisions driven by `bridgeCommand`

### 5. CLI Fallback Layer

Project:
- `src/VsIdeBridgeCli/VsIdeBridgeCli.csproj`

Owns:
- backup MCP hosting
- CLI verbs and operator workflows
- stdio/HTTP transport fallback
- last-resort compatibility for environments where the service cannot host MCP directly

Should contain:
- transport entrypoints
- request parsing and framing
- fallback-only host mechanics
- thin composition over shared definitions

Should not contain:
- primary product behavior
- service-native workflows
- long-term ownership of git, python, packaging, or process-heavy features
- duplicated business logic already available in service or VS layers

### 6. Installer Layer

Projects and files:
- `src/VsIdeBridgeInstaller/VsIdeBridgeInstaller.csproj`
- `installer/*`
- `scripts/build-setup.ps1`

Owns:
- packaging
- installation
- deployment layout
- service registration and delivery artifacts

Should contain:
- installer assembly and packaging assets
- packaging scripts and versioned output generation

Should not contain:
- runtime MCP logic
- service-state behavior
- tool definitions beyond installer-specific packaging steps

### 7. Tests and Probes

Projects:
- `tests/VsIdeBridgeCli.Tests/VsIdeBridgeCli.Tests.csproj`
- `src/IdeBridgeJsonProbe/IdeBridgeJsonProbe.vcxproj`

Owns:
- verification of contracts and regressions
- targeted probes for serialization, transport, and runtime assumptions

Should not contain:
- production ownership decisions

## Placement Rules By Feature

### Tool Discovery
- Metadata shape: `src/Shared`
- reusable discovery logic that outgrows metadata-only concerns: focused class library
- Canonical MCP exposure: `src/VsIdeBridgeService`
- CLI fallback exposure: `src/VsIdeBridgeCli`

### Visual Studio Navigation and Editing
- Command execution and IDE behavior: `src/VsIdeBridge`
- MCP wrapping and discovery: `src/VsIdeBridgeService`

### Service Readiness and Diagnostics
- Cached state and primary exposure: `src/VsIdeBridgeService`
- reusable readiness orchestration or diagnostics projection: focused class library
- IDE readiness signals: `src/VsIdeBridge`
- CLI mirroring only if required for fallback: `src/VsIdeBridgeCli`

### Git, Python, and External Tooling
- Preferred long-term home for structured runtime behavior: `src/VsIdeBridgeService`
- reusable providers and domain logic: focused class library
- Shared contracts and discovery metadata: `src/Shared`
- CLI only for fallback transport or explicit operator workflows

### Packaging and Versioning
- Version file mutation that is product-facing: `src/VsIdeBridgeService`
- reusable packaging orchestration: focused class library when script logic starts moving in-process
- Installer assembly and packaging mechanics: installer layer
- Backup CLI entrypoints only when the service is unavailable

## Class Library Rule

- Do not use executable projects as the default home for reusable logic.
- If logic is growing, reused, or needs clean tests, prefer a focused class library before adding another giant service or CLI file.
- `Shared` is for cross-host metadata and contracts, not for every extracted helper.
- Create capability-oriented libraries instead of one giant utilities project.

Suggested future library directions:
- `VsIdeBridge.Discovery`
- `VsIdeBridge.Git`
- `VsIdeBridge.Packaging`
- `VsIdeBridge.Tooling`
- `VsIdeBridge.Diagnostics`

## Current Cleanup Targets Mapped To The Hierarchy

- `src/VsIdeBridgeService/ToolCatalog.cs`
  - Future state: split into capability-specific service registrars plus extracted class-library logic where the behavior is reusable
- `src/VsIdeBridgeCli/McpToolCatalog.cs`
  - Future state: thin fallback composition over shared metadata and isolated helpers
- `src/VsIdeBridgeCli/McpServer.cs`
  - Future state: transport-only host entrypoint with product behavior removed
- `src/VsIdeBridgeService/SystemTools/ShellExecTool.cs`
  - Future state: explicit escape hatch, not the normal implementation path
- `src/VsIdeBridgeService/SystemTools/BuildInstallerTool.cs`
  - Future state: named packaging tool with less script delegation over time
- `src/Shared/ToolDefinition.cs`
  - Future state: public capability model centered on tool intent, not bridge command identity
- `src/Shared/ToolRegistry.cs`
  - Future state: keep trimming the base registry to lookup and discovery-list composition while recommendation orchestration continues moving into focused discovery files inside `VsIdeBridge.Discovery`

## Naming Rule

- If the user would naturally describe the capability in Visual Studio terms, the canonical tool name, summary, aliases, and help text should reflect that language.
- Internal transport names may exist, but they should not define the public mental model.

## Decision Test

When adding or moving code, ask:

1. Does this require live Visual Studio APIs or IDE state?
   - Put it in `VsIdeBridge`.
2. Is this reusable logic that does not need direct VS SDK access or host entrypoints?
   - Put it in a focused class library.
3. Is this the canonical MCP-facing runtime behavior that should be available from the service in memory?
   - Put it in `VsIdeBridgeService`.
4. Is this shared metadata, schema, or host-agnostic discovery logic?
   - Put it in `Shared`.
5. Is this only backup hosting, transport, or operator CLI behavior?
   - Put it in `VsIdeBridgeCli`.
6. Is this only for packaging or installation?
   - Put it in installer code.

If the answer is unclear, prefer the service path over the CLI path and record the ambiguity in `SERVICE_CONVERSION_TRACKER.md`.
