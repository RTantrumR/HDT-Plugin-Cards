@echo off
setlocal enabledelayedexpansion
title HSBG Card Lookup - Installer

set "SRC=%~dp0HsbgCardLookup"
set "PLUGINS=%APPDATA%\HearthstoneDeckTracker\Plugins"
set "DEST=%PLUGINS%\HsbgCardLookup"

echo(
echo   HSBG Card Lookup - HDT plugin installer
echo   =======================================
echo(

REM --- Verify the plugin folder is sitting next to this installer ---
if not exist "%SRC%\HsbgCardLookup.dll" (
  echo   ERROR: Could not find the "HsbgCardLookup" folder next to this installer.
  echo(
  echo   Please EXTRACT the whole zip first ^(right-click ^> Extract All^),
  echo   then run install.bat from the extracted folder.
  echo(
  pause
  exit /b 1
)

REM --- Close HDT if it's running, so the plugin files aren't locked ---
tasklist /fi "imagename eq HearthstoneDeckTracker.exe" 2>nul | find /i "HearthstoneDeckTracker.exe" >nul
if not errorlevel 1 (
  echo   Closing Hearthstone Deck Tracker...
  taskkill /im HearthstoneDeckTracker.exe /f >nul 2>&1
  timeout /t 2 /nobreak >nul
)

echo   Installing to:
echo     %DEST%
echo(

if not exist "%PLUGINS%" mkdir "%PLUGINS%"

REM robocopy /E copies the whole folder (overwriting old files). Exit codes 0-7 = success.
robocopy "%SRC%" "%DEST%" /E /R:2 /W:1 /NJH /NJS /NDL /NP >nul
if %ERRORLEVEL% GEQ 8 (
  echo   ERROR: Copy failed. Try closing HDT manually and run install.bat again.
  echo(
  pause
  exit /b 1
)

echo   Done! HSBG Card Lookup is installed.
echo(
echo   If asked on first launch, enable the plugin under Options ^> Plugins.
echo   Press F3 in-game to open the card search.
echo   ^(First launch downloads card art in the background - about 200 MB, one time.^)
echo(

REM Offer to relaunch HDT right away (it was closed above if it was running).
set "HDTEXE=%LOCALAPPDATA%\HearthstoneDeckTracker\HearthstoneDeckTracker.exe"
if exist "%HDTEXE%" (
  choice /c YN /m "  Start Hearthstone Deck Tracker now"
  if not errorlevel 2 start "" "%HDTEXE%"
) else (
  echo   Next: start Hearthstone Deck Tracker.
  pause
)
exit /b 0
