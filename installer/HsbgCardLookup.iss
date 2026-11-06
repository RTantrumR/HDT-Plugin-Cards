; Tantrum's Battlegrounds Toolkit — per-user setup for the Hearthstone Deck Tracker plugin.
; Built by installer\build.ps1, which stages the Release build (the plugin DLL, the bundled
; WebP-decoder DLLs and data\) into installer\stage first and passes the version as /DAppVersion.
;
; What it does, in order: closes Hearthstone Deck Tracker gracefully (a WM_CLOSE to its main window
; runs HDT's own shutdown, which writes plugins.xml — a killed HDT never writes it), copies the files
; into %APPDATA%\HearthstoneDeckTracker\Plugins\HsbgCardLookup, and on a PC where the plugin has never
; run switches it on in plugins.xml (HDT starts a newly found plugin OFF and never asks). Then it
; offers to start HDT. Upgrades install over the previous files (same AppId); Apps & features gets an
; uninstall entry. The uninstaller removes the plugin folder only — settings and the art cache in
; %APPDATA%\HearthstoneDeckTracker\HsbgCardLookup stay. HDT's per-version mirror under %LOCALAPPDATA%
; needs no cleanup: HDT's SyncPlugins deletes mirror files that are gone from Roaming at the next start.
;
; Testing: /DIR=<temp>\HearthstoneDeckTracker\Plugins\HsbgCardLookup installs into a throwaway
; profile — every path in [Code] is derived from {app}, never from {userappdata} directly.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppDisplayName "Tantrum's Battlegrounds Toolkit"

[Setup]
AppId={{9B1E4C7A-5D62-4F0B-8E3C-2A7F6D9C1B45}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher=RTantrumR
AppPublisherURL=https://github.com/RTantrumR/HDT-Plugin-Cards
AppSupportURL=https://github.com/RTantrumR/HDT-Plugin-Cards/issues
AppUpdatesURL=https://github.com/RTantrumR/HDT-Plugin-Cards/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={userappdata}\HearthstoneDeckTracker\Plugins\HsbgCardLookup
DisableDirPage=yes
UsePreviousAppDir=no
DirExistsWarning=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UninstallFilesDir={code:DataDir}\uninstall
UninstallDisplayName={#AppDisplayName} (HDT plugin)
UninstallDisplayIcon={uninstallexe}
OutputDir=out
OutputBaseFilename=TantrumsBattlegroundsToolkit-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Messages]
WelcomeLabel2=This will install [name/ver] into Hearthstone Deck Tracker's plugin folder for the current user.%n%nHearthstone Deck Tracker is closed while the files are copied; Setup can start it again at the end.
FinishedLabel=[name] is installed.%n%nPress F3 in a Battlegrounds match to open the card search. The first launch downloads card art in the background (about 200 MB, once).

[Files]
; HDT 1.57+ runs plugins from a per-version mirror that it refreshes from this folder at startup,
; but only files with a NEWER timestamp — "touch" stamps every installed file with the install time,
; so the mirror always picks the new files up (install.bat did the same).
Source: "stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs touch

[InstallDelete]
; The installer replaces the whole set: a DLL or data file that a newer version no longer ships
; must not linger from an older one.
Type: files; Name: "{app}\*.dll"
Type: filesandordirs; Name: "{app}\data"

[Run]
; The Squirrel launcher at the root starts the newest app-<ver>. Never next to a live instance.
Filename: "{localappdata}\HearthstoneDeckTracker\HearthstoneDeckTracker.exe"; Description: "Start Hearthstone Deck Tracker"; Flags: postinstall nowait skipifsilent; Check: HdtExeExists and not HdtRunning

[Code]
const
  WM_CLOSE = $0010;
  HdtProcessName = 'HearthstoneDeckTracker.exe';
  HdtWindowTitle = 'Hearthstone Deck Tracker';
  PluginFileName = 'Plugins/HsbgCardLookup/HsbgCardLookup.dll';

var
  PluginEnabled: Boolean;   // this run wrote the enabled entry into plugins.xml

// {app} = <HDT data dir>\Plugins\HsbgCardLookup; two levels up is HDT's data dir (plugins.xml,
// the plugin's own HsbgCardLookup\ data folder). Derived, so a /DIR test profile stays self-contained.
function HdtDataRoot: String;
begin
  Result := ExtractFileDir(ExtractFileDir(ExpandConstant('{app}')));
end;

function DataDir(Param: String): String;
begin
  Result := HdtDataRoot + '\HsbgCardLookup';
end;

function HdtExeExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\HearthstoneDeckTracker\HearthstoneDeckTracker.exe'));
end;

function HdtRunning: Boolean;
var
  Locator, Wmi, Found: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Wmi.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + HdtProcessName + '''');
    Result := Found.Count > 0;
  except
    Log('HdtRunning: WMI query failed: ' + GetExceptionMessage);
  end;
end;

// Ask HDT to close the way its own Exit does: WM_CLOSE on the main window runs Core.Shutdown,
// which saves config and plugins.xml and unloads the plugins. With HDT's "close to tray" option
// on, that only hides the window — then the user has to exit it from the tray icon (the message
// below says so). Never a kill: a killed HDT does not write plugins.xml.
procedure AskHdtToClose;
var
  H: HWND;
begin
  H := FindWindowByWindowName(HdtWindowTitle);
  if H <> 0 then
  begin
    Log('Asking Hearthstone Deck Tracker to close');
    PostMessage(H, WM_CLOSE, 0, 0);
  end
  else
    Log('Hearthstone Deck Tracker main window not found; waiting for the user to close it');
end;

function WaitForHdtClosed(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while HdtRunning and (Waited < TimeoutMs) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  Result := not HdtRunning;
end;

// True once HDT is not running; False when the user gives up. The first wait covers HDT's own
// shutdown (its PrepareShutdown polls up to ~5 s); each Retry asks again and waits a little.
function EnsureHdtClosed: Boolean;
var
  TimeoutMs: Integer;
begin
  Result := True;
  if not HdtRunning then Exit;
  AskHdtToClose;
  TimeoutMs := 20000;
  while not WaitForHdtClosed(TimeoutMs) do
  begin
    Log('Hearthstone Deck Tracker is still running; asking the user');
    if SuppressibleMsgBox('Hearthstone Deck Tracker is still running.' + #13#10#13#10 +
         'Close it (if its window is hidden: right-click the HDT tray icon > Exit), then click Retry.',
         mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    AskHdtToClose;
    TimeoutMs := 5000;
  end;
  Log('Hearthstone Deck Tracker is closed');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not EnsureHdtClosed then
    Result := 'Hearthstone Deck Tracker is still running. Close it and run Setup again.';
end;

function InitializeUninstall: Boolean;
begin
  Result := EnsureHdtClosed;
end;

// install.bat's rule: the plugin has run on this PC when its settings file exists or its log holds
// an OnLoad line. HDT calls OnUnload on every discovered plugin at shutdown, loaded or not, so
// OnUnload-only lines mean "never enabled".
function PluginEverRan(const Root: String): Boolean;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := FileExists(Root + '\HsbgCardLookup\config.xml');
  if Result then Exit;
  if LoadStringsFromFile(Root + '\HsbgCardLookup\spike.log', Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Pos('  OnLoad', Lines[I]) > 0 then
      begin
        Result := True;
        Exit;
      end;
end;

// Set IsEnabled=true on our PluginSettings entry in plugins.xml, adding the entry (or the whole
// file) when missing. HDT keys the entry by the relative DLL path. HDT is closed at this point;
// it rewrites the file in its own format on its next clean shutdown.
procedure EnablePluginInHdt(const Root: String);
var
  Doc, RootNode, Entry, Child, Found: Variant;
  F: String;
begin
  F := Root + '\plugins.xml';
  Doc := CreateOleObject('MSXML2.DOMDocument.6.0');
  Doc.async := False;
  Doc.resolveExternals := False;
  if FileExists(F) then
  begin
    Doc.load(F);
    if Doc.parseError.errorCode <> 0 then
      RaiseException('plugins.xml does not parse: ' + Doc.parseError.reason);
  end
  else
    Doc.loadXML('<?xml version="1.0" encoding="utf-8"?><ArrayOfPluginSettings />');
  RootNode := Doc.documentElement;
  Found := RootNode.selectNodes('PluginSettings[FileName=''' + PluginFileName + ''']');
  if Found.length > 0 then
  begin
    Entry := Found.item(0);
    Found := Entry.selectNodes('IsEnabled');
    if Found.length > 0 then
      Found.item(0).text := 'true'
    else
    begin
      Child := Doc.createElement('IsEnabled');
      Child.text := 'true';
      Entry.appendChild(Child);
    end;
  end
  else
  begin
    Entry := Doc.createElement('PluginSettings');
    Child := Doc.createElement('FileName');
    Child.text := PluginFileName;
    Entry.appendChild(Child);
    Child := Doc.createElement('IsEnabled');
    Child.text := 'true';
    Entry.appendChild(Child);
    Child := Doc.createElement('Name');
    Child.text := '{#StringChange(AppDisplayName, "'", "''")}';
    Entry.appendChild(Child);
    RootNode.appendChild(Entry);
  end;
  Doc.save(F);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Root: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Root := HdtDataRoot;
  if PluginEverRan(Root) then
  begin
    Log('The plugin has run on this PC before; plugins.xml is left as the user set it');
    Exit;
  end;
  try
    EnablePluginInHdt(Root);
    PluginEnabled := True;
    Log('First install on this PC: switched the plugin on in ' + Root + '\plugins.xml');
  except
    Log('Could not switch the plugin on in plugins.xml: ' + GetExceptionMessage);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID <> wpFinished then Exit;
  if PluginEnabled then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'The plugin is switched on in Hearthstone Deck Tracker.'
  else
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'If it does not show up in Hearthstone Deck Tracker, switch it on under Options > Tracker > Plugins.';
end;
