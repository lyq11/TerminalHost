@echo off
"%~dp0runtime\node.exe" "%~dp0mcp\dist\src\index.js" --transport http --host 127.0.0.1 --port 8766
