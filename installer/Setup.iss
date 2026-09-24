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
#ifndef MyAppVersion
  #define MyAppVersion "1.0"
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
AppId={{9F1D6C3E-6E2B-4C0E-9B2A-DARKSTAR0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
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
Filename: "{sys}\sc.exe"; Parameters: "create ""{#MyServiceName}"" binPath= ""\""{app}\{#MyBotExe}\"""" start= auto"; \
    StatusMsg: "Registering Windows Service..."; Flags: runhidden; Tasks: installservice
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

// Writes a config.json pre-filled with the paths that only the installer can know for sure
// (where it actually put the Vosk model, where it put the bot exe) - this is exactly the
// manual editing step ("VoskModelPath musste angepasst werden") this installer exists to avoid.
// Only runs if config.json doesn't already exist (never overwrites an existing installation's settings).
#ifndef NoVoskModel
procedure WriteInitialConfig();
var
  ConfigPath, VoskPath, Json: String;
begin
  ConfigPath := ExpandConstant('{app}\config.json');
  if FileExists(ConfigPath) then
    exit; // don't touch an existing config on upgrade/repair

  VoskPath := ExpandConstant('{app}\models');
  // Use forward slashes in the JSON value's backslashes escaped, since JSON needs \\ for a
  // literal backslash in a Windows path.
  StringChangeEx(VoskPath, '\', '\\', True);

  Json :=
    '{' + #13#10 +
    '  "VoskModelPath": "' + VoskPath + '"' + #13#10 +
    '}' + #13#10;

  SaveStringToFile(ConfigPath, Json, False);
  // The bot's own AppConfig.LoadOrCreateDefault will merge in every other field with its
  // normal defaults on first launch (see the "config.json was extended with new settings"
  // log message) - we only need to seed the one value the installer actually knows.
end;
#endif

procedure CurStepChanged(CurStep: TSetupStep);
begin
#ifndef NoVoskModel
  // WizardIsComponentSelected is the current name; the old IsComponentSelected still works but
  // the compiler emits a deprecation hint for it.
  if (CurStep = ssPostInstall) and WizardIsComponentSelected('voskmodel') then
    WriteInitialConfig();
#endif
end;
