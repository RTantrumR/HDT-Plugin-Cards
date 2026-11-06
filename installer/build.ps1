<#
.SYNOPSIS
  Build installer\out\TantrumsBattlegroundsToolkit-Setup-<version>.exe with Inno Setup 6.

.DESCRIPTION
  Builds Release (unless -SkipBuild), stages what the plugin folder needs — the plugin DLL, the
  bundled WebP-decoder DLLs and data\ (the same set package.ps1 zips) — into installer\stage, then
  compiles HsbgCardLookup.iss. The version defaults to what Plugin.cs reports, like package.ps1.
  package.ps1 calls this with -SkipBuild after its own build and copies the result into dist\.

  Inno Setup 6: winget install JRSoftware.InnoSetup
#>
param(
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path $here -Parent

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

if (-not $Version) {
    $pluginCs = Join-Path $repo "HsbgCardLookup\Plugin.cs"
    $m = [regex]::Match((Get-Content -Raw $pluginCs), 'Version\s*=>\s*new\s+Version\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)')
    if (-not $m.Success) { throw "Could not read plugin Version from $pluginCs" }
    $Version = "{0}.{1}.{2}" -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value
    Write-Host "Version (from Plugin.cs): $Version" -ForegroundColor Cyan
}

if (-not $SkipBuild) {
    $msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }
    Write-Host "Building Release..." -ForegroundColor Cyan
    & $msbuild "$repo\HsbgCardLookup.sln" /t:Rebuild /p:Configuration=Release /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$bin = "$repo\HsbgCardLookup\bin\Release"
if (-not (Test-Path "$bin\HsbgCardLookup.dll")) { throw "DLL not found at $bin\HsbgCardLookup.dll" }
if (-not (Test-Path "$bin\data\cards.json")) { throw "data\cards.json missing from build output" }

# HDT's own assemblies are Private=False, so bin holds only our DLL + the bundled runtime deps.
# The PDB is omitted.
$stage = Join-Path $here "stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item "$bin\*.dll" $stage -Force
Copy-Item "$bin\data" $stage -Recurse -Force

& $iscc "/DAppVersion=$Version" "/Qp" (Join-Path $here "HsbgCardLookup.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)" }

$exe = Join-Path $here "out\TantrumsBattlegroundsToolkit-Setup-$Version.exe"
if (-not (Test-Path $exe)) { throw "Setup not found at $exe" }
Write-Host ("Built: {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
Get-Item $exe
