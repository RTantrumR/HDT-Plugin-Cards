<#
.SYNOPSIS
  Build HsbgCardLookup (Release) and deploy the DLL into HDT's Plugins folder.

.DESCRIPTION
  HDT loads plugins from %appdata%\Hearthstone Deck Tracker\Plugins (subfolders supported).
  We deploy into a dedicated subfolder so the DLL + future data/ travel together.
  Restart HDT after running this for it to pick up changes.
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild -- adjust the path for your VS edition." }

Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
& $msbuild "$root\HsbgCardLookup.sln" /t:Build /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$binDir = "$root\HsbgCardLookup\bin\$Configuration"
$dll = "$binDir\HsbgCardLookup.dll"
$pdb = "$binDir\HsbgCardLookup.pdb"
if (-not (Test-Path $dll)) { throw "DLL not found at $dll" }

$dest = Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins\HsbgCardLookup"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
# Copy our DLL plus the bundled runtime deps (SixLabors.ImageSharp + its System.* closure).
# HDT's own assemblies are Private=False so they're not in bin and won't be copied.
Copy-Item "$binDir\*.dll" $dest -Force
if (Test-Path $pdb) { Copy-Item $pdb $dest -Force }

# Bundled data (cards.json etc.) lives in a data\ subfolder next to the DLL.
$srcData = "$root\HsbgCardLookup\bin\$Configuration\data"
if (Test-Path $srcData) {
    $dstData = Join-Path $dest "data"
    New-Item -ItemType Directory -Force -Path $dstData | Out-Null
    Copy-Item "$srcData\*" $dstData -Recurse -Force
}

Write-Host "Deployed to: $dest" -ForegroundColor Green
Get-ChildItem $dest | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "Restart HDT to load the new build." -ForegroundColor Yellow
