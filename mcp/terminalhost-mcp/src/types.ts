export interface TerminalHostSettings {
  apiPort: number;
  apiToken: string;
  wsUrl: string;
}

export interface TerminalSession {
  id: string;
  shell: string;
  commandLine: string;
  workingDirectory: string;
  columns: number;
  rows: number;
  startedAt: string;
  isRunning: boolean;
}

export interface TerminalHostMessage {
  type: string;
  requestId?: string | null;
  sessionId?: string;
  data?: string;
  error?: string;
  action?: string;
  exitCode?: number;
  session?: unknown;
  sessions?: unknown[];
  [key: string]: unknown;
}

export interface ExecuteResult {
  sessionId: string;
  shell: string;
  command: string;
  output: string;
  rawOutput: string;
  exitCode: number | null;
  timedOut: boolean;
  truncated: boolean;
  durationMs: number;
}
