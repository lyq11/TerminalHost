$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish .\src\TerminalHost.App\TerminalHost.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o .\publish\win-x64
Write-Host "Published: publish\win-x64\TerminalHost.exe"
