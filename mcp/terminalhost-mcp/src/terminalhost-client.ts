import { EventEmitter } from "node:events";
import { randomUUID } from "node:crypto";
import WebSocket, { type RawData } from "ws";
import { loadTerminalHostSettings } from "./settings.js";
import type { TerminalHostMessage, TerminalSession } from "./types.js";

interface PendingRequest {
  resolve: (message: TerminalHostMessage) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

function text(raw: RawData): string {
  if (typeof raw === "string") return raw;
  if (raw instanceof ArrayBuffer) return Buffer.from(raw).toString("utf8");
  if (Array.isArray(raw)) return Buffer.concat(raw).toString("utf8");
  return raw.toString("utf8");
}

function readProperty<T>(value: unknown, ...names: string[]): T | undefined {
  if (!value || typeof value !== "object") return undefined;
  const record = value as Record<string, unknown>;
  for (const name of names) {
    if (name in record) return record[name] as T;
  }
  return undefined;
}

export function normalizeSession(value: unknown): TerminalSession {
  return {
    id: String(readProperty(value, "id", "Id") ?? ""),
    shell: String(readProperty(value, "shell", "Shell") ?? ""),
    commandLine: String(readProperty(value, "commandLine", "CommandLine") ?? ""),
    workingDirectory: String(readProperty(value, "workingDirectory", "WorkingDirectory") ?? ""),
    columns: Number(readProperty(value, "columns", "Columns") ?? 0),
    rows: Number(readProperty(value, "rows", "Rows") ?? 0),
    startedAt: String(readProperty(value, "startedAt", "StartedAt") ?? ""),
    isRunning: Boolean(readProperty(value, "isRunning", "IsRunning"))
  };
}

export class TerminalHostClient extends EventEmitter {
  private socket?: WebSocket;
  private connectPromise?: Promise<void>;
  private pending = new Map<string, PendingRequest>();
  private initialSessions: TerminalSession[] = [];
  private closed = false;

  async connect(): Promise<void> {
    if (this.socket?.readyState === WebSocket.OPEN) return;
    if (this.connectPromise) return this.connectPromise;
    if (this.closed) throw new Error("TerminalHost client is closed.");

    this.connectPromise = this.openConnection();
    try {
      await this.connectPromise;
    } finally {
      this.connectPromise = undefined;
    }
  }

  private async openConnection(): Promise<void> {
    const settings = await loadTerminalHostSettings();
    const url = new URL(settings.wsUrl);
    url.searchParams.set("token", settings.apiToken);

    await new Promise<void>((resolve, reject) => {
      const socket = new WebSocket(url);
      let receivedHello = false;
      const handshakeTimer = setTimeout(() => {
        socket.terminate();
        reject(new Error("Timed out while connecting to TerminalHost."));
      }, 10_000);

      socket.on("message", (raw) => {
        let message: TerminalHostMessage;
        try {
          message = JSON.parse(text(raw)) as TerminalHostMessage;
        } catch {
          return;
        }

        if (!receivedHello) {
          if (message.type !== "hello") {
            clearTimeout(handshakeTimer);
            socket.terminate();
            reject(new Error(`Expected TerminalHost hello, received ${message.type}.`));
            return;
          }
          receivedHello = true;
          clearTimeout(handshakeTimer);
          this.socket = socket;
          this.initialSessions = (message.sessions ?? []).map(normalizeSession);
          socket.send(JSON.stringify({ action: "identify", requestId: `identify-${randomUUID()}`, client: "mcp" }));
          resolve();
          return;
        }

        this.handleMessage(message);
      });

      socket.once("error", (error) => {
        if (!receivedHello) {
          clearTimeout(handshakeTimer);
          reject(new Error(`Unable to connect to TerminalHost: ${error.message}`));
        }
      });

      socket.on("close", () => {
        clearTimeout(handshakeTimer);
        if (this.socket === socket) this.socket = undefined;
        const error = new Error("TerminalHost WebSocket connection closed.");
        for (const request of this.pending.values()) {
          clearTimeout(request.timer);
          request.reject(error);
        }
        this.pending.clear();
        this.emit("disconnected", error);
        if (!receivedHello) reject(error);
      });
    });
  }

  private handleMessage(message: TerminalHostMessage): void {
    if (message.requestId) {
      const request = this.pending.get(message.requestId);
      if (request) {
        clearTimeout(request.timer);
        this.pending.delete(message.requestId);
        if (message.type === "error") request.reject(new Error(message.error || "TerminalHost request failed."));
        else request.resolve(message);
      }
    }

    this.emit("message", message);
    // EventEmitter treats the name "error" specially and terminates the
    // process when it has no listener. Request errors are already delivered to
    // the matching pending promise above, so only emit a standalone error event
    // when a consumer explicitly subscribed to it.
    if (message.type !== "error" || this.listenerCount("error") > 0)
      this.emit(message.type, message);
  }

  async request(action: string, fields: Record<string, unknown> = {}, timeoutMs = 30_000): Promise<TerminalHostMessage> {
    await this.connect();
    const socket = this.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) throw new Error("TerminalHost is not connected.");

    const requestId = `mcp-${randomUUID()}`;
    const response = new Promise<TerminalHostMessage>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`TerminalHost ${action} request timed out after ${timeoutMs} ms.`));
      }, timeoutMs);
      this.pending.set(requestId, { resolve, reject, timer });
    });

    try {
      socket.send(JSON.stringify({ action, requestId, ...fields }));
    } catch (error) {
      const pending = this.pending.get(requestId);
      if (pending) clearTimeout(pending.timer);
      this.pending.delete(requestId);
      throw error;
    }

    return response;
  }

  async listSessions(): Promise<TerminalSession[]> {
    const response = await this.request("list");
    return (response.sessions ?? []).map(normalizeSession);
  }

  async findSession(sessionId: string): Promise<TerminalSession> {
    const sessions = await this.listSessions();
    const session = sessions.find((item) => item.id.toLowerCase() === sessionId.toLowerCase());
    if (!session) throw new Error(`Terminal session does not exist: ${sessionId}`);
    return session;
  }

  getConnectedSessions(): readonly TerminalSession[] {
    return this.initialSessions;
  }

  async close(): Promise<void> {
    this.closed = true;
    const socket = this.socket;
    this.socket = undefined;
    if (!socket) return;

    await new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        socket.terminate();
        resolve();
      }, 1_000);
      socket.once("close", () => {
        clearTimeout(timer);
        resolve();
      });
      socket.close(1000, "MCP server closing");
    });
  }
}
