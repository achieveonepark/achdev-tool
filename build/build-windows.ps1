#!/usr/bin/env pwsh
# Builds a self-contained win-x64 publish of AchDev Tool and packages it into a
# Velopack installer (a self-installing "<AppId>-<version>-win-Setup.exe").
#
# Usage:  pwsh ./build/build-windows.ps1
# Requires: .NET SDK, and `vpk` (installed automatically below if missing).

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "src/AchDevTool/AchDevTool.csproj"
$AppId = "AchDevTool"
$Rid = "win-x64"
$PublishDir = Join-Path $RepoRoot "publish/$Rid"
$ReleaseDir = Join-Path $RepoRoot "releases/$Rid"
$Icon = Join-Path $RepoRoot "src/AchDevTool/Assets/app.ico"

$Version = (Select-String -Path $Project -Pattern '<Version>(.*)</Version>').Matches[0].Groups[1].Value
Write-Host "Building AchDev Tool v$Version for $Rid"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing vpk (Velopack CLI)..."
    dotnet tool install -g vpk
}

if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

dotnet publish $Project -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

vpk pack -u $AppId -v $Version -p $PublishDir -e "AchDevTool.exe" -i $Icon -o $ReleaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host ""
Write-Host "Done. Installer created in: $ReleaseDir"
Get-ChildItem $ReleaseDir -Filter "*Setup.exe" | ForEach-Object { Write-Host " - $($_.Name)" }
