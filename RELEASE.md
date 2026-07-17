# Release packaging

TerminalHost releases are portable Windows packages containing both the desktop application and the standard MCP server.

## Package contents

- Self-contained `win-x64` TerminalHost application (no separate .NET installation required)
- Compiled TerminalHost MCP server
- MCP production dependencies
- Bundled Node.js runtime
- Launchers for TerminalHost, MCP stdio, and local Streamable HTTP
- Configuration examples for Codex and JSON-based MCP clients

## Build locally

```powershell
.\scripts\package-release.ps1 -Version 1.0.0
```

The archive and its SHA-256 checksum are written to `publish\release`.

## Publish

Push a `v*` tag. The GitHub Actions release workflow builds the portable archive and attaches it to a GitHub Release automatically.

```powershell
git tag v1.0.0
git push origin v1.0.0
```
