$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$mcpRoot = Join-Path $root "mcp\terminalhost-mcp"

Set-Location $mcpRoot
npm install
npm run build

Write-Host "MCP server built: mcp\terminalhost-mcp\dist\src\index.js"
