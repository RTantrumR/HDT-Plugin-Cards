<#
.SYNOPSIS
  Build installer\out\TantrumsBattlegroundsToolkit-Setup-<version>.exe with Inno Setup 6.

.DESCRIPTION
  Builds Release (unless -SkipBuild), stages what the plugin folder needs — the plugin DLL, the
  bundled WebP-decoder DLLs and data\ (the same set package.ps1 zips) — into installer\stage, then
  compiles HsbgCardLookup.iss. The version defaults to what Plugin.cs reports, like package.ps1.
  The wizard's texts in its 12 languages come from installer\i18n\<lang>.json, turned into
  out\messages.iss here — compile the .iss through this script, not on its own.
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
    # The standalone exe (not in the solution; it builds the Release plugin it references itself).
    & $msbuild "$repo\Standalone\Standalone.csproj" /t:Rebuild /p:Configuration=Release /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Standalone build failed (exit $LASTEXITCODE)." }
}

$bin = "$repo\HsbgCardLookup\bin\Release"
if (-not (Test-Path "$bin\HsbgCardLookup.dll")) { throw "DLL not found at $bin\HsbgCardLookup.dll" }
if (-not (Test-Path "$bin\data\cards.json")) { throw "data\cards.json missing from build output" }
$standaloneBin = "$repo\Standalone\bin\Release"
if (-not (Test-Path "$standaloneBin\TantrumsToolkitStandalone.exe")) { throw "Standalone exe not found in $standaloneBin" }

# Two sets, one per mode. Plugin: our DLL + the bundled runtime deps (HDT's own assemblies are
# Private=False) + data\. Standalone: the exe, its config, every DLL next to it (the plugin assembly,
# Json.NET, the WebP decoder), data\ and the icon. No PDBs, logs or test leftovers.
$stage = Join-Path $here "stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$stage\plugin", "$stage\standalone" | Out-Null
Copy-Item "$bin\*.dll" "$stage\plugin" -Force
Copy-Item "$bin\data" "$stage\plugin" -Recurse -Force
Copy-Item "$standaloneBin\TantrumsToolkitStandalone.exe", "$standaloneBin\TantrumsToolkitStandalone.exe.config", "$standaloneBin\toolkit.ico" "$stage\standalone" -Force
Copy-Item "$standaloneBin\*.dll" "$stage\standalone" -Force
Copy-Item "$standaloneBin\data" "$stage\standalone" -Recurse -Force

# The language page's flags: the plugin's own Ui\Flags.cs rendered to BMPs (out\flags) by
# tools\RenderFlags.cs, so the wizard shows the same marks as the plugin and the site.
$outDir = Join-Path $here "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$ref = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"
$csc = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
& $csc /nologo /target:exe "/out:$outDir\RenderFlags.exe" "/r:$ref\PresentationCore.dll" "/r:$ref\PresentationFramework.dll" `
    "/r:$ref\WindowsBase.dll" "/r:$ref\System.Xaml.dll" (Join-Path $here "tools\RenderFlags.cs")
if ($LASTEXITCODE -ne 0) { throw "RenderFlags did not compile (exit $LASTEXITCODE)" }
Remove-Item "$outDir\flags" -Recurse -Force -ErrorAction SilentlyContinue
& "$outDir\RenderFlags.exe" "$bin\HsbgCardLookup.dll" "$outDir\flags"
if ($LASTEXITCODE -ne 0) { throw "RenderFlags failed (exit $LASTEXITCODE)" }

# The wizard's own texts: installer\i18n\<lang>.json -> out\messages.iss, one language-qualified
# [Messages] / [CustomMessages] line per key (HsbgCardLookup.iss #includes it). A language file
# missing a key falls back to English for that key, with a warning.
$innoNames = [ordered]@{
    "en" = "english"; "de" = "german"; "es" = "spanish"; "fr" = "french"; "it" = "italian"; "ja" = "japanese";
    "ko" = "korean"; "pl" = "polish"; "pt" = "brazilianportuguese"; "th" = "thai";
    "zh-cn" = "chinesesimplified"; "zh-tw" = "chinesetraditional"
}
function Read-Texts([string]$code) {
    $f = Join-Path $here "i18n\$code.json"
    if (-not (Test-Path $f)) { return $null }
    return (Get-Content -Raw -Encoding UTF8 $f | ConvertFrom-Json)
}
$en = Read-Texts "en"
if (-not $en) { throw "installer\i18n\en.json missing" }
$sections = @{ "messages" = "[Messages]"; "custom" = "[CustomMessages]" }
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("; Generated by installer\build.ps1 from installer\i18n\*.json - edit those, not this.")
foreach ($section in @("messages", "custom")) {
    $lines.Add($sections[$section])
    foreach ($code in $innoNames.Keys) {
        $texts = Read-Texts $code
        if (-not $texts) { Write-Warning "installer\i18n\$code.json missing - English used for $code" }
        foreach ($p in $en.$section.PSObject.Properties) {
            $value = $null
            if ($texts -and $texts.$section) { $value = $texts.$section.($p.Name) }
            if ([string]::IsNullOrEmpty($value)) {
                if ($texts) { Write-Warning "$code.json has no $section.$($p.Name) - English used" }
                $value = $p.Value
            }
            if ($value -match "[`r`n]") { throw "$code.json $section.$($p.Name) holds a raw line break; use %n" }
            $lines.Add(("{0}.{1}={2}" -f $innoNames[$code], $p.Name, $value))
        }
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $here "out") | Out-Null
[IO.File]::WriteAllLines((Join-Path $here "out\messages.iss"), $lines, (New-Object Text.UTF8Encoding($true)))

& $iscc "/DAppVersion=$Version" "/Qp" (Join-Path $here "HsbgCardLookup.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)" }

$exe = Join-Path $here "out\TantrumsBattlegroundsToolkit-Setup-$Version.exe"
if (-not (Test-Path $exe)) { throw "Setup not found at $exe" }
Write-Host ("Built: {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
Get-Item $exe
