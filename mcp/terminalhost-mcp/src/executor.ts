import { randomUUID } from "node:crypto";
import type { TerminalHostClient } from "./terminalhost-client.js";
import type { ExecuteResult, TerminalHostMessage, TerminalSession } from "./types.js";

const ANSI_PATTERN = /[\u001B\u009B][[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d\/#&.:=?%@~_]+)*)?\u0007)|(?:(?:\d{1,4}(?:[;:]\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))/g;

function stripTerminalControl(text: string): string {
  return text
    .replace(ANSI_PATTERN, "")
    .replace(/\r/g, "")
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001A\u001C-\u001F]/g, "");
}

function commandWithMarker(session: TerminalSession, command: string, marker: string): string {
  if (session.shell === "cmd") {
    const brokenMarker = marker.replace("_DONE_", "_^DONE_");
    // Queue the marker as the next command so %ERRORLEVEL% is expanded only after the target command finishes.
    // The caret breaks the marker in the echoed input but disappears in the actual echo output.
    return `${command}\recho ${brokenMarker}:%ERRORLEVEL%\r`;
  }

  const markerPrefix = marker.slice(0, marker.indexOf("DONE_"));
  const markerSuffix = marker.slice(marker.indexOf("DONE_"));
  return [
    `$__thm='${markerPrefix}'+'${markerSuffix}'`,
    "$global:LASTEXITCODE=0",
    `& { ${command} }`,
    "$__thok=$?",
    "$__thec=$LASTEXITCODE",
    "if ($null -eq $__thec) { $__thec=if ($__thok) { 0 } else { 1 } }",
    "if (-not $__thok -and $__thec -eq 0) { $__thec=1 }",
    "Write-Output ($__thm+':'+$__thec)"
  ].join("; ") + "\r";
}

export class TerminalCommandExecutor {
  private readonly locks = new Map<string, Promise<void>>();

  constructor(private readonly client: TerminalHostClient) {}

  async execute(
    sessionId: string,
    command: string,
    timeoutMs: number,
    maxOutputChars: number,
    plainText: boolean
  ): Promise<ExecuteResult> {
    return this.withSessionLock(sessionId, async () => {
      const session = await this.client.findSession(sessionId);
      if (!session.isRunning) throw new Error(`Terminal session is not running: ${sessionId}`);

      const nonce = randomUUID().replaceAll("-", "");
      const marker = `__TH_MCP_DONE_${nonce}__`;
      const markerPattern = new RegExp(`${marker}:(-?\\d+)`);
      const submitted = commandWithMarker(session, command, marker);
      const startedAt = Date.now();
      let rawOutput = "";
      let truncated = false;
      let cancelCompletion: ((error: Error) => void) | undefined;

      const completion = new Promise<{ exitCode: number | null; timedOut: boolean }>((resolve, reject) => {
        let settled = false;
        const onOutput = (message: TerminalHostMessage) => {
          if (message.sessionId?.toLowerCase() !== sessionId.toLowerCase()) return;
          rawOutput += message.data ?? "";
          if (rawOutput.length > maxOutputChars * 2) {
            rawOutput = rawOutput.slice(-maxOutputChars * 2);
            truncated = true;
          }

          const match = markerPattern.exec(rawOutput);
          if (match) finish({ exitCode: Number.parseInt(match[1], 10), timedOut: false });
        };
        const onExited = (message: TerminalHostMessage) => {
          if (message.sessionId?.toLowerCase() === sessionId.toLowerCase()) {
            finish({ exitCode: typeof message.exitCode === "number" ? message.exitCode : null, timedOut: false });
          }
        };
        const onDisconnected = (error: Error) => finish(undefined, error);
        const timer = setTimeout(() => finish({ exitCode: null, timedOut: true }), timeoutMs);

        const cleanup = () => {
          clearTimeout(timer);
          this.client.off("output", onOutput);
          this.client.off("exited", onExited);
          this.client.off("disconnected", onDisconnected);
        };
        const finish = (result?: { exitCode: number | null; timedOut: boolean }, error?: Error) => {
          if (settled) return;
          settled = true;
          cleanup();
          if (error) reject(error);
          else resolve(result!);
        };
        cancelCompletion = (error) => finish(undefined, error);

        this.client.on("output", onOutput);
        this.client.on("exited", onExited);
        this.client.on("disconnected", onDisconnected);
      });

      try {
        await this.client.request("write", { sessionId, data: submitted });
      } catch (error) {
        cancelCompletion?.(error instanceof Error ? error : new Error(String(error)));
        await completion.catch(() => undefined);
        throw error;
      }
      const state = await completion;

      const markerMatch = markerPattern.exec(rawOutput);
      if (markerMatch?.index !== undefined) rawOutput = rawOutput.slice(0, markerMatch.index);
      if (rawOutput.length > maxOutputChars) {
        rawOutput = rawOutput.slice(-maxOutputChars);
        truncated = true;
      }

      return {
        sessionId,
        shell: session.shell,
        command,
        output: plainText ? stripTerminalControl(rawOutput) : rawOutput,
        rawOutput,
        exitCode: state.exitCode,
        timedOut: state.timedOut,
        truncated,
        durationMs: Date.now() - startedAt
      };
    });
  }

  private async withSessionLock<T>(sessionId: string, operation: () => Promise<T>): Promise<T> {
    const key = sessionId.toLowerCase();
    const previous = this.locks.get(key) ?? Promise.resolve();
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const chain = previous.then(() => gate);
    this.locks.set(key, chain);

    await previous;
    try {
      return await operation();
    } finally {
      release();
      if (this.locks.get(key) === chain) this.locks.delete(key);
    }
  }
}
