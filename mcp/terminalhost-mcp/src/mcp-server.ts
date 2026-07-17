import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import * as z from "zod/v4";
import { TerminalCommandExecutor } from "./executor.js";
import { normalizeSession, TerminalHostClient } from "./terminalhost-client.js";

function asToolResult(value: unknown) {
  return {
    content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }]
  };
}

export function createTerminalHostMcpServer(client: TerminalHostClient): McpServer {
  const server = new McpServer({
    name: "terminalhost-mcp",
    version: "1.0.0"
  });
  const executor = new TerminalCommandExecutor(client);

  server.registerTool("terminal_list_sessions", {
    title: "List TerminalHost sessions",
    description: "List all GUI and background TerminalHost sessions, including shell, working directory, size, and running state.",
    inputSchema: {},
    annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }
  }, async () => asToolResult({ sessions: await client.listSessions() }));

  server.registerTool("terminal_get_session", {
    title: "Get a TerminalHost session",
    description: "Get metadata for one TerminalHost session by ID.",
    inputSchema: {
      sessionId: z.string().min(1).describe("TerminalHost session ID")
    },
    annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }
  }, async ({ sessionId }) => asToolResult({ session: await client.findSession(sessionId) }));

  server.registerTool("terminal_snapshot", {
    title: "Read terminal output snapshot",
    description: "Read the recent buffered VT/ANSI output for a TerminalHost session. This does not change the session.",
    inputSchema: {
      sessionId: z.string().min(1).describe("TerminalHost session ID"),
      maxChars: z.number().int().min(100).max(1_000_000).default(100_000).describe("Maximum characters returned from the end of the snapshot")
    },
    annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }
  }, async ({ sessionId, maxChars }) => {
    await client.findSession(sessionId);
    const response = await client.request("snapshot", { sessionId });
    const full = String(response.data ?? "");
    const data = full.length > maxChars ? full.slice(-maxChars) : full;
    return asToolResult({ sessionId, data, truncated: data.length < full.length, totalChars: full.length });
  });

  server.registerTool("terminal_create_session", {
    title: "Create a terminal session",
    description: "Create and start a new TerminalHost shell session. Allowed shells are pwsh, powershell, and cmd.",
    inputSchema: {
      shell: z.enum(["pwsh", "powershell", "cmd"]).default("powershell"),
      cwd: z.string().min(1).optional().describe("Existing working directory; defaults to the user profile"),
      cols: z.number().int().min(20).max(500).default(120),
      rows: z.number().int().min(5).max(300).default(30)
    },
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: true }
  }, async ({ shell, cwd, cols, rows }) => {
    const response = await client.request("create", { shell, cwd, cols, rows });
    return asToolResult({ session: normalizeSession(response.session) });
  });

  server.registerTool("terminal_write", {
    title: "Write raw terminal input",
    description: "Write raw input to a running TerminalHost session. Add \\r to submit a command. This only confirms input acceptance and does not wait for completion.",
    inputSchema: {
      sessionId: z.string().min(1),
      data: z.string().min(1).describe("Raw terminal input, including control characters when needed")
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true }
  }, async ({ sessionId, data }) => {
    const session = await client.findSession(sessionId);
    if (!session.isRunning) throw new Error(`Terminal session is not running: ${sessionId}`);
    await client.request("write", { sessionId, data });
    return asToolResult({ ok: true, sessionId, acceptedChars: data.length });
  });

  server.registerTool("terminal_execute", {
    title: "Execute a terminal command",
    description: "Execute one command in an existing TerminalHost session, wait for a unique completion marker, and return output plus exit code. A timeout does not interrupt the command.",
    inputSchema: {
      sessionId: z.string().min(1),
      command: z.string().min(1).describe("PowerShell or CMD command matching the session shell"),
      timeoutMs: z.number().int().min(100).max(600_000).default(120_000),
      maxOutputChars: z.number().int().min(1_000).max(1_000_000).default(200_000),
      plainText: z.boolean().default(true).describe("Strip ANSI/VT control sequences from the primary output field; rawOutput is always preserved")
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true }
  }, async ({ sessionId, command, timeoutMs, maxOutputChars, plainText }) => {
    return asToolResult(await executor.execute(sessionId, command, timeoutMs, maxOutputChars, plainText));
  });

  server.registerTool("terminal_signal", {
    title: "Send a terminal control key",
    description: "Send Ctrl+C, Ctrl+D, Escape, or Enter to a running TerminalHost session.",
    inputSchema: {
      sessionId: z.string().min(1),
      signal: z.enum(["ctrlC", "ctrlD", "escape", "enter"])
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true }
  }, async ({ sessionId, signal }) => {
    await client.findSession(sessionId);
    await client.request("signal", { sessionId, signal });
    return asToolResult({ ok: true, sessionId, signal });
  });

  server.registerTool("terminal_resize", {
    title: "Resize a terminal session",
    description: "Resize a TerminalHost pseudoconsole.",
    inputSchema: {
      sessionId: z.string().min(1),
      cols: z.number().int().min(20).max(500),
      rows: z.number().int().min(5).max(300)
    },
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false }
  }, async ({ sessionId, cols, rows }) => {
    await client.findSession(sessionId);
    await client.request("resize", { sessionId, cols, rows });
    return asToolResult({ ok: true, sessionId, cols, rows });
  });

  server.registerTool("terminal_stop_session", {
    title: "Stop a terminal session",
    description: "Stop a TerminalHost session. Requires confirm=true. Removing it also discards future access to its buffered snapshot.",
    inputSchema: {
      sessionId: z.string().min(1),
      graceful: z.boolean().default(true),
      remove: z.boolean().default(false),
      confirm: z.literal(true).describe("Must be true to confirm stopping the process")
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true }
  }, async ({ sessionId, graceful, remove }) => {
    await client.findSession(sessionId);
    await client.request("stop", { sessionId, graceful, remove }, graceful ? 10_000 : 5_000);
    return asToolResult({ ok: true, sessionId, graceful, remove });
  });

  server.registerTool("terminal_ping", {
    title: "Check TerminalHost connectivity",
    description: "Check the persistent WebSocket connection to TerminalHost and return the service timestamp.",
    inputSchema: {},
    annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }
  }, async () => asToolResult(await client.request("ping")));

  return server;
}
