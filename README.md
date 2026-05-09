# VS IDE Bridge

VS IDE Bridge connects your AI assistant to Visual Studio. Once connected, your assistant can read and edit code, check errors, run builds, inspect debugger state, and work with Git — all against the live project that is already open on your machine.

## Requirements

- Windows 10 or Windows 11
- Visual Studio 2022 (17.x) or Visual Studio 2026 (18.x)
- An AI assistant with MCP support (Claude Code, Cursor, LM Studio, or any MCP-compatible client)

## Installation

1. Download and run the latest installer.
2. Open Visual Studio. The extension activates automatically on startup.
3. Connect your AI assistant using the setup below.

## Connecting Your AI Assistant

Pick the connection method that matches your client.

### Claude Code (stdio)

```bash
# Register for the current project
claude mcp add --transport stdio --scope project vs-ide-bridge "C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe" mcp-server

# Or register globally (available from any project)
claude mcp add --transport stdio --scope user vs-ide-bridge "C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe" mcp-server
```

Restart Claude Code after adding. Visual Studio must be running before you start a session.

If the server stops appearing after a reinstall, re-run the command above and restart Claude Code.

### HTTP (Codex, Cursor, and most web-based clients)

The bridge listens on `http://localhost:43117/` by default.

```toml
# Codex — ~/.codex/config.toml
[mcp_servers.vs-ide-bridge]
url = "http://localhost:43117/"
enabled = true
```

```json
// Generic HTTP MCP config
{
  "mcpServers": {
    "vs-ide-bridge": {
      "transport": { "type": "http", "url": "http://localhost:43117/" }
    }
  }
}
```

If the endpoint is off, enable it from Visual Studio: **Tools → VS IDE Bridge → Toggle HTTP MCP Server**

### Streamable HTTP (LM Studio and local models)

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "transport": { "type": "streamable-http", "url": "http://localhost:43118/mcp" }
    }
  }
}
```

Enable from Visual Studio: **Tools → VS IDE Bridge → Toggle Streamable HTTP MCP Server**

## What You Can Do

Once connected, describe what you want in plain language. The assistant figures out how to use the bridge.

**Navigate code**
- "Find all the places where `ProcessOrder` is called."
- "What does this method do? Show me its definition."
- "Show me everything that calls into this class."

**Make changes**
- "Rename this variable everywhere it's used."
- "Extract this block into a new helper method."
- "Add null checks to all the public methods in this file."

**Diagnose and build**
- "What errors are in the project right now?"
- "Fix all the warnings in this file."
- "Build the solution and tell me what broke."

**Debug**
- "Set a breakpoint on line 42 and start the debugger."
- "What are the local variables at the current line?"
- "Show me the call stack."

**Git**
- "What files have I changed since the last commit?"
- "Commit everything with a sensible message."
- "Show me the diff for this file."

**Project and packages**
- "What NuGet packages does this project reference?"
- "Add the latest version of Newtonsoft.Json."

## Tips

- **Open Visual Studio first.** The bridge connects to a running VS instance. If VS is not open, the assistant cannot see your code.
- **Multiple VS windows?** Tell your assistant which solution to work with: *"Bind to MySolution.sln."*
- **After a build error?** Ask *"what errors came up?"* instead of reading the Output window yourself — the assistant gets a structured list it can act on immediately.
- **Slow start?** IntelliSense takes a moment to load after VS opens. If the assistant reports incomplete symbol results, wait a few seconds and try again.

## Logs

Logs are written to `C:\Program Files\VsIdeBridge\logs\`:

- `mcp-server.log` — MCP request and response log
- `vs-ide-bridge-yyyy-MM-dd.log` — Visual Studio extension activity log

## Troubleshooting

**Assistant says it can't find Visual Studio or the solution**
Visual Studio must be running with your solution open before you start the AI session. Try telling your assistant: *"Connect to Visual Studio"* or *"Bind to MySolution.sln."*

**Tools stopped working after a reinstall**
Re-run the registration command for your client (see [Connecting Your AI Assistant](#connecting-your-ai-assistant)), then restart the client.

**Bridge is not responding**
Check that the `VsIdeBridgeService` Windows service is running. Open Services (`services.msc`) and look for **VS IDE Bridge Service**.

## Shell Safety

The bridge can run arbitrary shell commands on your machine when asked. This is intentional for advanced tasks, but treat it as a power tool:

- Prefer asking for typed actions (edit this file, run a build, commit this change) before asking for raw shell commands.
- Do not use unattended automation that includes shell commands without review.
- If your MCP client supports tool approval rules, add `shell_exec` to the requires-approval list.

## Building from Source

Prerequisites: Visual Studio 2022 or later with the Visual Studio extension development workload.

```bash
git clone https://github.com/<owner>/vs-ide-bridge
cd vs-ide-bridge
dotnet build
```

Open `VsIdeBridge.sln` in Visual Studio to work on the extension. Build the installer with **Build → Rebuild Solution** — the post-build step runs Inno Setup automatically and produces:

```
installer\output\vs-ide-bridge-setup-<version>.exe
```

## Third-Party Notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for license information covering third-party components used in this project.
