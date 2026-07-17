$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Using .NET SDK:"
dotnet --version

Remove-Item -Recurse -Force .\src\TerminalHost.App\bin -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\src\TerminalHost.App\obj -ErrorAction SilentlyContinue

dotnet restore .\TerminalHost.sln --force
dotnet build .\TerminalHost.sln -c Release --no-restore

Write-Host "Build completed: src\TerminalHost.App\bin\Release\net6.0-windows"
