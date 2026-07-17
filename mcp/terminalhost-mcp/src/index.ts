#!/usr/bin/env node
import { timingSafeEqual } from "node:crypto";
import type { Server as HttpServer } from "node:http";
import { createMcpExpressApp } from "@modelcontextprotocol/sdk/server/express.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { NextFunction, Request, Response } from "express";
import { createTerminalHostMcpServer } from "./mcp-server.js";
import { TerminalHostClient } from "./terminalhost-client.js";

interface Options {
  transport: "stdio" | "http";
  host: string;
  port: number;
  authToken?: string;
  allowedHosts?: string[];
}

function argument(name: string): string | undefined {
  const prefix = `--${name}=`;
  const inline = process.argv.find((item) => item.startsWith(prefix));
  if (inline) return inline.slice(prefix.length);
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 ? process.argv[index + 1] : undefined;
}

function parseOptions(): Options {
  const transport = (argument("transport") ?? process.env.TERMINALHOST_MCP_TRANSPORT ?? "stdio").toLowerCase();
  if (transport !== "stdio" && transport !== "http") throw new Error("--transport must be stdio or http.");

  const host = argument("host") ?? process.env.TERMINALHOST_MCP_HOST ?? "127.0.0.1";
  const port = Number.parseInt(argument("port") ?? process.env.TERMINALHOST_MCP_PORT ?? "8766", 10);
  if (!Number.isInteger(port) || port < 1 || port > 65_535) throw new Error("MCP HTTP port must be between 1 and 65535.");

  const authToken = argument("auth-token") ?? process.env.TERMINALHOST_MCP_AUTH_TOKEN;
  const allowedHostsText = argument("allowed-hosts") ?? process.env.TERMINALHOST_MCP_ALLOWED_HOSTS;
  const allowedHosts = allowedHostsText?.split(",").map((item) => item.trim()).filter(Boolean);
  const isLoopback = ["127.0.0.1", "localhost", "::1"].includes(host);
  if (!isLoopback && (!authToken || !allowedHosts?.length)) {
    throw new Error("Non-loopback HTTP requires both --auth-token and --allowed-hosts.");
  }

  return { transport, host, port, authToken, allowedHosts };
}

function tokenMatches(actual: string | undefined, expected: string): boolean {
  if (!actual) return false;
  const prefix = "Bearer ";
  if (!actual.startsWith(prefix)) return false;
  const supplied = Buffer.from(actual.slice(prefix.length), "utf8");
  const wanted = Buffer.from(expected, "utf8");
  return supplied.length === wanted.length && timingSafeEqual(supplied, wanted);
}

async function runStdio(client: TerminalHostClient): Promise<McpServer> {
  const server = createTerminalHostMcpServer(client);
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("TerminalHost MCP server is running over stdio.");
  return server;
}

async function runHttp(client: TerminalHostClient, options: Options): Promise<HttpServer> {
  const app = createMcpExpressApp({ host: options.host, allowedHosts: options.allowedHosts });

  if (options.authToken) {
    app.use("/mcp", (req: Request, res: Response, next: NextFunction) => {
      if (!tokenMatches(req.header("authorization"), options.authToken!)) {
        res.status(401).json({ error: "Unauthorized" });
        return;
      }
      next();
    });
  }

  app.get("/health", (_req, res) => {
    res.json({ ok: true, service: "terminalhost-mcp", transport: "streamable-http" });
  });

  app.post("/mcp", async (req, res) => {
    const server = createTerminalHostMcpServer(client);
    const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined });
    try {
      await server.connect(transport);
      await transport.handleRequest(req, res, req.body);
      res.on("close", () => {
        void transport.close();
        void server.close();
      });
    } catch (error) {
      console.error("MCP HTTP request failed:", error);
      if (!res.headersSent) {
        res.status(500).json({ jsonrpc: "2.0", error: { code: -32603, message: "Internal server error" }, id: null });
      }
    }
  });

  app.get("/mcp", (_req, res) => {
    res.status(405).json({ jsonrpc: "2.0", error: { code: -32000, message: "Method not allowed" }, id: null });
  });
  app.delete("/mcp", (_req, res) => {
    res.status(405).json({ jsonrpc: "2.0", error: { code: -32000, message: "Method not allowed" }, id: null });
  });

  return await new Promise<HttpServer>((resolve, reject) => {
    const httpServer = app.listen(options.port, options.host, () => {
      console.error(`TerminalHost MCP Streamable HTTP server: http://${options.host}:${options.port}/mcp`);
      resolve(httpServer);
    });
    httpServer.once("error", reject);
  });
}

async function main(): Promise<void> {
  const options = parseOptions();
  const client = new TerminalHostClient();
  let mcpServer: McpServer | undefined;
  let httpServer: HttpServer | undefined;
  let shuttingDown = false;

  if (options.transport === "stdio") mcpServer = await runStdio(client);
  else httpServer = await runHttp(client, options);

  const shutdown = async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    if (mcpServer) await mcpServer.close();
    if (httpServer) await new Promise<void>((resolve) => httpServer!.close(() => resolve()));
    await client.close();
  };

  process.once("SIGINT", () => { void shutdown().finally(() => process.exit(0)); });
  process.once("SIGTERM", () => { void shutdown().finally(() => process.exit(0)); });
}

main().catch((error) => {
  console.error(error instanceof Error ? error.stack ?? error.message : error);
  process.exit(1);
});
