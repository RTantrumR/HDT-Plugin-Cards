; Tantrum's Battlegrounds Toolkit — per-user setup for the Hearthstone Deck Tracker plugin.
; Built by installer\build.ps1, which stages the Release build (the plugin DLL, the bundled
; WebP-decoder DLLs and data\) into installer\stage first and passes the version as /DAppVersion.
;
; What it does, in order: makes sure Hearthstone Deck Tracker is installed (the "Hearthstone Deck
; Tracker" page below — Setup does not go past it until HDT is found), closes HDT gracefully (a
; WM_CLOSE to its main window runs HDT's own shutdown, which writes plugins.xml — a killed HDT never
; writes it), copies the files into %APPDATA%\HearthstoneDeckTracker\Plugins\HsbgCardLookup, and on
; a PC where the plugin has never run switches it on in plugins.xml (HDT starts a newly found plugin
; OFF and never asks). Then it offers to start HDT. Upgrades install over the previous files (same
; AppId); Apps & features gets an uninstall entry. The uninstaller removes the plugin folder only —
; settings and the art cache in %APPDATA%\HearthstoneDeckTracker\HsbgCardLookup stay. HDT's
; per-version mirror under %LOCALAPPDATA% needs no cleanup: HDT's SyncPlugins deletes mirror files
; that are gone from Roaming at the next start.
;
; Finding HDT. The plugin folder is always %APPDATA%\HearthstoneDeckTracker\Plugins, for the
; installed and the portable HDT alike (HDT's PluginManager.PluginDirectory = Config.AppDataPath);
; what Setup needs HDT's program for is to know it exists, to start it at the end, and — for a
; portable HDT that keeps its config next to its exe — to find plugins.xml (Config.ConfigDir).
; Looked for, in order: the exe picked with "Locate HDT...", /HDTEXE=<path>, a running HDT (WMI
; ExecutablePath), the Squirrel launcher %LOCALAPPDATA%\HearthstoneDeckTracker\HearthstoneDeckTracker.exe,
; an Apps & features entry named "Hearthstone Deck Tracker" (its InstallLocation), and the exe the
; last run of this Setup used. A found app-<ver>\HearthstoneDeckTracker.exe is swapped for the
; Squirrel launcher next to app-<ver>, which always starts the newest version.
; When nothing is found the page says so, links hsdecktracker.net, and offers Check again and
; Locate HDT...; Next stays disabled. A silent install without HDT fails (exit code 7) instead of
; installing a plugin nothing will load.
;
; Testing: /DIR=<temp>\HearthstoneDeckTracker\Plugins\HsbgCardLookup installs into a throwaway
; profile — every data path in [Code] is derived from {app}, never from {userappdata} directly.
; /ASSUMENOHDT=1 ignores every automatic way of finding HDT, so the not-found page shows; Locate
; HDT... and /HDTEXE= still work under it. Point /HDTEXE at a copy, not your real HDT, when testing
; a portable layout: a config.xml next to that exe makes Setup write plugins.xml there.
; A /DIR test is not fully throwaway: it rewrites the per-user Apps & features entry
; (HKCU\...\Uninstall\{9B1E4C7A-5D62-4F0B-8E3C-2A7F6D9C1B45}_is1) to point at the test folder —
; put App Path, InstallLocation, DisplayIcon, (Quiet)UninstallString, DisplayVersion and
; Inno Setup CodeFile: HdtExe back after, and never run the test folder's uninstaller (it deletes
; the real entry).

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
ReadyLabel2a=Click Install to continue. Setup closes Hearthstone Deck Tracker if it is running, installs the plugin and switches it on.
FinishedLabel=[name] is installed and starts together with Hearthstone Deck Tracker.%n%nWhile HDT runs, press F3 to open the card search; the rest appears over Hearthstone in a Battlegrounds match. The first launch downloads card art in the background (about 200 MB, once).

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
; The HDT that the "Hearthstone Deck Tracker" page found (for the installed HDT, the Squirrel
; launcher, which starts the newest app-<ver>). Never next to a live instance.
Filename: "{code:HdtExePath}"; WorkingDir: "{code:HdtExeDir}"; Description: "Start Hearthstone Deck Tracker"; Flags: postinstall nowait skipifsilent; Check: HdtCanStart

[Code]
const
  WM_CLOSE = $0010;
  HdtProcessName = 'HearthstoneDeckTracker.exe';
  HdtWindowTitle = 'Hearthstone Deck Tracker';
  HdtDownloadUrl = 'https://hsdecktracker.net/';
  PluginFileName = 'Plugins/HsbgCardLookup/HsbgCardLookup.dll';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  OwnAppIdPart = '9B1E4C7A-5D62-4F0B-8E3C-2A7F6D9C1B45';

var
  PluginEnabled: Boolean;   // this run wrote the enabled entry into plugins.xml
  HdtExe: String;           // HearthstoneDeckTracker.exe that was found; '' while HDT is not found
  HdtSource: String;        // where it was found, for the page and the log
  ManualHdtExe: String;     // picked with "Locate HDT..." in this run
  FoundAtStart: Boolean;    // found before the wizard showed: the HDT page is skipped

  HdtPage: TWizardPage;
  HdtStatusText: TNewStaticText;
  HdtDetailText: TNewStaticText;
  HdtGetButton: TNewButton;
  HdtCheckButton: TNewButton;
  HdtLocateButton: TNewButton;

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

// ---------------------------------------------------------------------------------------------
// Finding Hearthstone Deck Tracker
// ---------------------------------------------------------------------------------------------

// For the installed HDT, prefer the Squirrel launcher at the root over app-<ver>\…exe: the
// launcher starts the newest version, while app-<ver> goes away at HDT's next update.
function PreferSquirrelLauncher(const Exe: String): String;
var
  Parent, Launcher: String;
begin
  Result := Exe;
  Parent := ExtractFileDir(Exe);
  if Pos('app-', Lowercase(ExtractFileName(Parent))) = 1 then
  begin
    Launcher := AddBackslash(ExtractFileDir(Parent)) + HdtProcessName;
    if FileExists(Launcher) then
      Result := Launcher;
  end;
end;

// The path when it names an existing HearthstoneDeckTracker.exe, else ''.
function ValidHdtExe(Path: String): String;
begin
  Result := '';
  Path := RemoveQuotes(Trim(Path));
  if Path = '' then Exit;
  if (CompareText(ExtractFileName(Path), HdtProcessName) = 0) and FileExists(Path) then
    Result := PreferSquirrelLauncher(Path);
end;

function RunningHdtExe: String;
var
  Locator, Wmi, Found, Proc: Variant;
  I: Integer;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Wmi.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''' + HdtProcessName + '''');
    for I := 0 to Found.Count - 1 do
    begin
      Proc := Found.ItemIndex(I);
      // ExecutablePath is Null for a process this user may not query (e.g. an elevated HDT).
      if not VarIsNull(Proc.ExecutablePath) then
      begin
        Result := ValidHdtExe(Proc.ExecutablePath);
        if Result <> '' then Exit;
      end;
    end;
  except
    Log('RunningHdtExe: WMI query failed: ' + GetExceptionMessage);
  end;
end;

// HDT's Squirrel setup registers a per-user Apps & features entry whose InstallLocation is the
// folder holding the launcher. Matched by DisplayName, so a renamed key still counts.
function HdtFromUninstallEntries(RootKey: Integer): String;
var
  Names: TArrayOfString;
  I: Integer;
  Key, Name, Location: String;
begin
  Result := '';
  if not RegGetSubkeyNames(RootKey, UninstallKey, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if Pos(OwnAppIdPart, Uppercase(Names[I])) > 0 then Continue;
    Key := UninstallKey + '\' + Names[I];
    if not RegQueryStringValue(RootKey, Key, 'DisplayName', Name) then Continue;
    if Pos('hearthstone deck tracker', Lowercase(Name)) = 0 then Continue;
    if not RegQueryStringValue(RootKey, Key, 'InstallLocation', Location) then Continue;
    Location := RemoveQuotes(Trim(Location));
    if Location = '' then Continue;
    Result := ValidHdtExe(AddBackslash(Location) + HdtProcessName);
    if Result <> '' then Exit;
  end;
end;

function AssumeNoHdt: Boolean;
begin
  Result := ExpandConstant('{param:ASSUMENOHDT|0}') = '1';
end;

// Sets HdtExe and HdtSource; HdtExe stays '' when HDT is not found.
procedure DetectHdt;
begin
  HdtSource := '';
  HdtExe := ValidHdtExe(ManualHdtExe);
  if HdtExe <> '' then begin HdtSource := 'the file you picked'; Exit; end;

  HdtExe := ValidHdtExe(ExpandConstant('{param:HDTEXE|}'));
  if HdtExe <> '' then begin HdtSource := 'the /HDTEXE parameter'; Exit; end;

  if AssumeNoHdt then
  begin
    Log('/ASSUMENOHDT=1: not looking for Hearthstone Deck Tracker by itself');
    Exit;
  end;

  HdtExe := RunningHdtExe;
  if HdtExe <> '' then begin HdtSource := 'the running Hearthstone Deck Tracker'; Exit; end;

  HdtExe := ValidHdtExe(ExpandConstant('{localappdata}\HearthstoneDeckTracker\') + HdtProcessName);
  if HdtExe <> '' then begin HdtSource := 'its usual install folder'; Exit; end;

  HdtExe := HdtFromUninstallEntries(HKCU);
  if HdtExe = '' then HdtExe := HdtFromUninstallEntries(HKLM32);
  if (HdtExe = '') and IsWin64 then HdtExe := HdtFromUninstallEntries(HKLM64);
  if HdtExe <> '' then begin HdtSource := 'Apps & features'; Exit; end;

  HdtExe := ValidHdtExe(GetPreviousData('HdtExe', ''));
  if HdtExe <> '' then begin HdtSource := 'the previous install of this toolkit'; Exit; end;
end;

procedure LogHdt;
begin
  if HdtExe <> '' then
    Log('Hearthstone Deck Tracker found via ' + HdtSource + ': ' + HdtExe)
  else
    Log('Hearthstone Deck Tracker not found');
end;

function HdtExePath(Param: String): String;
begin
  Result := HdtExe;
end;

function HdtExeDir(Param: String): String;
begin
  Result := ExtractFileDir(HdtExe);
end;

// Where HDT reads plugins.xml (its Config.ConfigDir): %APPDATA%\HearthstoneDeckTracker, except for
// a portable HDT that keeps config.xml next to its exe. The installed (Squirrel) HDT always uses
// %APPDATA%, and its launcher folder never holds a config.xml.
function HdtConfigDir: String;
var
  ExeDir: String;
begin
  Result := HdtDataRoot;
  if HdtExe = '' then Exit;
  ExeDir := ExtractFileDir(HdtExe);
  if FileExists(AddBackslash(ExeDir) + 'config.xml') then
    Result := ExeDir;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'HdtExe', HdtExe);
end;

// ---------------------------------------------------------------------------------------------
// The "Hearthstone Deck Tracker" page
// ---------------------------------------------------------------------------------------------

procedure UpdateHdtPage;
begin
  if HdtExe <> '' then
  begin
    HdtStatusText.Caption := 'Hearthstone Deck Tracker found';
    HdtDetailText.Caption :=
      HdtExe + #13#10 + '(found via ' + HdtSource + ')' + #13#10#13#10 +
      'Click Next to continue. Setup closes Hearthstone Deck Tracker while it copies the files ' +
      'and can start it again at the end.';
    HdtGetButton.Visible := False;
    HdtLocateButton.Caption := '&Use another HDT...';
  end
  else
  begin
    HdtStatusText.Caption := 'Hearthstone Deck Tracker was not found on this PC';
    HdtDetailText.Caption :=
      '1. Click Download HDT and install Hearthstone Deck Tracker from its website. ' +
      'Its installer starts HDT when it is done; leave it running.' + #13#10 +
      '2. Come back to this window and click Check again.' + #13#10#13#10 +
      'Already have HDT and Setup still cannot find it? Click Locate HDT and pick ' +
      'HearthstoneDeckTracker.exe in the folder where HDT is.';
    HdtGetButton.Visible := True;
    HdtLocateButton.Caption := '&Locate HDT...';
  end;
  if (WizardForm <> nil) and (WizardForm.CurPageID = HdtPage.ID) then
    WizardForm.NextButton.Enabled := HdtExe <> '';
end;

procedure HdtGetButtonClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  if not ShellExec('open', HdtDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('Could not open the browser. Go to ' + HdtDownloadUrl + ' to download Hearthstone Deck Tracker.',
      mbError, MB_OK);
end;

procedure HdtCheckButtonClick(Sender: TObject);
begin
  DetectHdt;
  LogHdt;
  UpdateHdtPage;
  if HdtExe = '' then
    MsgBox('Hearthstone Deck Tracker is still not found.' + #13#10#13#10 +
      'If you have just installed it, start it once and click Check again. ' +
      'If it is installed in a folder of your own, click Locate HDT and pick HearthstoneDeckTracker.exe.',
      mbInformation, MB_OK)
  else
    WizardForm.NextButton.SetFocus;
end;

procedure HdtLocateButtonClick(Sender: TObject);
var
  FileName, InitialDir: String;
begin
  FileName := '';
  if HdtExe <> '' then
    InitialDir := ExtractFileDir(HdtExe)
  else
    InitialDir := ExpandConstant('{localappdata}');
  if not GetOpenFileName('Pick HearthstoneDeckTracker.exe', FileName, InitialDir,
       'Hearthstone Deck Tracker (HearthstoneDeckTracker.exe)|HearthstoneDeckTracker.exe|Programs (*.exe)|*.exe',
       'exe') then
    Exit;
  if ValidHdtExe(FileName) = '' then
  begin
    MsgBox('That is not Hearthstone Deck Tracker.' + #13#10#13#10 +
      'Pick the file named HearthstoneDeckTracker.exe in the folder where HDT is installed.',
      mbError, MB_OK);
    Exit;
  end;
  ManualHdtExe := FileName;
  DetectHdt;
  LogHdt;
  UpdateHdtPage;
  WizardForm.NextButton.SetFocus;
end;

procedure CreateHdtPage;
var
  Intro: TNewStaticText;
  Gap, ButtonWidth, ButtonTop: Integer;
begin
  HdtPage := CreateCustomPage(wpWelcome, 'Hearthstone Deck Tracker',
    'The toolkit is a plugin: it runs inside Hearthstone Deck Tracker (HDT).');

  Intro := TNewStaticText.Create(HdtPage);
  Intro.Parent := HdtPage.Surface;
  Intro.AutoSize := False;
  Intro.WordWrap := True;
  Intro.Left := 0;
  Intro.Top := 0;
  Intro.Width := HdtPage.SurfaceWidth;
  Intro.Height := ScaleY(42);
  Intro.Caption := 'It has no window or desktop icon of its own and starts together with HDT, ' +
    'the free deck tracker. Setup installs it only once HDT is on this PC.';

  HdtStatusText := TNewStaticText.Create(HdtPage);
  HdtStatusText.Parent := HdtPage.Surface;
  HdtStatusText.Left := 0;
  HdtStatusText.Top := Intro.Top + Intro.Height + ScaleY(6);
  HdtStatusText.Font.Style := [fsBold];

  Gap := ScaleX(8);
  ButtonWidth := (HdtPage.SurfaceWidth - 2 * Gap) div 3;
  ButtonTop := HdtPage.SurfaceHeight - WizardForm.NextButton.Height;

  HdtDetailText := TNewStaticText.Create(HdtPage);
  HdtDetailText.Parent := HdtPage.Surface;
  HdtDetailText.AutoSize := False;
  HdtDetailText.WordWrap := True;
  HdtDetailText.Left := 0;
  HdtDetailText.Top := HdtStatusText.Top + ScaleY(22);
  HdtDetailText.Width := HdtPage.SurfaceWidth;
  HdtDetailText.Height := ButtonTop - HdtDetailText.Top - ScaleY(8);

  HdtGetButton := TNewButton.Create(HdtPage);
  HdtGetButton.Parent := HdtPage.Surface;
  HdtGetButton.Left := 0;
  HdtGetButton.Top := ButtonTop;
  HdtGetButton.Width := ButtonWidth;
  HdtGetButton.Height := WizardForm.NextButton.Height;
  HdtGetButton.Caption := '&Download HDT';
  HdtGetButton.OnClick := @HdtGetButtonClick;

  HdtCheckButton := TNewButton.Create(HdtPage);
  HdtCheckButton.Parent := HdtPage.Surface;
  HdtCheckButton.Left := ButtonWidth + Gap;
  HdtCheckButton.Top := ButtonTop;
  HdtCheckButton.Width := ButtonWidth;
  HdtCheckButton.Height := WizardForm.NextButton.Height;
  HdtCheckButton.Caption := 'Check &again';
  HdtCheckButton.OnClick := @HdtCheckButtonClick;

  HdtLocateButton := TNewButton.Create(HdtPage);
  HdtLocateButton.Parent := HdtPage.Surface;
  HdtLocateButton.Left := 2 * (ButtonWidth + Gap);
  HdtLocateButton.Top := ButtonTop;
  HdtLocateButton.Width := HdtPage.SurfaceWidth - HdtLocateButton.Left;
  HdtLocateButton.Height := WizardForm.NextButton.Height;
  HdtLocateButton.OnClick := @HdtLocateButtonClick;

  UpdateHdtPage;
end;

procedure InitializeWizard;
begin
  DetectHdt;
  LogHdt;
  FoundAtStart := HdtExe <> '';
  CreateHdtPage;
end;

// HDT found right away: nothing to do on that page; the Ready page still names the HDT used.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = HdtPage.ID) and FoundAtStart;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = HdtPage.ID) and (HdtExe = '') then
  begin
    MsgBox('Install Hearthstone Deck Tracker first, then click Check again.', mbInformation, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Hearthstone Deck Tracker:' + NewLine + Space + HdtExe + NewLine + NewLine +
    'Plugin folder:' + NewLine + Space + ExpandConstant('{app}');
end;

// ---------------------------------------------------------------------------------------------
// Closing HDT, installing, switching the plugin on
// ---------------------------------------------------------------------------------------------

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

function HdtCanStart: Boolean;
begin
  Result := (HdtExe <> '') and FileExists(HdtExe) and not HdtRunning;
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

// The HDT page keeps an interactive install from getting here without HDT; a silent install has
// no pages, so this is where it stops (exit code 7) instead of installing a plugin nothing loads.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if HdtExe = '' then
  begin
    DetectHdt;
    LogHdt;
  end;
  if HdtExe = '' then
  begin
    Result := 'Hearthstone Deck Tracker is not installed on this PC. The toolkit is a plugin that ' +
      'runs inside it: install HDT from ' + HdtDownloadUrl + ', start it once, then run Setup again.';
    Exit;
  end;
  if not EnsureHdtClosed then
    Result := 'Hearthstone Deck Tracker is still running. Close it and run Setup again.';
end;

function InitializeUninstall: Boolean;
begin
  Result := EnsureHdtClosed;
end;

// install.bat's rule: the plugin has run on this PC when its settings file exists or its log holds
// an OnLoad line. HDT calls OnUnload on every discovered plugin at shutdown, loaded or not, so
// OnUnload-only lines mean "never enabled". The plugin's own data is always under %APPDATA%.
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
procedure EnablePluginInHdt(const ConfigDir: String);
var
  Doc, RootNode, Entry, Child, Found: Variant;
  F: String;
begin
  F := ConfigDir + '\plugins.xml';
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
  ConfigDir: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if PluginEverRan(HdtDataRoot) then
  begin
    Log('The plugin has run on this PC before; plugins.xml is left as the user set it');
    Exit;
  end;
  ConfigDir := HdtConfigDir;
  try
    EnablePluginInHdt(ConfigDir);
    PluginEnabled := True;
    Log('First install on this PC: switched the plugin on in ' + ConfigDir + '\plugins.xml');
  except
    Log('Could not switch the plugin on in plugins.xml: ' + GetExceptionMessage);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = HdtPage.ID then
  begin
    WizardForm.NextButton.Enabled := HdtExe <> '';
    Exit;
  end;
  if CurPageID <> wpFinished then Exit;
  if PluginEnabled then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'The plugin is switched on in Hearthstone Deck Tracker.'
  else
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'If it does not show up in Hearthstone Deck Tracker, switch it on under Options > Tracker > Plugins.';
end;
