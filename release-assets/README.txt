TerminalHost portable package
=============================

Requirements
------------
- Windows 10 1809 or newer (Windows 11 recommended)
- Microsoft Edge WebView2 Runtime (normally already installed)

Quick start
-----------
1. Run TerminalHost.exe. No command prompt window is opened.
2. Select PowerShell, Windows PowerShell, or CMD in the application.
3. Start a terminal session.

Use the "Enable MCP" switch in TerminalHost to start or stop the bundled HTTP
MCP service silently. The setting is remembered. Its endpoint is
http://127.0.0.1:8766/mcp.

MCP (Codex, LM Studio, Claude Desktop, Cursor, etc.)
---------------------------------------------------
This package includes Node.js, compiled MCP code, and all production dependencies.
No separate Node.js installation is required.

Example JSON configuration:

  {
    "mcpServers": {
      "terminalhost": {
        "command": "C:\\absolute\\path\\TerminalHost.exe",
        "args": ["--mcp-stdio"]
      }
    }
  }

Example Codex configuration (~/.codex/config.toml):

  [mcp_servers.terminalhost]
  enabled = true
  required = true
  command = 'C:\absolute\path\TerminalHost.exe'
  args = ['--mcp-stdio']
  startup_timeout_sec = 15
  tool_timeout_sec = 600

For clients that connect to an MCP URL, run TerminalHost.exe, turn on "Enable
MCP", and configure the client URL as http://127.0.0.1:8766/mcp. No separate
MCP window is required.

Security
--------
TerminalHost and the bundled HTTP MCP service listen on 127.0.0.1 by default.
MCP terminal tools execute commands with the permissions of TerminalHost.
