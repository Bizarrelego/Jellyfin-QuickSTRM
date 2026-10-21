<#
.SYNOPSIS
    QuickSTRM 1-Step Version Bumper & Synchronizer.
.DESCRIPTION
    Synchronizes QuickSTRM.csproj, meta.json, build.yaml, README.md, and manifest.json
    automatically using CHANGELOG.md as the source of truth, or from a provided version parameter.
.EXAMPLE
    .\scripts\bump.ps1
    (Synchronizes all files to match the latest header in CHANGELOG.md)
.EXAMPLE
    .\scripts\bump.ps1 3.0.2
    (Bumps and synchronizes all files to version 3.0.2)
#>
param(
    [string]$Version,
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

$pythonCmd = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCmd) {
    Write-Error "Python 3 is required to run sync_release.py. Please install Python or ensure it is in PATH."
    exit 1
}

$scriptPath = Join-Path $RepoRoot ".github/scripts/sync_release.py"

Write-Host "==> Synchronizing QuickSTRM Release Metadata..." -ForegroundColor Cyan

$syncArgs = @($scriptPath, "--extract-notes")
if ($Version) {
    $syncArgs += @("--version", $Version)
}

& python $syncArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Version synchronization failed."
    exit $LASTEXITCODE
}

if ($Build) {
    Write-Host "==> Compiling Release Build..." -ForegroundColor Cyan
    $dotnetCmd = "dotnet"
    $userDotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
    if (Test-Path $userDotnet) {
        $dotnetCmd = $userDotnet
    }
    
    & $dotnetCmd publish -c Release -o "$RepoRoot/publish/QuickSTRM"
    
    Copy-Item "$RepoRoot/meta.json" "$RepoRoot/publish/QuickSTRM/" -Force
    Copy-Item "$RepoRoot/build.yaml" "$RepoRoot/publish/QuickSTRM/" -Force
    
    $zipPath = "$RepoRoot/QuickSTRM.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$RepoRoot/publish/QuickSTRM/*" -DestinationPath $zipPath -Force
    
    Write-Host "==> Updating Manifest Checksum & Timestamp..." -ForegroundColor Cyan
    & python $scriptPath --update-checksum "$zipPath"
}

Write-Host "==> Synchronization Complete! All files are in sync." -ForegroundColor Green
