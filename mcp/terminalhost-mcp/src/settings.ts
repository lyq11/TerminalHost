import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { homedir } from "node:os";
import type { TerminalHostSettings } from "./types.js";

interface StoredSettings {
  ApiPort?: number;
  ApiToken?: string;
  apiPort?: number;
  apiToken?: string;
}

function defaultSettingsPath(): string {
  const localAppData = process.env.LOCALAPPDATA;
  if (localAppData) return join(localAppData, "TerminalHost", "settings.json");

  // This fallback mainly helps compatibility layers and tests. TerminalHost itself is Windows-only.
  return join(homedir(), ".terminalhost", "settings.json");
}

export async function loadTerminalHostSettings(): Promise<TerminalHostSettings> {
  const explicitUrl = process.env.TERMINALHOST_WS_URL?.trim();
  const explicitToken = process.env.TERMINALHOST_API_TOKEN?.trim();

  if (explicitUrl && explicitToken) {
    return { apiPort: Number(new URL(explicitUrl).port || 80), apiToken: explicitToken, wsUrl: explicitUrl };
  }

  const path = process.env.TERMINALHOST_SETTINGS_PATH?.trim() || defaultSettingsPath();
  let stored: StoredSettings;
  try {
    stored = JSON.parse(await readFile(path, "utf8")) as StoredSettings;
  } catch (error) {
    throw new Error(`Unable to read TerminalHost settings at ${path}: ${error instanceof Error ? error.message : String(error)}`);
  }

  const apiPort = stored.ApiPort ?? stored.apiPort;
  const apiToken = stored.ApiToken ?? stored.apiToken;
  if (!Number.isInteger(apiPort) || (apiPort ?? 0) <= 0 || !apiToken || apiToken.length < 20) {
    throw new Error(`TerminalHost settings are invalid: ${path}`);
  }

  return {
    apiPort: apiPort!,
    apiToken,
    wsUrl: `ws://127.0.0.1:${apiPort}/ws`
  };
}
