@echo off
setlocal
title Tantrum's Battlegrounds Toolkit - Installer

set "SRC=%~dp0HsbgCardLookup"
set "DATADIR=%APPDATA%\HearthstoneDeckTracker"
set "DEST=%DATADIR%\Plugins\HsbgCardLookup"
set "HDTEXE=%LOCALAPPDATA%\HearthstoneDeckTracker\HearthstoneDeckTracker.exe"
set "LOG=%TEMP%\HsbgCardLookup-install.log"
set "PS=powershell -NoProfile -ExecutionPolicy Bypass -Command"
set "WASRUNNING=0"

echo(
echo   Tantrum's Battlegrounds Toolkit - HDT plugin installer
echo   ======================================================
echo(

REM --- 1. The plugin folder must sit next to this installer. If it is missing the zip was not
REM        extracted: Explorer's zip preview runs the .bat alone from a temp folder.
if not exist "%SRC%\HsbgCardLookup.dll" goto :no_source

REM --- 2. HDT must be installed. Its data folder appears on first start; its program lives in
REM        %LOCALAPPDATA% (Squirrel layout: a launcher at the root plus app-<ver> folders).
if not exist "%DATADIR%" if not exist "%HDTEXE%" goto :no_hdt

REM --- 3. Close HDT if it is running: ask nicely first (lets it save and unload plugins), wait up
REM        to 10 s, force only if it ignores that, then confirm it is really gone. Copying while
REM        HDT is alive fails on the locked DLL, and starting HDT next to a live instance makes the
REM        new one wait on the old one instead of starting.
%PS% "$p = Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue; if (-not $p) { exit 0 }; Write-Host '  Closing Hearthstone Deck Tracker...'; $p | ForEach-Object { $_.CloseMainWindow() | Out-Null }; $p | ForEach-Object { if (-not $_.WaitForExit(10000)) { Write-Host '  ...it did not close by itself, forcing it'; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }; Start-Sleep -Milliseconds 700; if (Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue) { exit 2 }; exit 1"
if errorlevel 2 goto :still_running
if errorlevel 1 set "WASRUNNING=1"

REM --- 4. Copy the whole folder over the old one. robocopy exit codes 0-7 mean success.
echo   Installing to:
echo     %DEST%
echo(
if not exist "%DEST%" mkdir "%DEST%"
robocopy "%SRC%" "%DEST%" /E /R:2 /W:1 /NJH /NJS /NDL /NP /LOG:"%LOG%" >nul
set "RC=%ERRORLEVEL%"
if %RC% GEQ 8 goto :copy_failed

REM --- 5. HDT 1.57+ runs plugins from a per-version copy under %LOCALAPPDATA% that it refreshes
REM        from this folder at startup - but only files with a NEWER timestamp. robocopy keeps the
REM        zip's build dates, so an install over a newer-dated copy would be silently ignored.
REM        Stamping every installed file "now" makes HDT always pick the new files up.
%PS% "Get-ChildItem -LiteralPath $env:DEST -Recurse -File | ForEach-Object { $_.LastWriteTime = Get-Date }"
if errorlevel 1 echo   Warning: could not refresh file timestamps. If HDT keeps the old version, restart it once more.

REM --- 6. Verify: every DLL we shipped must now be at the destination with the same size.
REM        A wrong folder, a failed overwrite or an antivirus quarantine all show up here.
set "BAD="
for %%F in ("%SRC%\*.dll") do call :check "%%~nxF" %%~zF
if defined BAD goto :verify_failed

echo   INSTALLED OK
echo(
echo   If asked on first launch, enable the plugin under Options ^> Plugins.
echo   Press F3 in-game to open the card search.
echo   ^(First launch downloads card art in the background - about 200 MB, one time.^)
echo(

REM --- 7. Put HDT back if we closed it, otherwise offer to start it. Never start it next to a
REM        live instance.
if not exist "%HDTEXE%" goto :no_launcher
if "%WASRUNNING%"=="1" goto :launch
choice /c YN /m "  Start Hearthstone Deck Tracker now"
if errorlevel 2 goto :done
:launch
%PS% "if (Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue) { exit 1 }"
if errorlevel 1 goto :already_running
echo   Starting Hearthstone Deck Tracker...
start "" "%HDTEXE%"
goto :done

:check
if not exist "%DEST%\%~1" (set "BAD=%~1" & goto :eof)
for %%G in ("%DEST%\%~1") do if not "%%~zG"=="%~2" set "BAD=%~1"
goto :eof

:no_launcher
echo   Could not find the HDT program at "%HDTEXE%" - start Hearthstone Deck Tracker yourself.
goto :done

:already_running
echo   Hearthstone Deck Tracker is already running - restart it to load the plugin.
goto :done

:done
echo(
pause
exit /b 0

:no_source
echo   Looked for the plugin files in:
echo     "%SRC%"
echo(
echo   Please EXTRACT the whole zip first ^(right-click ^> Extract All^), then run
echo   install.bat from the extracted folder, next to the HsbgCardLookup folder.
set "REASON=the HsbgCardLookup folder was not found next to install.bat"
goto :fail

:no_hdt
echo   Found neither HDT's data folder "%DATADIR%"
echo   nor its program "%HDTEXE%".
echo   Install Hearthstone Deck Tracker, start it once, then run install.bat again.
set "REASON=Hearthstone Deck Tracker is not installed"
goto :fail

:still_running
echo   Hearthstone Deck Tracker is still running and could not be closed from here
echo   ^(it may be running as administrator^). Close it yourself, then run install.bat again.
set "REASON=could not close Hearthstone Deck Tracker"
goto :fail

:copy_failed
echo   robocopy reported exit code %RC%. Its output:
echo(
type "%LOG%"
echo(
echo   Access denied usually means a file is still in use or blocked by antivirus.
set "REASON=copying the files failed"
goto :fail

:verify_failed
echo   "%DEST%\%BAD%" is missing or has the wrong size.
echo   If your antivirus removed it, add the plugin folder to its exclusions and run install.bat again.
set "REASON=the installed files do not match the package"
goto :fail

:fail
echo(
echo   INSTALL FAILED: %REASON%
echo(
pause
exit /b 1
