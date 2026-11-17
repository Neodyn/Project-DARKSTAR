; D.A.R.K.S.T.A.R. radio wheel installer - for the PLAYER's PC (Inno Setup 6.x)
;
; The server gets DARKSTAR-Setup-<version>.exe (Setup.iss). Everybody who flies on that server and
; wants the wheel gets this one: DARKSTAR-Wheel-Setup-<version>.exe. Nothing in it concerns the
; server - no bot, no config editor, no service, no speech model.
;
; WHAT THIS DOES:
;   - Installs Darkstar.Wheel.exe for the current Windows user, without asking for administrator
;     rights. A pilot's PC is a gaming PC, not a server; a per-user install needs no UAC prompt and
;     cannot touch anything outside that user's profile.
;   - Copies the client hook Darkstar-Wheel.lua into Saved Games\<DCS>\Scripts\Hooks for every DCS
;     the user picks. The page lists what it finds under Saved Games (DCS, DCS.openbeta, ...) and
;     leaves out the dedicated-server folders, which have no use for a client hook. The hooks it
;     wrote are recorded, so updating with a different choice and uninstalling remove them again.
;   - Installs the .NET 8 Desktop Runtime if it is missing. That is the one step that needs
;     administrator rights, so Windows asks for them then - and only then.
;   - Optionally starts the wheel with Windows (a shortcut in the user's Startup folder). It sits
;     in the notification area and does nothing until DCS is in front.
;
; What stays when it is uninstalled: the player's settings, %LOCALAPPDATA%\Darkstar\wheel.json
; (learnt key, joystick button, callsign). Reinstalling picks them up again.
;
; BUILD: build-installer.ps1 publishes src\Darkstar.Wheel into installer\publish\wheel and compiles
; this script right after Setup.iss. By hand:
;   dotnet publish src\Darkstar.Wheel\Darkstar.Wheel.csproj -c Release -r win-x64 --self-contained false -o installer\publish\wheel
;   then compile this file with Inno Setup. WindowsDesktopRuntime8-x64.exe next to it is optional,
;   exactly as for Setup.iss.

#define MyAppName "D.A.R.K.S.T.A.R. Radio Wheel"

#ifndef MyAppVersion
  #define MyAppVersion "1.0"
#endif

#ifndef MyVersionInfo
  #define MyVersionInfo "1.0.0.0"
#endif

#define MyAppPublisher "Darkstar Project"
#define MyWheelExe "Darkstar.Wheel.exe"
#define MyHookFile "Darkstar-Wheel.lua"

#ifdef WithSymbols
  #define PayloadExcludes ""
#else
  #define PayloadExcludes "*.pdb,*.xml"
#endif

[Setup]
; Its own AppId: the wheel is a separate product on a separate PC, and must never be taken for an
; upgrade of the server installation (or the other way round). NEVER change it, for the same
; reason as in Setup.iss.
AppId={{4E0C9A71-2B6D-4F3A-8E15-DARKSTAR0002}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile=..\src\Darkstar.ico
; Per user: {autopf} is %LOCALAPPDATA%\Programs in a non-administrative install.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Darkstar Wheel
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=DARKSTAR-Wheel-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Published for win-x64 only, like the bot.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
UsePreviousTasks=yes
; The running wheel holds its .exe open. PrepareToInstall closes it; the Restart Manager is the
; fallback for anything else that has a file of ours open.
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyWheelExe}
VersionInfoVersion={#MyVersionInfo}
VersionInfoProductVersion={#MyVersionInfo}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup {#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The wheel, and with it dcs\Scripts\Hooks\Darkstar-Wheel.lua - the [Code] below copies that one
; into the DCS folders chosen on the hook page.
Source: "publish\wheel\*"; DestDir: "{app}"; Excludes: "{#PayloadExcludes}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The same runtime installer Setup.iss bundles. Only run when the runtime is missing.
Source: "WindowsDesktopRuntime8-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist

[Tasks]
Name: "autostart"; Description: "Start the radio wheel with Windows (it waits in the notification area until DCS is in front)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\Darkstar Radio Wheel"; Filename: "{app}\{#MyWheelExe}"
Name: "{group}\Uninstall the Darkstar Radio Wheel"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Darkstar Radio Wheel"; Filename: "{app}\{#MyWheelExe}"; Tasks: desktopicon
; A Startup-folder shortcut rather than a Run key: it is visible and removable in the place
; Windows users look for autostart entries, and the uninstaller removes it like any other icon.
Name: "{userstartup}\Darkstar Radio Wheel"; Filename: "{app}\{#MyWheelExe}"; Tasks: autostart

[Run]
; shellexec: the runtime installer demands administrator rights, which a process started from a
; non-administrative setup only gets through ShellExecute - that is what makes Windows show the
; UAC prompt instead of failing the start.
Filename: "{tmp}\WindowsDesktopRuntime8-x64.exe"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing the .NET 8 Desktop Runtime (Windows asks for permission)..."; \
    Check: RuntimeToInstall; Flags: shellexec waituntilterminated

Filename: "{app}\{#MyWheelExe}"; Description: "Start the radio wheel now"; \
    Flags: nowait postinstall skipifsilent; Check: DotNetDesktopRuntime8Installed

[UninstallDelete]
; Written by [Code], so the uninstaller does not know it on its own.
Type: files; Name: "{app}\hooks.txt"

[Code]

var
  // The hook page, and the Saved Games\<DCS> folder behind each of its boxes.
  HookPage: TInputOptionWizardPage;
  HookFolders: TArrayOfString;

// --- The .NET 8 Desktop Runtime ---------------------------------------------------------------

// Same check as Setup.iss: any 8.x folder of the shared desktop framework. Readable without
// administrator rights.
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
    Result := True;
    FindClose(FindRec);
  end;
end;

// Missing, and the installer for it was bundled. Without the second half a build made with
// -SkipDependencyDownload would end in "file not found" instead of in the hint at the end.
function RuntimeToInstall(): Boolean;
begin
  Result := (not DotNetDesktopRuntime8Installed()) and
            FileExists(ExpandConstant('{tmp}\WindowsDesktopRuntime8-x64.exe'));
end;

// --- Which DCS gets the hook ------------------------------------------------------------------

// A Saved Games folder of a DCS a pilot flies with: DCS, DCS.openbeta, ... - not a dedicated
// server's (DCS.server, DCS.openbeta_server, DCS.release_server), which never runs as a client.
function IsClientDcsFolder(Name: String): Boolean;
begin
  Result := (Pos('dcs', Lowercase(Name)) = 1) and (Pos('server', Lowercase(Name)) = 0);
end;

procedure AddHookFolder(Folder: String);
var
  Count: Integer;
begin
  Count := GetArrayLength(HookFolders);
  SetArrayLength(HookFolders, Count + 1);
  HookFolders[Count] := Folder;
end;

procedure InitializeWizard();
var
  FindRec: TFindRec;
  SavedGames: String;
  i: Integer;
begin
  SavedGames := ExpandConstant('{usersavedgames}');
  SetArrayLength(HookFolders, 0);

  if FindFirst(SavedGames + '\DCS*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and IsClientDcsFolder(FindRec.Name) then
          AddHookFolder(SavedGames + '\' + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  HookPage := CreateInputOptionPage(wpSelectTasks,
    'DCS hook',
    'Which DCS do you fly with?',
    'The wheel types its requests into the DCS chat through a small hook, ' + '{#MyHookFile}' + ', ' +
    'in Saved Games\<DCS>\Scripts\Hooks. Pick every DCS installation you fly on the server with. ' +
    'DCS has to be restarted once afterwards to load it.',
    False, False);

  if GetArrayLength(HookFolders) = 0 then
  begin
    // DCS creates its Saved Games folder on the first start. Offering the standard one anyway
    // means the hook is in place by the time it does.
    AddHookFolder(SavedGames + '\DCS');
    HookPage.Add('Saved Games\DCS  (not there yet - DCS has not been started on this account)');
  end
  else
    for i := 0 to GetArrayLength(HookFolders) - 1 do
      HookPage.Add('Saved Games\' + ExtractFileName(HookFolders[i]));

  for i := 0 to GetArrayLength(HookFolders) - 1 do
    HookPage.Values[i] := True;
end;

function NothingChosen(): Boolean;
var
  i: Integer;
begin
  Result := True;
  for i := 0 to GetArrayLength(HookFolders) - 1 do
    if HookPage.Values[i] then
      Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = HookPage.ID) and NothingChosen() then
    Result := MsgBox(
      'Without the hook the wheel opens and closes, but nothing it sends reaches DCS.' + #13#10 + #13#10 +
      'Continue without it? (Copy dcs\Scripts\Hooks\' + '{#MyHookFile}' + ' from the install folder by hand later.)',
      mbConfirmation, MB_YESNO) = IDYES;
end;

function HookPath(Folder: String): String;
begin
  Result := Folder + '\Scripts\Hooks\' + '{#MyHookFile}';
end;

// The summary on the "Ready to Install" page says where the hook goes, since that is the one
// thing this installer writes outside its own folder.
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Hooks: String;
  i: Integer;
begin
  Hooks := '';
  for i := 0 to GetArrayLength(HookFolders) - 1 do
    if HookPage.Values[i] then
      Hooks := Hooks + Space + HookPath(HookFolders[i]) + NewLine;
  if Hooks = '' then
    Hooks := Space + '(none - nothing the wheel sends reaches DCS)' + NewLine;

  Result := MemoDirInfo + NewLine + NewLine +
    'DCS hook:' + NewLine + Hooks + NewLine +
    MemoTasksInfo;
end;

// --- Closing the running wheel ----------------------------------------------------------------

// The wheel is a tray program without a window to close, so it is ended outright. Nothing is lost:
// it saves its settings the moment they change. Ending somebody else's copy is refused by Windows,
// which is fine - this user's copy is the one in this user's folder.
procedure CloseRunningWheel();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyWheelExe} /F', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
  CloseRunningWheel();
end;

// --- Writing and removing the hooks ------------------------------------------------------------

function HookListFile(): String;
begin
  Result := ExpandConstant('{app}\hooks.txt');
end;

// Removes a hook this installer wrote. Only ever a file with exactly the hook's name, whatever
// hooks.txt says - the list is a text file anybody could have edited.
procedure RemoveHook(Path: String);
begin
  if CompareText(ExtractFileName(Path), '{#MyHookFile}') = 0 then
    DeleteFile(Path);
end;

procedure InstallHooks();
var
  Source, Target, Failed: String;
  Previous, Written: TArrayOfString;
  i, j, Count: Integer;
  StillWanted: Boolean;
begin
  Source := ExpandConstant('{app}\dcs\Scripts\Hooks\{#MyHookFile}');
  Failed := '';
  SetArrayLength(Written, 0);

  for i := 0 to GetArrayLength(HookFolders) - 1 do
  begin
    if not HookPage.Values[i] then
      continue;

    Target := HookPath(HookFolders[i]);
    if ForceDirectories(ExtractFileDir(Target)) and FileCopy(Source, Target, False) then
    begin
      Count := GetArrayLength(Written);
      SetArrayLength(Written, Count + 1);
      Written[Count] := Target;
    end
    else
      Failed := Failed + '  ' + Target + #13#10;
  end;

  // An update with a DCS unticked that had the hook before: take it out there, rather than
  // leaving a hook nobody asked for any more.
  if LoadStringsFromFile(HookListFile(), Previous) then
    for i := 0 to GetArrayLength(Previous) - 1 do
    begin
      StillWanted := False;
      for j := 0 to GetArrayLength(Written) - 1 do
        if CompareText(Previous[i], Written[j]) = 0 then
          StillWanted := True;
      if (not StillWanted) and (Trim(Previous[i]) <> '') then
        RemoveHook(Previous[i]);
    end;

  SaveStringsToFile(HookListFile(), Written, False);

  if Failed <> '' then
    MsgBox('The hook could not be written to:' + #13#10 + Failed + #13#10 +
           'Copy dcs\Scripts\Hooks\' + '{#MyHookFile}' + ' from ' + ExpandConstant('{app}') +
           ' there by hand.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    exit;

  InstallHooks();

  if not DotNetDesktopRuntime8Installed() then
    MsgBox('The radio wheel needs the .NET 8 Desktop Runtime, which is not installed.' + #13#10 + #13#10 +
           'Install it from https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe' + #13#10 +
           '(administrator rights needed), then start the wheel from the Start menu.',
           mbInformation, MB_OK);
end;

// --- Uninstalling ------------------------------------------------------------------------------

function InitializeUninstall(): Boolean;
begin
  CloseRunningWheel();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Hooks: TArrayOfString;
  i: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;

  // Before the files go - hooks.txt is one of them.
  if LoadStringsFromFile(HookListFile(), Hooks) then
    for i := 0 to GetArrayLength(Hooks) - 1 do
      if Trim(Hooks[i]) <> '' then
        RemoveHook(Hooks[i]);
end;
