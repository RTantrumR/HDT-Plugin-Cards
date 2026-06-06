<#
.SYNOPSIS
  Build Release and package a drop-in distribution zip for HSBG Card Lookup.

.DESCRIPTION
  Produces dist\HsbgCardLookup-v<Version>.zip. The zip root contains:
    HsbgCardLookup\   (the DLL + bundled WebP-decoder deps + data\ — the plugin itself)
    install.bat       (one-click: closes HDT, copies the folder into the Plugins dir)
    README.txt        (EN install/usage)
    UA_Readme.txt     (UA install/usage)
  Users extract and double-click install.bat (or drag the HsbgCardLookup folder into
  %APPDATA%\HearthstoneDeckTracker\Plugins\ manually). Release excludes the dev-only
  GameStateProbe (it's #if DEBUG).
#>
param([string]$Version)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Default the version to whatever Plugin.cs reports (the `Version` property the GitHub updater compares
# against the release tag), so the zip name can never drift from the shipped build. Pass -Version only
# to override.
if (-not $Version) {
    $pluginCs = Join-Path $root "HsbgCardLookup\Plugin.cs"
    $m = [regex]::Match((Get-Content -Raw $pluginCs), 'Version\s*=>\s*new\s+Version\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)')
    if (-not $m.Success) { throw "Could not read plugin Version from $pluginCs" }
    $Version = "{0}.{1}.{2}" -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value
    Write-Host "Version (from Plugin.cs): $Version" -ForegroundColor Cyan
}
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }

Write-Host "Building Release..." -ForegroundColor Cyan
& $msbuild "$root\HsbgCardLookup.sln" /t:Rebuild /p:Configuration=Release /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$bin = "$root\HsbgCardLookup\bin\Release"
$dll = "$bin\HsbgCardLookup.dll"
if (-not (Test-Path $dll)) { throw "DLL not found at $dll" }
if (-not (Test-Path "$bin\data\cards.json")) { throw "data\cards.json missing from build output" }

# Package layout:
#   dist\pkg\                  <- zipped contents (the extraction root the user sees)
#   dist\pkg\HsbgCardLookup\   <- runtime files only (what lands in the Plugins folder)
$dist  = "$root\dist"
$pkg   = "$dist\pkg"
$stage = "$pkg\HsbgCardLookup"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Our DLL + bundled runtime deps (ImageSharp + System.* closure). HDT's own assemblies are
# Private=False so they aren't in bin and aren't shipped. PDB is omitted.
Copy-Item "$bin\*.dll" $stage -Force
Copy-Item "$bin\data" $stage -Recurse -Force

# install.bat lives at the package root (beside the HsbgCardLookup folder it copies).
Copy-Item "$root\packaging\install.bat" $pkg -Force

# README templates are committed UTF-8 files under packaging\ — copied (NOT embedded as here-strings)
# so non-ASCII (Ukrainian) survives: PowerShell 5.1 parses .ps1 as the ANSI codepage, which mangled
# Cyrillic literals. We read each as UTF-8 and re-emit it as UTF-8 *with BOM* so Notepad on any
# Windows shows Cyrillic correctly. READMEs sit at the package root (visible right after extraction).
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
function Copy-Readme($name) {
    $src = Join-Path $root "packaging\$name"
    if (-not (Test-Path $src)) { throw "Readme template missing: $src" }
    $text = Get-Content -Raw -Encoding UTF8 $src
    [System.IO.File]::WriteAllText((Join-Path $pkg $name), $text, $utf8Bom)
}
Copy-Readme "README.txt"
Copy-Readme "UA_Readme.txt"

$zip = "$dist\HsbgCardLookup-v$Version.zip"
Compress-Archive -Path "$pkg\*" -DestinationPath $zip -Force

$size = "{0:N1} MB" -f ((Get-Item $zip).Length / 1MB)
Write-Host "Packaged: $zip ($size)" -ForegroundColor Green
Write-Host "Zip contents:" -ForegroundColor Cyan
Get-ChildItem $pkg -Recurse -File | ForEach-Object { "  " + $_.FullName.Substring($pkg.Length + 1) }
