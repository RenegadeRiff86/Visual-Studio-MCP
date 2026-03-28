# VS IDE Bridge — Roadmap

- For product runtime guidance, read [README.md](README.md).
- For LLM workflow rules, read [AGENTS.md](AGENTS.md).
- For current runtime gaps, read [BUGS.md](BUGS.md).
- For code ownership and extraction boundaries, read [ARCHITECTURE_HIERARCHY.md](ARCHITECTURE_HIERARCHY.md).

## Remaining Work

## 1. Finish the runtime story

- Verify or replace the missing stdio attach-client path.
- Keep docs explicit that `VsIdeBridgeService.exe mcp-server` is a foreground host, not Windows-service attachment.
- Prove that `vs_open` can launch and register a live Visual Studio instance reliably, not just clean up failed launches.

## 2. Reduce warning-driven structural debt

Current live baseline:

- `0` errors
- `91` warnings
- `1` message

Highest-signal cleanup targets from the live warning list:

- `SolutionProjectCommands`
- `ErrorListService`
- `DocumentService`
- `PatchService`
- `SearchService`
- `BuildService`
- `PipeServerService`
- `BestPracticeAnalyzer`
- service tool registrars under `src/VsIdeBridgeService/ToolCatalog/Registrars/`

Primary warning families still left:

- `BP1018` large classes
- `BP1013` long methods
- `BP1027` accessor-heavy model types
- `BP1012` oversized files

## 3. Keep analyzer guidance aligned with LLM behavior

- Continue expanding best-practice coverage when a code type is missing useful guidance.
- Prefer analyzer additions that steer refactoring, service extraction, and safer MCP-tool usage.
- Rebuild and verify the live warning surface after each analyzer or structural batch.
