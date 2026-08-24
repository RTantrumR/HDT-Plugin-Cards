<#
.SYNOPSIS
  One-time setup after cloning: copy Hearthstone Deck Tracker's own assemblies into libs\.

.DESCRIPTION
  Everything else needed to build is committed to this repo. HDT's assemblies are not: they are the
  host application's binaries, referenced at build time only (Private=False in the csproj) and never
  shipped with this plugin. This script takes them from your local HDT install -- you need HDT
  installed to run the plugin anyway.

  Run once after cloning. Re-run with -Force after an HDT update to build against the newer API.

.EXAMPLE
  .\setup.ps1
  .\setup.ps1 -AppDir "$env:LOCALAPPDATA\HearthstoneDeckTracker\app-1.56.5" -Force
#>
param(
    [string]$AppDir,   # a specific HDT app-<version> folder; default = the newest installed
    [switch]$Force     # overwrite assemblies already present in libs\
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$libs = Join-Path $root "libs"

# HDT's own assemblies. Referenced at build time only; not redistributed with this plugin.
$hdtAssemblies = @(
    "HearthstoneDeckTracker.exe",   # the host: IPlugin, Core.Game, API.GameEvents, overlay canvas
    "HearthDb.dll",                 # card DB / GameTag enums
    "HearthMirror.dll",             # in-process game reads (BG lobby roster, big-card state)
    "Newtonsoft.Json.dll"           # JSON; loaded in-process by HDT
)

# Committed to the repo, so a fresh clone already has them. Checked to give one clear
# "you cannot build yet" answer instead of a wall of MSBuild reference errors.
$committedRefs = @(
    "SixLabors.ImageSharp.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Text.Encoding.CodePages.dll"
)

if (-not $AppDir) {
    $hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
    if (-not (Test-Path $hdtRoot)) {
        throw "Hearthstone Deck Tracker not found at $hdtRoot. Install HDT (https://hsdecktracker.net), or pass -AppDir <path to an app-<version> folder>."
    }
    $apps = Get-ChildItem $hdtRoot -Directory -Filter "app-*" -ErrorAction SilentlyContinue
    if (-not $apps) {
        throw "No app-<version> folder under $hdtRoot. Run HDT once so it unpacks itself, or pass -AppDir."
    }
    # Sort by real version number, not string ("app-1.9.0" must not beat "app-1.56.5").
    $AppDir = ($apps | Sort-Object {
        $v = $null
        if ([version]::TryParse(($_.Name -replace '^app-', ''), [ref]$v)) { $v } else { [version]"0.0.0" }
    } -Descending | Select-Object -First 1).FullName
}

if (-not (Test-Path $AppDir)) { throw "HDT folder not found: $AppDir" }
Write-Host "HDT install: $AppDir" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $libs | Out-Null

$copied = 0
$kept = 0
foreach ($name in $hdtAssemblies) {
    $src = Join-Path $AppDir $name
    $dst = Join-Path $libs $name
    if (-not (Test-Path $src)) {
        throw "$name is missing from $AppDir. Is that a complete HDT install? Pick another with -AppDir."
    }
    if ((Test-Path $dst) -and (-not $Force)) {
        $kept++
        continue
    }
    Copy-Item $src $dst -Force
    $copied++
}

if ($kept -gt 0) {
    Write-Host "$kept assembl$(if ($kept -eq 1) { 'y' } else { 'ies' }) already in libs\ (re-run with -Force to refresh)." -ForegroundColor Yellow
}
Write-Host "Copied $copied of $($hdtAssemblies.Count) HDT assemblies into libs\." -ForegroundColor Green

$missing = @()
foreach ($name in ($hdtAssemblies + $committedRefs)) {
    if (-not (Test-Path (Join-Path $libs $name))) { $missing += $name }
}

Get-ChildItem $libs -File | Sort-Object Name | ForEach-Object {
    [PSCustomObject]@{
        Assembly = $_.Name
        Version  = (Get-Item $_.FullName).VersionInfo.FileVersion
        Size     = "{0:N0} KB" -f ($_.Length / 1KB)
    }
} | Format-Table -AutoSize

if ($missing.Count -gt 0) {
    Write-Host "Still missing from libs\: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "The committed ones ship with the repo -- restore them with: git checkout -- libs" -ForegroundColor Red
    exit 1
}

Write-Host "Ready to build:" -ForegroundColor Green
Write-Host "  .\deploy.ps1     build Release + install into HDT's Plugins folder (restarts HDT)"
Write-Host "  .\package.ps1    build Release + dist\HsbgCardLookup-v<ver>.zip"
