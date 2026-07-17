$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet restore .\TerminalHost.sln
dotnet build .\TerminalHost.sln -c Release
Write-Host "Build completed: src\TerminalHost.App\bin\Release"
