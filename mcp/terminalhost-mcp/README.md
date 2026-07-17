# TerminalHost MCP Server

TerminalHost MCP Server is a standalone, standards-based adapter for TerminalHost. It keeps a persistent WebSocket connection to the local TerminalHost application and exposes its sessions as MCP tools.

It is not tied to one AI product. Any MCP host that supports either standard local `stdio` servers or Streamable HTTP can use it, including Codex, LM Studio, Claude Desktop, Cursor, and custom MCP clients.

## Why it is faster than the WebSocket skill

The earlier skill started a shell helper, loaded settings, opened a WebSocket, performed one operation, and disconnected on every call. This server stays alive and reuses one WebSocket connection, while MCP clients invoke typed tools directly.

```text
Codex / LM Studio / other MCP host
       │
       ├─ stdio, or
       └─ Streamable HTTP
                │
        TerminalHost MCP Server
                │ persistent WebSocket
                ▼
          TerminalHost GUI/API
                │
              ConPTY
```

## Requirements

- Windows with TerminalHost running
- Node.js 18 or newer
- TerminalHost `%LOCALAPPDATA%\TerminalHost\settings.json`

The API token is read directly from the settings file. It is not placed in MCP client configuration or printed in logs.

## Build

```powershell
cd .\mcp\terminalhost-mcp
npm install
npm run build
```

The executable entry point is:

```text
mcp\terminalhost-mcp\dist\src\index.js
```

## Transport modes

### stdio

Use stdio when the MCP host launches local servers as child processes. This is the simplest choice for Codex, LM Studio, Claude Desktop, and Cursor.

```powershell
node .\dist\src\index.js --transport stdio
```

Do not start stdio mode manually for normal use. Put the command in the host's MCP configuration and let the host launch it.

### Streamable HTTP

Use HTTP when multiple applications should share one long-running MCP service or when the host only accepts an MCP URL.

```powershell
node .\dist\src\index.js --transport http --host 127.0.0.1 --port 8766
```

MCP endpoint:

```text
http://127.0.0.1:8766/mcp
```

Health endpoint:

```text
http://127.0.0.1:8766/health
```

HTTP mode is stateless at the MCP transport layer for broad client compatibility, while all requests share the same persistent TerminalHost WebSocket backend.

## LM Studio

Open **Program → Install → Edit mcp.json** and add the following entry. Replace the path with the absolute path on the machine:

```json
{
  "mcpServers": {
    "terminalhost": {
      "command": "node",
      "args": [
        "C:\\path\\to\\TerminalHost\\mcp\\terminalhost-mcp\\dist\\src\\index.js",
        "--transport",
        "stdio"
      ]
    }
  }
}
```

LM Studio follows the common Cursor-style `mcp.json` notation and spawns local servers as separate processes.

To use the shared HTTP service instead:

```json
{
  "mcpServers": {
    "terminalhost-http": {
      "url": "http://127.0.0.1:8766/mcp"
    }
  }
}
```

## Codex

Add this to the repository's `.codex/config.toml` or the user-level `~/.codex/config.toml`:

```toml
[mcp_servers.terminalhost]
enabled = true
required = true
command = "node"
args = [
  "C:\\path\\to\\TerminalHost\\mcp\\terminalhost-mcp\\dist\\src\\index.js",
  "--transport",
  "stdio"
]
startup_timeout_sec = 10
tool_timeout_sec = 600
```

For a separately started HTTP service:

```toml
[mcp_servers.terminalhost]
enabled = true
url = "http://127.0.0.1:8766/mcp"
startup_timeout_sec = 10
tool_timeout_sec = 600
```

Restart the MCP host or start a new task after changing its configuration.

## Claude Desktop and Cursor

Both can use the same local JSON entry shown for LM Studio. Put it under the client's `mcpServers` object and use an absolute script path.

## Tools

| Tool | Purpose |
| --- | --- |
| `terminal_list_sessions` | List GUI and background sessions |
| `terminal_get_session` | Read one session's metadata |
| `terminal_snapshot` | Read recent raw VT/ANSI output |
| `terminal_create_session` | Create and start a shell session |
| `terminal_write` | Write raw terminal input without waiting |
| `terminal_execute` | Run a command, wait for a unique marker, return output and exit code |
| `terminal_signal` | Send Ctrl+C, Ctrl+D, Escape, or Enter |
| `terminal_resize` | Resize the pseudoconsole |
| `terminal_stop_session` | Stop a session; requires `confirm=true` |
| `terminal_ping` | Check the TerminalHost connection |

For smaller local models, configure the host's tool allow-list to expose only the tools it needs. A useful minimal set is:

```text
terminal_list_sessions
terminal_snapshot
terminal_execute
terminal_ping
```

## `terminal_execute` behavior

`terminal_execute`:

1. Reads the session shell type.
2. Serializes concurrent commands per session.
3. Appends a unique shell-compatible completion marker.
4. Waits for the marker or process exit.
5. Returns output, raw VT output, exit code, duration, truncation state, and timeout state.

A timeout does **not** automatically send Ctrl+C. The process is left running so a model cannot accidentally terminate long-running work. Use `terminal_signal` explicitly if interruption is intended.

`terminal_execute` is intended for commands that return to their shell. For interactive programs that consume additional stdin, use `terminal_write` and `terminal_snapshot` instead.

## Configuration

| Environment variable | Purpose |
| --- | --- |
| `TERMINALHOST_SETTINGS_PATH` | Override the TerminalHost settings file |
| `TERMINALHOST_WS_URL` | Override the TerminalHost WebSocket URL; requires `TERMINALHOST_API_TOKEN` |
| `TERMINALHOST_API_TOKEN` | Token used with an explicit WebSocket URL |
| `TERMINALHOST_MCP_TRANSPORT` | `stdio` or `http` |
| `TERMINALHOST_MCP_HOST` | HTTP bind host; default `127.0.0.1` |
| `TERMINALHOST_MCP_PORT` | HTTP port; default `8766` |
| `TERMINALHOST_MCP_AUTH_TOKEN` | Optional independent Bearer token for the MCP HTTP endpoint |
| `TERMINALHOST_MCP_ALLOWED_HOSTS` | Comma-separated Host-header allow-list |

Equivalent CLI flags are available: `--transport`, `--host`, `--port`, `--auth-token`, and `--allowed-hosts`.

## Network safety

- HTTP defaults to `127.0.0.1` and enables Host-header validation to prevent DNS rebinding.
- Binding to a non-loopback address is rejected unless both an MCP Bearer token and an allowed-host list are configured.
- The MCP Bearer token is separate from the TerminalHost WebSocket token.
- All terminal mutation tools can execute code with the permissions of the TerminalHost process.
- `terminal_stop_session` requires an explicit `confirm=true` argument.

Example network-enabled startup:

```powershell
$env:TERMINALHOST_MCP_AUTH_TOKEN = "generate-a-long-random-token"
node .\dist\src\index.js `
  --transport http `
  --host 0.0.0.0 `
  --port 8766 `
  --allowed-hosts "terminalhost-mcp.local,192.168.1.10"
```

Clients must then send:

```text
Authorization: Bearer generate-a-long-random-token
```

Use TLS termination before exposing the endpoint beyond a trusted local network.

## Verification

With TerminalHost running:

```powershell
npm run build
npm run test:protocol
```

To include a harmless `terminal_execute` integration test against an existing session:

```powershell
$env:TERMINALHOST_TEST_SESSION_ID = "session-id"
npm run test:protocol
```

To also create, exercise, stop, and remove a temporary CMD session:

```powershell
$env:TERMINALHOST_TEST_CREATE_CMD = "1"
npm run test:protocol
```

The smoke test uses the official MCP client SDK against both stdio and Streamable HTTP.
