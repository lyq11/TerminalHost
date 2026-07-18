param(
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$packageName = "TerminalHost-$Version-$Runtime"
$releaseRoot = Join-Path $root "publish\release"
$stageRoot = Join-Path $releaseRoot $packageName
$appRoot = $stageRoot
$mcpRoot = Join-Path $stageRoot "mcp"
$runtimeRoot = Join-Path $stageRoot "runtime"
$sourceMcpRoot = Join-Path $root "mcp\terminalhost-mcp"
$archivePath = Join-Path $releaseRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

New-Item -ItemType Directory -Path $appRoot, $mcpRoot, $runtimeRoot -Force | Out-Null

dotnet publish (Join-Path $root "src\TerminalHost.App\TerminalHost.App.csproj") `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $appRoot

Push-Location $sourceMcpRoot
try {
    npm ci
    npm run build
}
finally {
    Pop-Location
}

Copy-Item -LiteralPath (Join-Path $sourceMcpRoot "dist") -Destination (Join-Path $mcpRoot "dist") -Recurse
Copy-Item -LiteralPath (Join-Path $sourceMcpRoot "package.json") -Destination $mcpRoot
Copy-Item -LiteralPath (Join-Path $sourceMcpRoot "package-lock.json") -Destination $mcpRoot

Push-Location $mcpRoot
try {
    npm ci --omit=dev --ignore-scripts
    $binShimRoot = Join-Path $mcpRoot "node_modules\.bin"
    if (Test-Path -LiteralPath $binShimRoot) {
        Remove-Item -LiteralPath $binShimRoot -Recurse -Force
    }
}
finally {
    Pop-Location
}

$nodeCommand = Get-Command node.exe -ErrorAction Stop
Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $runtimeRoot "node.exe")

Copy-Item -LiteralPath (Join-Path $root "release-assets\README.txt") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $stageRoot

Compress-Archive -LiteralPath $stageRoot -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$archiveHash  $packageName.zip" -Encoding ascii

Write-Host "Release package created: $archivePath"
Write-Host "SHA256 checksum created: $checksumPath"
Write-Output $archivePath
