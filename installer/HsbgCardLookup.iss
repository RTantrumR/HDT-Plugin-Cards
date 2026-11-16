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
; Looked for, in order: the exe picked with "Locate HDT...", /HDTEXE=<path>, a running HDT (its
; process image path), the Squirrel launcher %LOCALAPPDATA%\HearthstoneDeckTracker\HearthstoneDeckTracker.exe,
; an Apps & features entry named "Hearthstone Deck Tracker" (its InstallLocation), and the exe the
; last run of this Setup used. A found app-<ver>\HearthstoneDeckTracker.exe is swapped for the
; Squirrel launcher next to app-<ver>, which always starts the newest version.
; When nothing is found the page says so, links hsdecktracker.net, and offers Check again and
; Locate HDT...; Next stays disabled. A silent install without HDT fails (exit code 7) instead of
; installing a plugin nothing will load.
;
; Languages. The wizard runs in the language Windows is set to when it has that one (English
; otherwise — Russian and Ukrainian systems included), and asks at the start. Its own lines live in
; installer\i18n\<lang>.json; build.ps1 turns them into out\messages.iss, #included below. A page
; then asks which language the TOOLKIT uses — by default the wizard's language (es-MX and the other
; Latin American Spanish systems get es-mx, Taiwan/Hong Kong/Macau Chinese get zh-tw), on an
; upgrade the language already set. The choice reaches the plugin as setup-language.txt in its data
; folder, which the plugin applies once and deletes; the wizard never writes config.xml, because
; the plugin's first-run defaults and PluginEverRan below both depend on it not existing.
; /TOOLKITLANG=<code> preselects (silent installs too); "follow" = same as HDT.
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
; The language page holds an intro, a row and two columns of seven: a tenth more room than the default.
WizardSizePercent=110
CloseApplications=no

[Languages]
; English first: it is what a Windows language without its own entry gets.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"
; Not shipped with Inno Setup 6.7: the official translations from jrsoftware/issrc (Files\Languages).
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "chinesetraditional"; MessagesFile: "languages\ChineseTraditional.isl"

; Our own wizard texts in every language above (Setup creates no shortcuts, so Inno shows
; FinishedLabelNoIcons; FinishedLabel is kept in step there).
#include "out\messages.iss"

[Files]
; HDT 1.57+ runs plugins from a per-version mirror that it refreshes from this folder at startup,
; but only files with a NEWER timestamp — "touch" stamps every installed file with the install time,
; so the mirror always picks the new files up (install.bat did the same).
Source: "stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs touch

; The language page's flags (rendered from the plugin's own Ui\Flags.cs by build.ps1), unpacked
; to {tmp} for the page only.
Source: "out\flags\*.bmp"; Flags: dontcopy

[InstallDelete]
; The installer replaces the whole set: a DLL or data file that a newer version no longer ships
; must not linger from an older one.
Type: files; Name: "{app}\*.dll"
Type: filesandordirs; Name: "{app}\data"

[Run]
; The HDT that the "Hearthstone Deck Tracker" page found (for the installed HDT, the Squirrel
; launcher, which starts the newest app-<ver>). Never next to a live instance.
Filename: "{code:HdtExePath}"; WorkingDir: "{code:HdtExeDir}"; Description: "{cm:RunHdt}"; Flags: postinstall nowait skipifsilent; Check: HdtCanStart

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
  LangPage: TWizardPage;
  LangRadios: array of TNewRadioButton;
  LangCodes: TArrayOfString;   // LangRadios[i] -> the plugin's language code ('' = same as HDT)
  LangNames: TArrayOfString;   // LangRadios[i] -> what the row says, for the Ready page
  CurrentLang: String;         // the plugin's Language setting before this install; '?' = no config yet
  LangPreselected: Boolean;    // PreselectLanguage has run (it needs the final {app})
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

function OpenProcess(DesiredAccess: DWORD; InheritHandle: BOOL; ProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: DWORD; ExeName: String; var Size: DWORD): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

// The program file of a process. PROCESS_QUERY_LIMITED_INFORMATION is granted even for a process
// running elevated, where WMI's ExecutablePath comes back Null.
function ProcessImagePath(Pid: DWORD): String;
var
  H: THandle;
  Size: DWORD;
begin
  Result := '';
  H := OpenProcess($1000 {PROCESS_QUERY_LIMITED_INFORMATION}, False, Pid);
  if H = 0 then Exit;
  try
    Size := 1024;
    SetLength(Result, Size);
    if QueryFullProcessImageName(H, 0, Result, Size) then
      SetLength(Result, Size)
    else
      Result := '';
  finally
    CloseHandle(H);
  end;
end;

function RunningHdtExe: String;
var
  Locator, Wmi, Found: Variant;
  I: Integer;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Wmi.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + HdtProcessName + '''');
    for I := 0 to Found.Count - 1 do
    begin
      Result := ValidHdtExe(ProcessImagePath(Found.ItemIndex(I).ProcessId));
      if Result <> '' then Exit;
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
  if HdtExe <> '' then begin HdtSource := CustomMessage('SrcPicked'); Exit; end;

  HdtExe := ValidHdtExe(ExpandConstant('{param:HDTEXE|}'));
  if HdtExe <> '' then begin HdtSource := CustomMessage('SrcParam'); Exit; end;

  if AssumeNoHdt then
  begin
    Log('/ASSUMENOHDT=1: not looking for Hearthstone Deck Tracker by itself');
    Exit;
  end;

  HdtExe := RunningHdtExe;
  if HdtExe <> '' then begin HdtSource := CustomMessage('SrcRunning'); Exit; end;

  HdtExe := ValidHdtExe(ExpandConstant('{localappdata}\HearthstoneDeckTracker\') + HdtProcessName);
  if HdtExe <> '' then begin HdtSource := CustomMessage('SrcUsual'); Exit; end;

  HdtExe := HdtFromUninstallEntries(HKCU);
  if HdtExe = '' then HdtExe := HdtFromUninstallEntries(HKLM32);
  if (HdtExe = '') and IsWin64 then HdtExe := HdtFromUninstallEntries(HKLM64);
  if HdtExe <> '' then begin HdtSource := CustomMessage('SrcApps'); Exit; end;

  HdtExe := ValidHdtExe(GetPreviousData('HdtExe', ''));
  if HdtExe <> '' then begin HdtSource := CustomMessage('SrcPrevious'); Exit; end;
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
var
  ButtonTop: Integer;
begin
  if HdtExe <> '' then
  begin
    HdtStatusText.Caption := CustomMessage('HdtFound');
    HdtDetailText.Caption :=
      HdtExe + #13#10 + FmtMessage(CustomMessage('HdtFoundVia'), [HdtSource]) + #13#10#13#10 +
      CustomMessage('HdtFoundNext');
    HdtGetButton.Visible := False;
    HdtCheckButton.Left := 0;
    HdtLocateButton.Left := HdtGetButton.Width + ScaleX(8);
    HdtLocateButton.Caption := CustomMessage('HdtUseAnother');
  end
  else
  begin
    HdtStatusText.Caption := CustomMessage('HdtNotFound');
    HdtDetailText.Caption := CustomMessage('HdtNotFoundSteps');
    HdtGetButton.Visible := True;
    HdtCheckButton.Left := HdtGetButton.Width + ScaleX(8);
    HdtLocateButton.Left := 2 * (HdtGetButton.Width + ScaleX(8));
    HdtLocateButton.Caption := CustomMessage('HdtLocate');
  end;
  // Fit the label to its text (on Windows the not-found text wraps to six lines; a fixed height
  // cut the last one off behind the buttons) and keep the buttons right under it.
  WizardForm.AdjustLabelHeight(HdtDetailText);
  ButtonTop := HdtDetailText.Top + HdtDetailText.Height + ScaleY(8);
  HdtGetButton.Top := ButtonTop;
  HdtCheckButton.Top := ButtonTop;
  HdtLocateButton.Top := ButtonTop;
  if (WizardForm <> nil) and (WizardForm.CurPageID = HdtPage.ID) then
    WizardForm.NextButton.Enabled := (HdtExe <> '') or WizardSilent;
end;

procedure HdtGetButtonClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  if not ShellExec('open', HdtDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox(FmtMessage(CustomMessage('HdtBrowserFailed'), [HdtDownloadUrl]), mbError, MB_OK);
end;

procedure HdtCheckButtonClick(Sender: TObject);
begin
  DetectHdt;
  LogHdt;
  UpdateHdtPage;
  if HdtExe = '' then
    MsgBox(CustomMessage('HdtStillNotFound'), mbInformation, MB_OK)
  else
    WizardForm.ActiveControl := WizardForm.NextButton;
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
  if not GetOpenFileName(CustomMessage('HdtPickTitle'), FileName, InitialDir,
       'Hearthstone Deck Tracker (HearthstoneDeckTracker.exe)|HearthstoneDeckTracker.exe|' +
       CustomMessage('HdtPickPrograms') + ' (*.exe)|*.exe',
       'exe') then
    Exit;
  if ValidHdtExe(FileName) = '' then
  begin
    MsgBox(CustomMessage('HdtNotHdt'), mbError, MB_OK);
    Exit;
  end;
  ManualHdtExe := FileName;
  DetectHdt;
  LogHdt;
  UpdateHdtPage;
  WizardForm.ActiveControl := WizardForm.NextButton;
end;

procedure CreateHdtPage;
var
  Intro: TNewStaticText;
  Gap, ButtonWidth: Integer;
begin
  HdtPage := CreateCustomPage(wpWelcome, 'Hearthstone Deck Tracker', CustomMessage('HdtPageDescription'));

  Intro := TNewStaticText.Create(HdtPage);
  Intro.Parent := HdtPage.Surface;
  Intro.AutoSize := False;
  Intro.WordWrap := True;
  Intro.Left := 0;
  Intro.Top := 0;
  Intro.Width := HdtPage.SurfaceWidth;
  Intro.Height := ScaleY(42);
  Intro.Caption := CustomMessage('HdtIntro');

  HdtStatusText := TNewStaticText.Create(HdtPage);
  HdtStatusText.Parent := HdtPage.Surface;
  HdtStatusText.Left := 0;
  HdtStatusText.Top := Intro.Top + Intro.Height + ScaleY(6);
  HdtStatusText.Font.Style := [fsBold];

  // The buttons sit right under the text (UpdateHdtPage sizes the text and places them, and moves
  // them left when Download is hidden): the surface grows after InitializeWizard, so a position
  // taken from SurfaceHeight here would leave them floating mid-page.
  HdtDetailText := TNewStaticText.Create(HdtPage);
  HdtDetailText.Parent := HdtPage.Surface;
  HdtDetailText.AutoSize := False;
  HdtDetailText.WordWrap := True;
  HdtDetailText.Left := 0;
  HdtDetailText.Top := HdtStatusText.Top + ScaleY(22);
  HdtDetailText.Width := HdtPage.SurfaceWidth;
  HdtDetailText.Height := ScaleY(84);

  Gap := ScaleX(8);
  ButtonWidth := (HdtPage.SurfaceWidth - 2 * Gap) div 3;

  HdtGetButton := TNewButton.Create(HdtPage);
  HdtGetButton.Parent := HdtPage.Surface;
  HdtGetButton.Left := 0;
  HdtGetButton.Width := ButtonWidth;
  HdtGetButton.Height := WizardForm.NextButton.Height;
  HdtGetButton.Caption := CustomMessage('HdtDownload');
  HdtGetButton.OnClick := @HdtGetButtonClick;

  HdtCheckButton := TNewButton.Create(HdtPage);
  HdtCheckButton.Parent := HdtPage.Surface;
  HdtCheckButton.Left := ButtonWidth + Gap;
  HdtCheckButton.Width := ButtonWidth;
  HdtCheckButton.Height := WizardForm.NextButton.Height;
  HdtCheckButton.Caption := CustomMessage('HdtCheckAgain');
  HdtCheckButton.OnClick := @HdtCheckButtonClick;

  HdtLocateButton := TNewButton.Create(HdtPage);
  HdtLocateButton.Parent := HdtPage.Surface;
  HdtLocateButton.Left := 2 * (ButtonWidth + Gap);
  HdtLocateButton.Width := ButtonWidth;
  HdtLocateButton.Height := WizardForm.NextButton.Height;
  HdtLocateButton.OnClick := @HdtLocateButtonClick;

  UpdateHdtPage;
end;

// ---------------------------------------------------------------------------------------------
// The "Toolkit language" page
// ---------------------------------------------------------------------------------------------

// The plugin's Language setting as config.xml holds it ('' = same as HDT), or '?' when the plugin
// has no settings file yet (a fresh install). A plain text search: the element is a single line
// written by .NET's XmlSerializer, <Language>de</Language> or <Language /> when empty.
function ReadCurrentLanguage: String;
var
  Xml: AnsiString;
  Text: String;
  I, J: Integer;
begin
  Result := '?';
  if not LoadStringFromFile(DataDir('') + '\config.xml', Xml) then Exit;
  Text := String(Xml);
  Result := '';
  I := Pos('<Language>', Text);
  if I = 0 then Exit;
  I := I + Length('<Language>');
  J := Pos('</Language>', Copy(Text, I, 64));
  if J > 0 then
    Result := Trim(Copy(Text, I, J - 1));
end;

// Windows' UI language split into its primary language and sublanguage (LANGID bits).
function UiPrimary: Integer;
begin
  Result := GetUILanguage and $3FF;
end;

function UiSub: Integer;
begin
  Result := GetUILanguage shr 10;
end;

// The toolkit language that matches the wizard's language. The wizard has one Spanish and picks
// the Chinese entry by exact LANGID only, so the variant comes from Windows itself when Windows is
// set to that language: Spanish other than Spain's -> es-mx; Chinese of Taiwan (1), Hong Kong (3)
// or Macau (5) -> zh-tw, of the PRC (2) or Singapore (4) -> zh-cn.
function DefaultToolkitLanguage: String;
var
  L: String;
begin
  L := ActiveLanguage;
  if L = 'german' then Result := 'de'
  else if L = 'spanish' then
  begin
    Result := 'es';
    if (UiPrimary = $0A) and (UiSub <> 1) and (UiSub <> 3) then Result := 'es-mx';
  end
  else if L = 'french' then Result := 'fr'
  else if L = 'italian' then Result := 'it'
  else if L = 'japanese' then Result := 'ja'
  else if L = 'korean' then Result := 'ko'
  else if L = 'polish' then Result := 'pl'
  else if L = 'brazilianportuguese' then Result := 'pt'
  else if L = 'thai' then Result := 'th'
  else if (L = 'chinesesimplified') or (L = 'chinesetraditional') then
  begin
    if L = 'chinesetraditional' then Result := 'zh-tw' else Result := 'zh-cn';
    if UiPrimary = $04 then
      if (UiSub = 1) or (UiSub = 3) or (UiSub = 5) then Result := 'zh-tw' else Result := 'zh-cn';
  end
  else Result := 'en';
end;

// The flag size nearest this DPI, drawn by build.ps1 at 22/28/33/44 px wide (100-200 %).
function FlagWidth: Integer;
begin
  Result := ScaleX(22);
  if Result <= 24 then Result := 22
  else if Result <= 30 then Result := 28
  else if Result <= 38 then Result := 33
  else Result := 44;
end;

function LoadMark(const FileName: String; Parent: TWinControl; Left, Top: Integer): TBitmapImage;
begin
  ExtractTemporaryFile(FileName);
  Result := TBitmapImage.Create(LangPage);
  Result.Parent := Parent;
  Result.Bitmap.LoadFromFile(ExpandConstant('{tmp}\' + FileName));
  Result.AutoSize := True;
  Result.Left := Left;
  Result.Top := Top;
end;

// A click on a row's flag or words picks that row, as on the radio itself.
procedure LangRowClick(Sender: TObject);
begin
  LangRadios[TComponent(Sender).Tag].Checked := True;
end;

// One row: radio, mark, name in its own words, and the locale tag in grey — the plugin's own
// language page in miniature (Settings > Language).
procedure AddLang(const Code, Name, LocaleTag, MarkFile: String; Left, Top, RowH: Integer);
var
  N: Integer;
  Radio: TNewRadioButton;
  Mark: TBitmapImage;
  NameText, TagText: TNewStaticText;
begin
  N := GetArrayLength(LangCodes);
  SetArrayLength(LangCodes, N + 1);
  SetArrayLength(LangNames, N + 1);
  SetArrayLength(LangRadios, N + 1);
  LangCodes[N] := Code;
  if LocaleTag <> '' then LangNames[N] := Name + ' (' + LocaleTag + ')' else LangNames[N] := Name;

  Radio := TNewRadioButton.Create(LangPage);
  Radio.Parent := LangPage.Surface;
  Radio.Left := Left;
  Radio.Width := ScaleX(17);
  Radio.Height := RowH;
  Radio.Top := Top;
  LangRadios[N] := Radio;

  Mark := LoadMark(MarkFile, LangPage.Surface, Left + ScaleX(19), Top);
  Mark.Top := Top + (RowH - Mark.Height) div 2;
  Mark.Tag := N;
  Mark.OnClick := @LangRowClick;

  NameText := TNewStaticText.Create(LangPage);
  NameText.Parent := LangPage.Surface;
  NameText.Caption := Name;
  NameText.Left := Mark.Left + Mark.Width + ScaleX(6);
  NameText.Top := Top + (RowH - NameText.Height) div 2;
  NameText.Tag := N;
  NameText.OnClick := @LangRowClick;

  if LocaleTag <> '' then
  begin
    TagText := TNewStaticText.Create(LangPage);
    TagText.Parent := LangPage.Surface;
    TagText.Caption := LocaleTag;
    TagText.Font.Color := clGrayText;
    TagText.Left := NameText.Left + NameText.Width + ScaleX(6);
    TagText.Top := NameText.Top;
    TagText.Tag := N;
    TagText.OnClick := @LangRowClick;
  end;
end;

function ChosenIndex: Integer;
var
  I: Integer;
begin
  Result := -1;
  for I := 0 to GetArrayLength(LangRadios) - 1 do
    if LangRadios[I].Checked then Result := I;
end;

// The code the page (or /TOOLKITLANG) settles on.
function ChosenLanguage: String;
var
  I: Integer;
begin
  Result := '';
  I := ChosenIndex;
  if I >= 0 then Result := LangCodes[I];
end;

function ChosenLanguageName: String;
var
  I: Integer;
begin
  Result := '';
  I := ChosenIndex;
  if I >= 0 then Result := LangNames[I];
end;

procedure SelectLanguage(const Code: String);
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(LangCodes) - 1 do
    if CompareText(LangCodes[I], Code) = 0 then
    begin
      LangRadios[I].Checked := True;
      Exit;
    end;
  // A code this list doesn't know (a language added later, a typo in config.xml) -> English.
  SelectLanguage('en');
end;

procedure CreateLanguagePage;
var
  Intro: TNewStaticText;
  W, RowH, Top, ColW: Integer;
  F: String;
begin
  // After the folder page (hidden, DisableDirPage): {app} — and with it where config.xml is — is
  // only final from there on, /DIR included. The preselection waits for it (PreselectLanguage).
  LangPage := CreateCustomPage(wpSelectDir, CustomMessage('LangPageCaption'), CustomMessage('LangPageDescription'));

  Intro := TNewStaticText.Create(LangPage);
  Intro.Parent := LangPage.Surface;
  Intro.AutoSize := False;
  Intro.WordWrap := True;
  Intro.Left := 0;
  Intro.Top := 0;
  Intro.Width := LangPage.SurfaceWidth;
  Intro.Caption := CustomMessage('LangIntro');
  WizardForm.AdjustLabelHeight(Intro);

  W := FlagWidth;
  F := '_' + IntToStr(W) + '.bmp';
  RowH := ScaleY(23);
  ColW := LangPage.SurfaceWidth div 2;
  Top := Intro.Top + Intro.Height + ScaleY(8);

  // "Same as HDT" across the top, then the languages in two columns, each in its own words with
  // the flag and tag the plugin and the site show.
  AddLang('', CustomMessage('LangSameAsHdt'), '', 'globe' + F, 0, Top, RowH);
  Top := Top + RowH + ScaleY(4);
  AddLang('en', 'English', 'en-US', 'flag_en' + F, 0, Top + 0 * RowH, RowH);
  AddLang('de', 'Deutsch', 'de-DE', 'flag_de' + F, 0, Top + 1 * RowH, RowH);
  AddLang('es', 'Español (España)', 'es-ES', 'flag_es' + F, 0, Top + 2 * RowH, RowH);
  AddLang('es-mx', 'Español (Latinoamérica)', 'es-MX', 'flag_es-mx' + F, 0, Top + 3 * RowH, RowH);
  AddLang('fr', 'Français', 'fr-FR', 'flag_fr' + F, 0, Top + 4 * RowH, RowH);
  AddLang('it', 'Italiano', 'it-IT', 'flag_it' + F, 0, Top + 5 * RowH, RowH);
  AddLang('ja', '日本語', 'ja-JP', 'flag_ja' + F, 0, Top + 6 * RowH, RowH);
  AddLang('ko', '한국어', 'ko-KR', 'flag_ko' + F, ColW, Top + 0 * RowH, RowH);
  AddLang('pl', 'Polski', 'pl-PL', 'flag_pl' + F, ColW, Top + 1 * RowH, RowH);
  AddLang('pt', 'Português (Brasil)', 'pt-BR', 'flag_pt' + F, ColW, Top + 2 * RowH, RowH);
  AddLang('th', 'ไทย', 'th-TH', 'flag_th' + F, ColW, Top + 3 * RowH, RowH);
  AddLang('zh-cn', '简体中文', 'zh-CN', 'flag_zh-cn' + F, ColW, Top + 4 * RowH, RowH);
  AddLang('zh-tw', '繁體中文', 'zh-TW', 'flag_zh-tw' + F, ColW, Top + 5 * RowH, RowH);
end;

// Preselect, once {app} is final: /TOOLKITLANG, else the setting already there, else the wizard's
// language. Runs when the page first shows, and before the Ready page and the install for a silent
// run, which shows no pages.
procedure PreselectLanguage;
var
  Wanted: String;
begin
  if LangPreselected then Exit;
  LangPreselected := True;
  CurrentLang := ReadCurrentLanguage;
  Wanted := ExpandConstant('{param:TOOLKITLANG|}');
  if CompareText(Wanted, 'follow') = 0 then
    SelectLanguage('')
  else if Wanted <> '' then
    SelectLanguage(Wanted)
  else if CurrentLang <> '?' then
    SelectLanguage(CurrentLang)
  else
    SelectLanguage(DefaultToolkitLanguage);
  Log('Toolkit language: current=' + CurrentLang + ' default=' + DefaultToolkitLanguage +
    ' preselected=' + ChosenLanguage + ' (wizard: ' + ActiveLanguage + ', UI LANGID $' + Format('%.4x', [GetUILanguage]) + ')');
end;

procedure InitializeWizard;
begin
  DetectHdt;
  LogHdt;
  FoundAtStart := HdtExe <> '';
  CreateHdtPage;
  CreateLanguagePage;
end;

// HDT found right away: nothing to do on that page; the Ready page still names the HDT used.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = HdtPage.ID) and FoundAtStart;
end;

// A silent install also "clicks" Next on every page; it goes on to PrepareToInstall, which logs
// why it stops and exits with code 7, instead of a message box nobody sees.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = HdtPage.ID) and (HdtExe = '') and not WizardSilent then
  begin
    MsgBox(CustomMessage('HdtInstallFirst'), mbInformation, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  PreselectLanguage;
  Result := CustomMessage('ReadyHdt') + NewLine + Space + HdtExe + NewLine + NewLine +
    CustomMessage('ReadyFolder') + NewLine + Space + ExpandConstant('{app}') + NewLine + NewLine +
    CustomMessage('ReadyLanguage') + NewLine + Space + ChosenLanguageName;
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
    if SuppressibleMsgBox(CustomMessage('HdtStillRunning'), mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
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
    Result := FmtMessage(CustomMessage('HdtMissingSilent'), [HdtDownloadUrl]);
    Exit;
  end;
  if not EnsureHdtClosed then
    Result := CustomMessage('HdtRunningAbort');
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

// The plugin applies setup-language.txt once at its next start and deletes it. Written only when
// the choice differs from what config.xml already holds (always on a fresh install), so an upgrade
// that keeps the setting leaves nothing behind.
procedure HandOverLanguage;
var
  Code: String;
begin
  PreselectLanguage;
  Code := ChosenLanguage;
  if (CurrentLang <> '?') and (CompareText(Code, CurrentLang) = 0) then
  begin
    Log('Toolkit language unchanged (' + Code + ')');
    Exit;
  end;
  ForceDirectories(DataDir(''));
  if SaveStringToFile(DataDir('') + '\setup-language.txt', Code, False) then
    Log('Toolkit language handed to the plugin: "' + Code + '"')
  else
    Log('Could not write setup-language.txt');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDir: String;
begin
  if CurStep <> ssPostInstall then Exit;
  HandOverLanguage;
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
var
  Note, Caption, ClickFinish: String;
  Delta: Integer;
begin
  if CurPageID = HdtPage.ID then
  begin
    WizardForm.NextButton.Enabled := (HdtExe <> '') or WizardSilent;
    Exit;
  end;
  if CurPageID = LangPage.ID then
  begin
    PreselectLanguage;
    Exit;
  end;
  if CurPageID <> wpFinished then Exit;
  if PluginEnabled then
    Note := CustomMessage('PluginSwitchedOn')
  else
    Note := CustomMessage('PluginSwitchOnHint');
  // Inno has already laid the page out for its own text (ending in "Click Finish to exit Setup.")
  // when this runs: put the note before that line, then grow the label and move the
  // "Start Hearthstone Deck Tracker" box down by as much, or the note is cut off behind it.
  Caption := WizardForm.FinishedLabel.Caption;
  ClickFinish := SetupMessage(msgClickFinish);
  if Pos(ClickFinish, Caption) > 0 then
    StringChangeEx(Caption, ClickFinish, Note + #13#10#13#10 + ClickFinish, True)
  else
    Caption := Caption + #13#10#13#10 + Note;
  WizardForm.FinishedLabel.Caption := Caption;
  Delta := WizardForm.AdjustLabelHeight(WizardForm.FinishedLabel);
  WizardForm.RunList.Top := WizardForm.RunList.Top + Delta;
end;
