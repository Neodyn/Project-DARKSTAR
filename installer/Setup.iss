; D.A.R.K.S.T.A.R. installer (Inno Setup 6.x)
;
; WHAT THIS DOES:
;   - Installs the bot service + the config GUI into one folder
;   - Installs every runtime dependency the bot and GUI need on a bare Windows server:
;       * .NET 8 Desktop Runtime  (covers both the bot's plain .NET 8 runtime need AND the
;         GUI's WPF/Blazor Hybrid need - the Desktop Runtime is a superset that includes the
;         base .NET runtime, so one installer covers both components)
;       * Visual C++ Redistributable x64 (needed by Vosk's native libvosk.dll - a native DLL
;         dependency that framework-dependent, and even self-contained, .NET publishing does
;         NOT bring along automatically, since it isn't a .NET assembly)
;       * WebView2 Runtime (needed by the GUI to render its Blazor UI)
;     Every check is skip-if-already-installed, so re-running this installer on a machine that
;     already has some/all of these is a normal no-op for that step.
;   - Bundles the Vosk model and writes its correct, already-installed path straight into a
;     fresh config.json (this is exactly the manual step you'd otherwise have to do by hand)
;   - Optionally registers the bot as a Windows Service
;   - Creates a Start Menu shortcut for the config GUI
;
; BEFORE COMPILING THIS SCRIPT:
;   1. Publish both projects framework-dependent (NOT self-contained - the dependency installers
;      above now cover the runtime, so the published output stays small and this installer
;      handles what's actually needed on the target machine):
;        dotnet publish Darkstar.csproj                   -c Release -r win-x64 --self-contained false -o installer\publish\bot
;        dotnet publish Darkstar.Gui\Darkstar.Gui.csproj   -c Release -r win-x64 --self-contained false -o installer\publish\gui
;   2. Download a Vosk model (e.g. vosk-model-small-en-us-0.15) and unpack it into:
;        installer\vosk-model\   (so that installer\vosk-model\am\final.mdl etc. exist directly under it)
;   3. Download the three dependency installers once and keep them next to this .iss file (all
;      are official Microsoft download links, safe to re-download periodically for updates):
;        - .NET 8 Desktop Runtime (x64): https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe
;          -> save as installer\WindowsDesktopRuntime8-x64.exe
;        - Visual C++ Redistributable (x64): https://aka.ms/vs/17/release/vc_redist.x64.exe
;          -> save as installer\VC_redist.x64.exe
;        - WebView2 Evergreen Bootstrapper: https://developer.microsoft.com/microsoft-edge/webview2/
;          ("Evergreen Bootstrapper" download) -> save as installer\MicrosoftEdgeWebview2Setup.exe
;      Any of the three missing at compile time is fine (flags: skipifsourcedoesntexist) - that
;      dependency's install step is simply skipped, so build this without them if you know the
;      target machine already has them, but a normal build should have all three present.
;   4. Install Inno Setup (jrsoftware.org/isinfo.php), open this .iss file, click Compile.
;
; The resulting Setup.exe requires internet access on the target machine ONLY if one of the
; three bundled dependency installers actually needs to run (i.e. that dependency isn't already
; present) - each of them is a normal offline installer once downloaded, so no download happens
; at install time, only local execution of the bundled .exe files.

#define MyAppName "D.A.R.K.S.T.A.R."

; The version can be passed in by the build script (ISCC /DMyAppVersion=1.1); this is the
; fallback when the script is compiled directly from the Inno Setup IDE.
;
; The version matters beyond the file name: it is written to the uninstall registry entry as
; DisplayVersion, and the next installer reads it back to tell an upgrade from a reinstall from
; a downgrade (see InitializeSetup below). So bump it for every build you hand out.
#ifndef MyAppVersion
  #define MyAppVersion "1.0"
#endif

; The same version as a strict four-part number for the Setup.exe's own file properties, which
; Windows will not accept in any other shape. build-installer.ps1 derives it from -Version; the
; fallback is for compiling straight from the Inno Setup IDE.
#ifndef MyVersionInfo
  #define MyVersionInfo "1.0.0.0"
#endif

; Defining NoVoskModel (ISCC /DNoVoskModel, which build-installer.ps1 does for -SkipVoskModel)
; builds the slim installer: no bundled speech model, no model component, and no pre-filled
; VoskModelPath - that path then has to be set by hand after installing. Everything else,
; including all dependency handling, is identical.
#ifdef NoVoskModel
  #define SetupSuffix "-slim"
#else
  #define SetupSuffix ""
#endif
#define MyAppPublisher "Darkstar Project"
#define MyServiceName "D.A.R.K.S.T.A.R."
#define MyBotExe "Darkstar.exe"
#define MyGuiExe "Darkstar.ConfigEditor.exe"

[Setup]
; AppId is what makes an upgrade an upgrade: Windows and Inno Setup identify the installed
; application by this value, so a new installer with the same AppId replaces the old version in
; place instead of installing a second copy next to it. NEVER change it - doing so would orphan
; every existing installation, leaving two entries in "Apps & features".
; The [Code] section repeats it in UninstallRegKey; keep the two identical.
AppId={{9F1D6C3E-6E2B-4C0E-9B2A-DARKSTAR0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DARKSTAR
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=DARKSTAR-Setup-{#MyAppVersion}{#SetupSuffix}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible

; --- Upgrading an existing installation -------------------------------------------------------
; Install into the folder the previous version used, and keep the component/task choices that
; were made then, so an upgrade is a Next-Next-Finish affair rather than a re-decision of
; everything. (Both are Inno Setup defaults; stated explicitly because an upgrade depends on them.)
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousSetupType=yes

; Ask Windows' Restart Manager which of our files are in use and offer to close those programs -
; without this, upgrading while the config editor is open fails on a locked file. The bot's
; Windows Service is handled separately in PrepareToInstall, because the Restart Manager would
; kill it rather than stop it properly.
CloseApplications=yes
RestartApplications=no

; Shown in "Apps & features", and read back by the next installer to work out whether it is an
; upgrade, a reinstall or a downgrade.
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyBotExe}

; The Setup.exe's own file properties, so right-click -> Properties shows which build this is.
VersionInfoVersion={#MyVersionInfo}
VersionInfoProductVersion={#MyVersionInfo}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup {#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Bot + Config GUI (recommended)"
Name: "botonly"; Description: "Bot only (headless server)"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "bot"; Description: "D.A.R.K.S.T.A.R. bot service"; Types: full botonly custom; Flags: fixed
Name: "gui"; Description: "Config GUI (Darkstar.ConfigEditor.exe)"; Types: full custom
#ifndef NoVoskModel
Name: "voskmodel"; Description: "Vosk wake-word model (offline speech recognition)"; Types: full botonly custom
#endif

[Files]
; --- Bot service (published framework-dependent - needs the .NET 8 Desktop Runtime installed,
;     handled below) ---
Source: "publish\bot\*"; DestDir: "{app}"; Components: bot; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Config GUI (published framework-dependent) ---
Source: "publish\gui\*"; DestDir: "{app}\Gui"; Components: gui; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Vosk model, unpacked (left out entirely in the slim build) ---
#ifndef NoVoskModel
Source: "vosk-model\*"; DestDir: "{app}\models"; Components: voskmodel; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- Dependency installers, only run if actually missing (see [Run] + [Code] below) ---
Source: "WindowsDesktopRuntime8-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist
Source: "VC_redist.x64.exe"; DestDir: "{tmp}"; Components: bot; Flags: deleteafterinstall skipifsourcedoesntexist
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Components: gui; Flags: deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName} Config Editor"; Filename: "{app}\Gui\{#MyGuiExe}"; Components: gui
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; --- .NET 8 Desktop Runtime: covers the bot's plain .NET 8 runtime requirement AND the GUI's
;     WPF/Blazor Hybrid requirement, so this one install step is not gated to either component -
;     it always runs (unless already present) as long as anything at all is being installed. ---
Filename: "{tmp}\WindowsDesktopRuntime8-x64.exe"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing .NET 8 Desktop Runtime..."; \
    Check: not DotNetDesktopRuntime8Installed; Flags: waituntilterminated

; --- Visual C++ Redistributable: required by Vosk's native libvosk.dll (the bot's wake-word
;     detection engine). Only relevant to the bot component. ---
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing Visual C++ Redistributable..."; Components: bot; \
    Check: not VCRedistInstalled; Flags: waituntilterminated

; --- WebView2 Runtime: required by the GUI to render its Blazor UI. Only relevant to the GUI
;     component. Most current Windows 10/11 machines already have it. ---
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
    StatusMsg: "Installing WebView2 Runtime..."; Components: gui; \
    Check: not WebView2Installed; Flags: waituntilterminated skipifsilent

; Register the Windows Service (optional, unchecked by default so a first test run doesn't
; surprise anyone - tick the box on the "Ready to Install" page to enable it).
; NOTE on the quoting: the executable path must end up QUOTED inside the service's ImagePath,
; otherwise Windows fails to start a service installed under a path containing spaces (which the
; default "C:\Program Files\..." is). sc.exe strips one level of quoting itself, so the inner
; \" pair is what survives into the registry - hence the ""\"" ... \"""" dance below.
;
; On an upgrade the service is usually already registered (and was stopped in PrepareToInstall
; above). "sc create" would simply fail in that case and leave the old registration behind, so
; the existing one is removed first and recreated - which also repairs a registration pointing
; at a path from an earlier install location.
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#MyServiceName}"""; \
    StatusMsg: "Updating the Windows Service registration..."; \
    Check: DarkstarServiceInstalled; Flags: runhidden waituntilterminated; Tasks: installservice
Filename: "{sys}\sc.exe"; Parameters: "create ""{#MyServiceName}"" binPath= ""\""{app}\{#MyBotExe}\"""" start= auto"; \
    StatusMsg: "Registering Windows Service..."; Flags: runhidden waituntilterminated; Tasks: installservice
Filename: "{sys}\sc.exe"; Parameters: "start ""{#MyServiceName}"""; \
    StatusMsg: "Starting the service..."; Flags: runhidden; Tasks: installservice

Filename: "{app}\Gui\{#MyGuiExe}"; Description: "Launch the Config Editor now"; \
    Flags: nowait postinstall skipifsilent; Components: gui

[UninstallRun]
; Stopping/deleting fails harmlessly when the service was never registered - Inno Setup ignores
; a program's exit code here, so no flag is needed (and none exists) to allow that.
;
; RunOnceId makes each entry run only once even when the same application was installed several
; times over: of all uninstall-log entries sharing an id, only the newest one is executed. The two
; entries need DIFFERENT ids - with a shared one, only the last of them would ever run.
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#MyServiceName}"""; RunOnceId: "StopDarkstarService"; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#MyServiceName}"""; RunOnceId: "DeleteDarkstarService"; \
    Flags: runhidden waituntilterminated

[Tasks]
Name: "installservice"; Description: "Install and start {#MyAppName} as a Windows Service (runs unattended in the background)"; Flags: unchecked

[Code]

// --- Upgrading an existing installation --------------------------------------------------
//
// Installing over a running D.A.R.K.S.T.A.R. fails in a way that is hard to read: the bot's
// Windows Service holds Darkstar.exe open, so copying the new one is refused and Setup ends up
// asking for a reboot. So an upgrade is handled explicitly: find out what is installed, stop
// the service before any file is touched, and start it again afterwards if it was running.

var
  // The version found in the uninstall registry entry, '' on a first install.
  PreviousVersion: String;
  // Whether this run stopped a running service and therefore owes it a restart.
  ServiceWasStopped: Boolean;

// Must stay identical to AppId in [Setup] - this is the key Inno Setup registers itself under.
function UninstallRegKey(): String;
begin
  Result := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{9F1D6C3E-6E2B-4C0E-9B2A-DARKSTAR0001}_is1';
end;

// The version of D.A.R.K.S.T.A.R. already on this machine, or '' if there is none.
function GetInstalledVersion(): String;
var
  Version: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, UninstallRegKey(), 'DisplayVersion', Version) then
    Result := Version
  else if RegQueryStringValue(HKLM32, UninstallRegKey(), 'DisplayVersion', Version) then
    Result := Version;
end;

// -1 / 0 / 1 for older / same / newer. Versions that don't parse as numbers compare equal, so
// an unusual version string can never block an install.
function CompareVersionStrings(A, B: String): Integer;
var
  PackedA, PackedB: Int64;
begin
  Result := 0;
  if StrToVersion(A, PackedA) and StrToVersion(B, PackedB) then
    Result := ComparePackedVersion(PackedA, PackedB);
end;

// True when the bot's Windows Service is registered. Read from the registry rather than parsed
// out of "sc query" output, whose wording is translated on a localized Windows.
function DarkstarServiceInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MyServiceName}');
end;

// Runs a PowerShell one-liner hidden and waits for it. PowerShell rather than sc.exe because
// "sc stop" returns before the service has actually stopped, and waiting for it via sc means
// parsing localized status text; Get-Service/WaitForStatus does the waiting properly.
function RunPowerShell(Command: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Stops the service and waits until it has really reached "Stopped" - a service that is merely
// "stopping" still holds its executable open. Returns True if it was running and is now stopped.
function StopDarkstarService(): Boolean;
begin
  Result := False;
  if not DarkstarServiceInstalled() then
    exit;

  Result := RunPowerShell(
    '$s = Get-Service -Name ''{#MyServiceName}'' -ErrorAction SilentlyContinue; ' +
    'if (-not $s) { exit 1 }; ' +
    'if ($s.Status -eq ''Stopped'') { exit 1 }; ' +
    'Stop-Service -Name ''{#MyServiceName}'' -Force -ErrorAction Stop; ' +
    '$s.WaitForStatus(''Stopped'', (New-TimeSpan -Seconds 45))');
end;

function StartDarkstarService(): Boolean;
begin
  Result := RunPowerShell(
    'Start-Service -Name ''{#MyServiceName}'' -ErrorAction Stop');
end;

// Runs after the user has confirmed, before a single file is copied - the only point at which
// the service can still be stopped without the file copy having failed already. A non-empty
// return value is shown to the user and aborts the installation.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if not DarkstarServiceInstalled() then
    exit;

  WizardForm.StatusLabel.Caption := 'Stopping the D.A.R.K.S.T.A.R. service...';
  ServiceWasStopped := StopDarkstarService();

  // Not being able to stop it is not necessarily fatal - it may simply not have been running -
  // so let the install continue and let the file copy be the judge of whether it mattered.
end;

function InitializeSetup(): Boolean;
var
  Comparison: Integer;
begin
  Result := True;
  ServiceWasStopped := False;
  PreviousVersion := GetInstalledVersion();

  if PreviousVersion = '' then
    exit; // first install, nothing to compare against

  Comparison := CompareVersionStrings(PreviousVersion, '{#MyAppVersion}');

  if Comparison > 0 then
  begin
    // The only case worth interrupting for: going backwards silently would be confusing, and
    // a newer config.json can carry settings an older bot doesn't understand.
    Result := MsgBox(
      'Version ' + PreviousVersion + ' of ' + '{#MyAppName}' + ' is already installed,' + #13#10 +
      'which is NEWER than this installer (' + '{#MyAppVersion}' + ').' + #13#10 + #13#10 +
      'Installing the older version over it is not recommended.' + #13#10 +
      'Your config.json will be kept either way.' + #13#10 + #13#10 +
      'Continue anyway?',
      mbConfirmation, MB_YESNO) = IDYES;
  end;

  // An upgrade or a reinstall of the same version needs no question - the Ready page says what
  // is about to happen (see UpdateReadyMemo) and everything is kept in place.
end;

// Puts the upgrade information at the top of the summary on the "Ready to Install" page, so it
// is visible before the install starts without costing an extra click.
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Header, Comparison: String;
begin
  Header := '';

  if PreviousVersion <> '' then
  begin
    if CompareVersionStrings(PreviousVersion, '{#MyAppVersion}') = 0 then
      Comparison := 'Reinstalling version ' + PreviousVersion
    else
      Comparison := 'Updating version ' + PreviousVersion + ' to ' + '{#MyAppVersion}';

    Header := Comparison + NewLine +
      Space + 'config.json, phrases.json and vocabulary.json are kept.' + NewLine;

    if DarkstarServiceInstalled() then
      Header := Header + Space + 'The Windows Service is stopped and started again automatically.' + NewLine;

    Header := Header + NewLine;
  end;

  Result := Header +
    MemoDirInfo + NewLine + NewLine +
    MemoTypeInfo + NewLine + NewLine +
    MemoComponentsInfo + NewLine + NewLine +
    MemoTasksInfo;
end;

// Detects an existing .NET 8 Desktop Runtime install by checking for its shared-framework
// folder. The Desktop Runtime installer places a versioned subfolder here for every version
// installed side-by-side, so we just need any folder starting with "8." to exist - we don't
// care about the exact patch version, any 8.x satisfies a net8.0-windows / net8.0 app.
function DotNetDesktopRuntime8Installed(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BasePath) then
    exit;

  if FindFirst(BasePath + '\8.*', FindRec) then
  begin
    Result := True; // at least one 8.x folder exists
    FindClose(FindRec);
  end;
end;

// Detects the Visual C++ 2015-2022 Redistributable (x64) via the registry key it's documented
// to register under, with its "Installed" DWORD set to 1.
function VCRedistInstalled(): Boolean;
var
  InstalledValue: Cardinal;
begin
  Result :=
    RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Installed', InstalledValue)
    and (InstalledValue = 1);
end;

// Detects an existing WebView2 Runtime install via the registry key it registers under.
// If this returns True, we skip running the bootstrapper in [Run] above.
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
    or RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

// --- Finding the SRS installation -------------------------------------------------------
//
// Every reply the bot transmits goes out through DCS-SR-ExternalAudio.exe, which ships with
// DCS-SimpleRadio-Standalone - normally in "C:\Program Files\DCS-SimpleRadio-Standalone\
// ExternalAudio", but older builds put it elsewhere and plenty of people install SRS to
// another drive. Rather than hard-coding one path into config.json and letting the user find
// out at the first failed transmission, the installer looks the real one up.
//
// Kept in sync with Darkstar.Core\SrsPaths.cs, which does the same probing at runtime (that's
// what the config editor's "Detect SRS installation" button on CH1 calls).

// Probes the known layouts below one folder. The folder may be the SRS install directory
// itself or a parent that contains it. Returns '' when nothing matched.
function FindExternalAudioBelow(BaseDir: String): String;
var
  InstallNames: array[0..4] of String;
  RelativePaths: array[0..4] of String;
  i, j: Integer;
  Candidate, InstallDir: String;
begin
  Result := '';
  if (BaseDir = '') or not DirExists(BaseDir) then
    exit;

  // Normalise to "no trailing backslash" so the relative parts below (which all start with one)
  // can simply be appended - including for a drive root, where "C:\" becomes "C:".
  BaseDir := RemoveBackslash(BaseDir);

  // Index 0 is the base folder itself (someone pointing straight at their SRS directory),
  // the rest are the folder names SRS installs itself under.
  InstallNames[0] := '';
  InstallNames[1] := '\DCS-SimpleRadio-Standalone';
  InstallNames[2] := '\DCS-SimpleRadio-Standalone-Server';
  InstallNames[3] := '\DCS-SimpleRadio';
  InstallNames[4] := '\SimpleRadio-Standalone';

  // Where the executable sits inside an SRS install, most likely first.
  RelativePaths[0] := '\ExternalAudio\DCS-SR-ExternalAudio.exe';
  RelativePaths[1] := '\DCS-SR-ExternalAudio.exe';
  RelativePaths[2] := '\Server\ExternalAudio\DCS-SR-ExternalAudio.exe';
  RelativePaths[3] := '\Server\DCS-SR-ExternalAudio.exe';
  RelativePaths[4] := '\Client\ExternalAudio\DCS-SR-ExternalAudio.exe';

  for i := 0 to 4 do
  begin
    InstallDir := BaseDir + InstallNames[i];
    if DirExists(InstallDir) then
    begin
      for j := 0 to 4 do
      begin
        Candidate := InstallDir + RelativePaths[j];
        if FileExists(Candidate) then
        begin
          Result := Candidate;
          exit;
        end;
      end;
    end;
  end;
end;

// Walks the uninstall registry looking for SRS's own entry, and returns the InstallLocation it
// registered. This is the reliable route - it finds SRS wherever it was installed - with the
// path probing below as the fallback for installs that registered no location.
function FindSrsInstallLocationInRegistry(RootKey: Integer): String;
var
  UninstallKey, EntryKey, DisplayName, Location: String;
  Names: TArrayOfString;
  i: Integer;
begin
  Result := '';
  UninstallKey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';
  if not RegGetSubkeyNames(RootKey, UninstallKey, Names) then
    exit;

  for i := 0 to GetArrayLength(Names) - 1 do
  begin
    EntryKey := UninstallKey + '\' + Names[i];
    DisplayName := '';
    if RegQueryStringValue(RootKey, EntryKey, 'DisplayName', DisplayName) then
    begin
      // "DCS-SimpleRadio-Standalone", "DCS SimpleRadio Standalone Server", ... - all of them
      // carry "SimpleRadio", which nothing else on a normal machine does.
      if Pos('simpleradio', Lowercase(DisplayName)) > 0 then
      begin
        // Inno-Setup-built installers (which SRS uses) write both of these, but InstallLocation
        // is sometimes present and empty - so fall through to the other one rather than
        // trusting that the read succeeded.
        if not RegQueryStringValue(RootKey, EntryKey, 'InstallLocation', Location) then
          Location := '';
        if Location = '' then
          if not RegQueryStringValue(RootKey, EntryKey, 'Inno Setup: App Path', Location) then
            Location := '';

        if (Location <> '') and DirExists(Location) then
        begin
          Result := Location;
          exit;
        end;
      end;
    end;
  end;
end;

// Returns the full path to DCS-SR-ExternalAudio.exe, or '' if this machine has no SRS install
// in any place we know to look.
function FindExternalAudioExe(): String;
var
  i: Integer;
  Drive, DriveLetters: String;
begin
  DriveLetters := 'CDEFGHIJKLMNOPQRSTUVWXYZ';

  // 1. Ask the registry where SRS says it lives (64-bit view first, then the 32-bit one).
  Result := FindExternalAudioBelow(FindSrsInstallLocationInRegistry(HKLM64));
  if Result <> '' then exit;
  Result := FindExternalAudioBelow(FindSrsInstallLocationInRegistry(HKLM32));
  if Result <> '' then exit;
  Result := FindExternalAudioBelow(FindSrsInstallLocationInRegistry(HKCU));
  if Result <> '' then exit;

  // 2. The standard install locations.
  Result := FindExternalAudioBelow(ExpandConstant('{commonpf64}'));
  if Result <> '' then exit;
  Result := FindExternalAudioBelow(ExpandConstant('{commonpf32}'));
  if Result <> '' then exit;
  Result := FindExternalAudioBelow(ExpandConstant('{localappdata}\Programs'));
  if Result <> '' then exit;

  // 3. Other drives - installing games to D:\ is common enough to be worth the few DirExists
  //    calls (a letter with no drive behind it fails immediately).
  for i := 1 to Length(DriveLetters) do
  begin
    Drive := Copy(DriveLetters, i, 1) + ':';
    Result := FindExternalAudioBelow(Drive + '\');
    if Result <> '' then exit;
    Result := FindExternalAudioBelow(Drive + '\Program Files');
    if Result <> '' then exit;
    Result := FindExternalAudioBelow(Drive + '\Games');
    if Result <> '' then exit;
  end;

  Result := '';
end;

// Escapes a Windows path for embedding in a JSON string value (JSON needs \\ for a backslash).
function JsonPath(Path: String): String;
begin
  Result := Path;
  StringChangeEx(Result, '\', '\\', True);
end;

// Writes a config.json pre-filled with the paths that only the installer can work out - where
// it put the Vosk model, and where this machine's SRS installation is. This is exactly the
// manual editing step ("VoskModelPath musste angepasst werden") this installer exists to avoid.
// Only runs if config.json doesn't already exist (never overwrites an existing installation's
// settings), and each field is only written when there is something real to write.
procedure WriteInitialConfig();
var
  ConfigPath, Fields, Json, SrsExe: String;
begin
  ConfigPath := ExpandConstant('{app}\config.json');
  if FileExists(ConfigPath) then
    exit; // don't touch an existing config on upgrade/repair

  Fields := '';

#ifndef NoVoskModel
  if WizardIsComponentSelected('voskmodel') then
    Fields := '  "VoskModelPath": "' + JsonPath(ExpandConstant('{app}\models')) + '"';
#endif

  SrsExe := FindExternalAudioExe();
  if SrsExe <> '' then
  begin
    if Fields <> '' then
      Fields := Fields + ',' + #13#10;
    Fields := Fields + '  "ExternalAudioExePath": "' + JsonPath(SrsExe) + '"';
  end;

  if Fields = '' then
    exit; // nothing detected - let the bot write its own default config on first launch

  Json := '{' + #13#10 + Fields + #13#10 + '}' + #13#10;
  SaveStringToFile(ConfigPath, Json, False);
  // The bot's own AppConfig.LoadOrCreateDefault merges in every other field with its normal
  // defaults on first launch (see the "config.json was extended with new settings" log
  // message) - we only seed the values the installer actually knows.
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Runs for every install, including the slim one without the Vosk model: even then there
    // is the detected SRS path to seed. Never touches an existing config.json, so an upgrade
    // keeps every setting.
    WriteInitialConfig();

    // Put back what this installer stopped. Skipped when the "install as a service" task ran,
    // because that already registered and started it a moment ago.
    if ServiceWasStopped and not WizardIsTaskSelected('installservice') then
    begin
      WizardForm.StatusLabel.Caption := 'Starting the D.A.R.K.S.T.A.R. service...';
      if not StartDarkstarService() then
        MsgBox('The update is installed, but the D.A.R.K.S.T.A.R. service could not be started again.' + #13#10 + #13#10 +
               'Start it by hand in services.msc, or from the config editor on CH9 Service.',
               mbInformation, MB_OK);
    end;
  end;
end;
