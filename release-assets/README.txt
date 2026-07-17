TerminalHost portable package
=============================

Requirements
------------
- Windows 10 1809 or newer (Windows 11 recommended)
- Microsoft Edge WebView2 Runtime (normally already installed)

Quick start
-----------
1. Run Start-TerminalHost.cmd.
2. Select PowerShell, Windows PowerShell, or CMD in the application.
3. Start a terminal session.

MCP (Codex, LM Studio, Claude Desktop, Cursor, etc.)
---------------------------------------------------
This package includes Node.js, compiled MCP code, and all production dependencies.
No separate Node.js installation is required.

For clients that launch a local stdio MCP server, use the absolute path to:

  TerminalHost-MCP-stdio.cmd

Example JSON configuration:

  {
    "mcpServers": {
      "terminalhost": {
        "command": "C:\\absolute\\path\\TerminalHost-MCP-stdio.cmd"
      }
    }
  }

Example Codex configuration (~/.codex/config.toml):

  [mcp_servers.terminalhost]
  enabled = true
  required = true
  command = 'C:\absolute\path\TerminalHost-MCP-stdio.cmd'
  startup_timeout_sec = 15
  tool_timeout_sec = 600

For clients that connect to an MCP URL:
1. Run Start-MCP-HTTP.cmd after TerminalHost is running.
2. Configure the client URL as http://127.0.0.1:8766/mcp

Security
--------
TerminalHost and the bundled HTTP MCP service listen on 127.0.0.1 by default.
MCP terminal tools execute commands with the permissions of TerminalHost.
