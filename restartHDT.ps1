<#
.SYNOPSIS
  Restart Hearthstone Deck Tracker: close it if running, then relaunch it.

.DESCRIPTION
  HDT scans plugins only at startup (PluginManager.LoadPluginsFromPath), so a fresh deploy isn't
  picked up until HDT restarts. deploy.ps1 calls this as its final step so a build auto-reloads the
  plugin. Safe to run standalone too.

  Launch path: the Squirrel shim %LOCALAPPDATA%\HearthstoneDeckTracker\HearthstoneDeckTracker.exe
  (always points at the newest app-<ver>); falls back to the newest app-* folder's exe.
#>
param(
    [int]$WaitSeconds = 8   # how long to wait for a graceful close before forcing it
)

$ErrorActionPreference = "SilentlyContinue"
$procName = "HearthstoneDeckTracker"

$running = Get-Process -Name $procName -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Closing HDT..." -ForegroundColor Cyan
    # Ask politely first (lets HDT run OnUnload / save), then force whatever's left.
    foreach ($p in $running) { $p.CloseMainWindow() | Out-Null }
    foreach ($p in $running) {
        if (-not $p.WaitForExit($WaitSeconds * 1000)) {
            Write-Host "  (forcing close)" -ForegroundColor DarkYellow
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
    # Brief settle so file locks on the plugin folder are released before relaunch.
    Start-Sleep -Milliseconds 700
} else {
    Write-Host "HDT not running." -ForegroundColor DarkGray
}

$hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
$exe = Join-Path $hdtRoot "HearthstoneDeckTracker.exe"
if (-not (Test-Path $exe)) {
    $newest = Get-ChildItem $hdtRoot -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
              Sort-Object Name -Descending | Select-Object -First 1
    if ($newest) { $exe = Join-Path $newest.FullName "HearthstoneDeckTracker.exe" }
}
if (-not (Test-Path $exe)) {
    Write-Warning "HDT executable not found under $hdtRoot - skipping launch (start HDT manually)."
    return
}

Write-Host "Starting HDT..." -ForegroundColor Cyan
Start-Process -FilePath $exe
Write-Host "HDT relaunched." -ForegroundColor Green
