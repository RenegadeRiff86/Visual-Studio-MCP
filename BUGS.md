# Bridge Bugs

This file tracks current bridge/runtime issues that affect how the product should be documented or operated.

- For product usage guidance, read [README.md](README.md).
- For LLM workflow rules, read [AGENTS.md](AGENTS.md).
- For remaining cleanup work, read [ROADMAP.md](ROADMAP.md).
- For code ownership and extraction targets, read [ARCHITECTURE_HIERARCHY.md](ARCHITECTURE_HIERARCHY.md).

## Remaining

- No verified stdio attach client currently exists in the installed product path.
  - `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe mcp-server` is a separate foreground host, not attachment to the SCM-managed service.
  - Do not document any stdio path as service attachment until an actual attach client is verified.

- `vs_open` is currently unsafe and should be treated as failed for normal use.
  - Fresh launches still fail to register a live bridge instance reliably.
  - Failed launches can leave hidden or headless `devenv` processes behind long enough to interfere with normal Visual Studio startup and even repair workflows.
  - Do not rely on `vs_open` as a supported startup path until the launch strategy is redesigned and re-verified end to end.
  - The current double-hop launch model reports success too early and does not give one component reliable ownership of the full `devenv` lifetime.
  - Notes: `codex/sdk-notes/bridge-foundation-findings.md`

- Bridge-native project-system editing is still incomplete for C++-heavy workflows.
  - Bridge reads on repo `.csproj` files are working again, but project-owned artifacts still need stronger edit-path support instead of relying on editor-open behavior.
  - Files such as `.vcxproj`, `.props`, `.targets`, and similar project metadata should be first-class bridge content, especially for large native solutions.
  - `apply_diff` still fails on old-style project-owned files like `VsIdeBridgeLauncher.csproj` because the patch path falls back to `ItemOperations.OpenFile(...)`.
  - Notes: `codex/sdk-notes/bridge-foundation-findings.md`

- Keep runtime docs aligned with verified installed behavior, not older setup guidance.
  - If README examples and installed behavior diverge, update the docs to match the product that actually ships.

- Python tooling currently exposes stateless scratchpad commands, not a persistent REPL.
  - `python_eval` and `python_exec` are available for math and quick transforms.
  - Do not describe Python support as an interactive REPL until a real session-based tool exists.
