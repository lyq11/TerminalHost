import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const entry = process.env.TERMINALHOST_MCP_TEST_ENTRY ?? join(root, "dist", "src", "index.js");
const nodeCommand = process.env.TERMINALHOST_MCP_TEST_NODE ?? process.execPath;

function assertTools(tools) {
  const names = new Set(tools.tools.map((tool) => tool.name));
  for (const expected of ["terminal_list_sessions", "terminal_snapshot", "terminal_execute", "terminal_ping"]) {
    if (!names.has(expected)) throw new Error(`Missing MCP tool: ${expected}`);
  }
  return names.size;
}

async function exercise(client, label) {
  const toolCount = assertTools(await client.listTools());
  const ping = await client.callTool({ name: "terminal_ping", arguments: {} });
  if (ping.isError) throw new Error(`${label} terminal_ping failed`);
  const sessions = await client.callTool({ name: "terminal_list_sessions", arguments: {} });
  if (sessions.isError) throw new Error(`${label} terminal_list_sessions failed`);
  console.log(`${label}: ${toolCount} tools, ping ok, session listing ok`);
}

async function exerciseCommand(client) {
  const sessionId = process.env.TERMINALHOST_TEST_SESSION_ID;
  if (!sessionId) return;
  const expected = `MCP_EXEC_OK_${Date.now()}`;
  const response = await client.callTool({
    name: "terminal_execute",
    arguments: { sessionId, command: `echo ${expected}`, timeoutMs: 15_000 }
  });
  if (response.isError) throw new Error("terminal_execute returned an MCP error");
  const text = response.content?.find((item) => item.type === "text")?.text ?? "";
  if (!text.includes(expected) || !text.includes('"timedOut": false')) {
    throw new Error(`terminal_execute result was incomplete: ${text}`);
  }
  console.log("terminal_execute: completion marker and output ok");
}

async function exerciseSnapshot(client) {
  const sessionId = process.env.TERMINALHOST_TEST_SNAPSHOT_SESSION_ID;
  if (!sessionId) return;
  const response = await client.callTool({
    name: "terminal_snapshot",
    arguments: { sessionId, maxChars: 10_000 }
  });
  if (response.isError) throw new Error("terminal_snapshot returned an MCP error");
  const result = parseToolJson(response);
  if (result.sessionId !== sessionId || typeof result.data !== "string") {
    throw new Error(`terminal_snapshot result was invalid: ${JSON.stringify(result)}`);
  }
  console.log("terminal_snapshot: requested session read ok");
}

function parseToolJson(response) {
  const text = response.content?.find((item) => item.type === "text")?.text;
  if (!text) throw new Error("Tool did not return JSON text content");
  return JSON.parse(text);
}

async function exerciseCreatedCmdSession(client) {
  if (process.env.TERMINALHOST_TEST_CREATE_CMD !== "1") return;
  const createdResponse = await client.callTool({
    name: "terminal_create_session",
    arguments: { shell: "cmd", cwd: root, cols: 100, rows: 25 }
  });
  if (createdResponse.isError) throw new Error("Unable to create CMD integration-test session");
  const sessionId = parseToolJson(createdResponse).session.id;
  if (!sessionId) throw new Error("Created session did not include an ID");

  try {
    const expected = `CMD_MCP_OK_${Date.now()}`;
    const executeResponse = await client.callTool({
      name: "terminal_execute",
      arguments: { sessionId, command: `echo ${expected}`, timeoutMs: 15_000 }
    });
    const result = parseToolJson(executeResponse);
    if (executeResponse.isError || result.timedOut || result.exitCode !== 0 || !result.output.includes(expected)) {
      throw new Error(`CMD integration result was invalid: ${JSON.stringify(result)}`);
    }
    console.log("terminal_execute: CMD marker and exit code ok");
  } finally {
    await client.callTool({
      name: "terminal_stop_session",
      arguments: { sessionId, graceful: false, remove: true, confirm: true }
    });
  }
}

async function testStdio() {
  const transport = new StdioClientTransport({
    command: nodeCommand,
    args: [entry, "--transport", "stdio"],
    stderr: "pipe"
  });
  const client = new Client({ name: "terminalhost-mcp-smoke-stdio", version: "1.0.0" });
  try {
    await client.connect(transport);
    await exercise(client, "stdio");
    await exerciseSnapshot(client);
    await exerciseCommand(client);
    await exerciseCreatedCmdSession(client);
  } finally {
    await client.close();
  }
}

async function waitForHealth(url, child) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`HTTP server exited with ${child.exitCode}`);
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Retry until the startup deadline.
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error("HTTP MCP server did not become healthy.");
}

async function testHttp() {
  const port = 18_766;
  const child = spawn(nodeCommand, [entry, "--transport", "http", "--port", String(port)], {
    cwd: root,
    stdio: ["ignore", "ignore", "pipe"],
    windowsHide: true
  });
  let stderr = "";
  child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });

  try {
    await waitForHealth(`http://127.0.0.1:${port}/health`, child);
    const transport = new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`));
    const client = new Client({ name: "terminalhost-mcp-smoke-http", version: "1.0.0" });
    try {
      await client.connect(transport);
      await exercise(client, "streamable-http");
    } finally {
      await client.close();
    }
  } finally {
    child.kill("SIGTERM");
    await new Promise((resolve) => {
      if (child.exitCode !== null) resolve();
      else child.once("exit", resolve);
      setTimeout(resolve, 2_000);
    });
  }

  if (child.exitCode && child.exitCode !== 0) throw new Error(`HTTP MCP server failed:\n${stderr}`);
}

async function testHttpSafety() {
  const unsafe = spawn(nodeCommand, [entry, "--transport", "http", "--host", "0.0.0.0"], {
    cwd: root,
    stdio: ["ignore", "ignore", "pipe"],
    windowsHide: true
  });
  let unsafeError = "";
  unsafe.stderr.on("data", (chunk) => { unsafeError += chunk.toString(); });
  await new Promise((resolve) => unsafe.once("exit", resolve));
  if (unsafe.exitCode === 0 || !unsafeError.includes("Non-loopback HTTP requires")) {
    throw new Error(`Non-loopback safety guard failed: ${unsafeError}`);
  }

  const port = 18_767;
  const token = "terminalhost-mcp-integration-test-token";
  const protectedServer = spawn(nodeCommand, [entry, "--transport", "http", "--port", String(port)], {
    cwd: root,
    env: { ...process.env, TERMINALHOST_MCP_AUTH_TOKEN: token },
    stdio: ["ignore", "ignore", "pipe"],
    windowsHide: true
  });
  try {
    await waitForHealth(`http://127.0.0.1:${port}/health`, protectedServer);
    const response = await fetch(`http://127.0.0.1:${port}/mcp`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: "{}"
    });
    if (response.status !== 401) throw new Error(`Expected HTTP 401, received ${response.status}`);
  } finally {
    protectedServer.kill("SIGTERM");
    await new Promise((resolve) => {
      if (protectedServer.exitCode !== null) resolve();
      else protectedServer.once("exit", resolve);
      setTimeout(resolve, 2_000);
    });
  }
  console.log("http-safety: non-loopback guard and Bearer authentication ok");
}

await testStdio();
await testHttp();
await testHttpSafety();
console.log("MCP protocol smoke tests passed.");
