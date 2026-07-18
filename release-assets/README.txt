TerminalHost portable package
=============================

Requirements
------------
- Windows 10 1809 or newer (Windows 11 recommended)
- Microsoft Edge WebView2 Runtime (normally already installed)

Quick start
-----------
1. Run TerminalHost.exe. No command prompt window is opened.
2. Use "+ New Tab" to create independent PowerShell or CMD sessions.
3. Double-click a tab to rename it; right-click it to close it.

Open File > Settings to configure default shell and directory, terminal font
and theme, session restoration, tray behavior, MCP transport and ports, and
MCP security policy. Open Help > Diagnostics to copy a redacted diagnostic
report and recent application/audit logs.

Use the "Enable MCP" switch in TerminalHost to start or stop the bundled HTTP
MCP service silently. The setting is remembered. Its default endpoint is
http://127.0.0.1:8766/mcp; both the port and optional Bearer token are configurable.

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
The Settings window can restrict MCP tools and initial working directories,
require confirmDangerous=true for dangerous commands, and enable audit logging.
