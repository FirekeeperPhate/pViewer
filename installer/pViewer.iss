; Inno Setup installer for pViewer, two editions from the same script:
;   Light: framework-dependent build, needs the .NET 10 Desktop Runtime (small setup)
;   Full:  self-contained build, .NET runtime included (no prerequisites)
; Build with build.ps1, which publishes to ..\publish\<edition> and passes
; /DFlavor=Light|Full and /DAppVersion=<version from pViewer.csproj>.

#ifndef Flavor
  #define Flavor "Light"
#endif
#if Flavor != "Light" && Flavor != "Full"
  #error Flavor must be Light or Full
#endif
#ifndef AppVersion
  #error AppVersion is required (pass /DAppVersion=x.y.z)
#endif

#define AppName "pViewer"
#define AppExe "pViewer.exe"
; Extensions registered by the "Open with" task (also removed when it is unticked on an upgrade)
#define OpenWithExts ".jpg .jpeg .jpe .jfif .png .gif .bmp .dib .tif .tiff .ico .webp .heic .heif .avif .jxl .jxr .wdp .tga .qoi .cbz .cbr .cb7"
#define SourceDir AddBackslash(SourcePath) + "..\publish\" + LowerCase(Flavor)

; Registry lines that add pViewer to the "Open with" list of an extension.
#define OpenWith(Ext, ProgId) \
  "Root: HKA; Subkey: ""Software\Classes\" + Ext + "\OpenWithProgids""; ValueType: string; ValueName: """ + ProgId + """; ValueData: """"; Flags: uninsdeletevalue; Tasks: openwith" + NewLine + \
  "Root: HKA; Subkey: ""Software\Classes\Applications\" + AppExe + "\SupportedTypes""; ValueType: string; ValueName: """ + Ext + """; ValueData: """"; Flags: uninsdeletekey; Tasks: openwith" + NewLine + \
  "Root: HKA; Subkey: ""Software\pViewer\Capabilities\FileAssociations""; ValueType: string; ValueName: """ + Ext + """; ValueData: """ + ProgId + """; Flags: uninsdeletekey; Tasks: openwith" + NewLine

[Setup]
; One AppId for both editions: Light and Full replace each other
AppId={{03AEDA4F-AF78-4EC2-89CB-CE68A794A15F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Flavor})
AppPublisher=Phate
AppPublisherURL=https://github.com/FirekeeperPhate/pViewer
AppSupportURL=https://github.com/FirekeeperPhate/pViewer/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin rights needed); all users (with UAC) can be chosen
; in the first dialog. HKA below follows the choice (HKCU or HKLM).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=pViewer-Setup-{#AppVersion}-{#Flavor}
SetupIconFile=..\src\pViewer\Assets\pv.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} ({#Flavor})
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
; Created by the running app: setup and uninstall ask to close pViewer first
AppMutex=pViewer.Running
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
RuntimeMissing=pViewer requires the .NET 10 Desktop Runtime (x64), which does not appear to be installed.%n%nYes = open the download page and close setup%nNo = install anyway%nCancel = close setup%n%nAlternatively use the Full edition, which includes the runtime.
OpenWithTask=Add pViewer to the "Open with" menu of images and comics (.cbz, .cbr, .cb7)

[Tasks]
Name: "openwith"; Description: "{cm:OpenWithTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; Excludes: "*.pdb,settings.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Parent of Capabilities: removed at uninstall once empty
Root: HKA; Subkey: "Software\pViewer"; Flags: uninsdeletekeyifempty
; ProgIds used by "Open with" and by Settings > Default apps
Root: HKA; Subkey: "Software\Classes\pViewer.Image"; ValueType: string; ValueData: "Image"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pViewer.Image\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pViewer.Image\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pViewer.Comic"; ValueType: string; ValueData: "Comic book archive"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pViewer.Comic\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pViewer.Comic\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
; Registered application: makes pViewer selectable in Settings > Default apps (never forced)
Root: HKA; Subkey: "Software\pViewer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\pViewer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Image viewer and small editor, with comic and manga reading from archives"; Tasks: openwith
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "pViewer"; ValueData: "Software\pViewer\Capabilities"; Flags: uninsdeletevalue; Tasks: openwith
{#OpenWith(".jpg", "pViewer.Image")}
{#OpenWith(".jpeg", "pViewer.Image")}
{#OpenWith(".jpe", "pViewer.Image")}
{#OpenWith(".jfif", "pViewer.Image")}
{#OpenWith(".png", "pViewer.Image")}
{#OpenWith(".gif", "pViewer.Image")}
{#OpenWith(".bmp", "pViewer.Image")}
{#OpenWith(".dib", "pViewer.Image")}
{#OpenWith(".tif", "pViewer.Image")}
{#OpenWith(".tiff", "pViewer.Image")}
{#OpenWith(".ico", "pViewer.Image")}
{#OpenWith(".webp", "pViewer.Image")}
{#OpenWith(".heic", "pViewer.Image")}
{#OpenWith(".heif", "pViewer.Image")}
{#OpenWith(".avif", "pViewer.Image")}
{#OpenWith(".jxl", "pViewer.Image")}
{#OpenWith(".jxr", "pViewer.Image")}
{#OpenWith(".wdp", "pViewer.Image")}
{#OpenWith(".tga", "pViewer.Image")}
{#OpenWith(".qoi", "pViewer.Image")}
{#OpenWith(".cbz", "pViewer.Comic")}
{#OpenWith(".cbr", "pViewer.Comic")}
{#OpenWith(".cb7", "pViewer.Comic")}

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Update started by pViewer itself (/SILENT /RELAUNCH=1 /OPEN="file"): start it again on that file,
; as the user who ran it (not elevated, also for an all-users install).
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchParameters}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Code]
{ Switching edition (Full <-> Light) or upgrading: remove the previous program files so no
  stale runtime or library DLLs are left behind. Both editions publish everything flat in the
  program folder, so only files are touched (never folders the user may have put there);
  settings.json and the uninstaller files (unins*) are kept. }
procedure CleanProgramFolder;
var
  App, Name: String;
  FindRec: TFindRec;
begin
  App := ExpandConstant('{app}\');
  { Only a folder this setup installed before: a pViewer.exe alone (the old portable build kept in a
    folder of tools) must not get its neighbours deleted. }
  if (WizardForm.PrevAppDir = '') or
     (CompareText(AddBackslash(WizardForm.PrevAppDir), App) <> 0) or
     not FileExists(App + '{#AppExe}') then
    Exit;
  { The program itself first: if it cannot be deleted pViewer is still running, and nothing else
    must be removed (an interrupted setup would leave a program that cannot start). }
  if not DeleteFile(App + '{#AppExe}') then
    Exit;
  if FindFirst(App + '*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           (CompareText(Name, 'settings.json') <> 0) and
           (CompareText(Copy(Name, 1, 5), 'unins') <> 0) then
          DeleteFile(App + Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ The "Open with" task unticked on an upgrade: Inno skips the registry lines of an unselected
  task, so the registrations of the previous install would stay until uninstall. }
procedure RemoveOpenWith;
var
  Exts: String;
  Ext: String;
  P: Integer;
begin
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\pViewer.Image');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\pViewer.Comic');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\Applications\{#AppExe}');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\pViewer\Capabilities');
  RegDeleteValue(HKA, 'Software\RegisteredApplications', 'pViewer');
  Exts := '{#OpenWithExts} ';
  while Length(Exts) > 0 do
  begin
    P := Pos(' ', Exts);
    Ext := Copy(Exts, 1, P - 1);
    Delete(Exts, 1, P);
    if Ext <> '' then
    begin
      RegDeleteValue(HKA, 'Software\Classes\' + Ext + '\OpenWithProgids', 'pViewer.Image');
      RegDeleteValue(HKA, 'Software\Classes\' + Ext + '\OpenWithProgids', 'pViewer.Comic');
    end;
  end;
end;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
external 'CloseHandle@kernel32.dll stdcall';

{ Update started by pViewer (/NOTIFYPID=<its process id>): tell it that setup is really starting
  (after the UAC prompt of an all-users install; if that is refused, pViewer stays open), then
  give it time to close before the AppMutex check, which comes after InitializeSetup. }
procedure ReleasePViewer;
var
  Pid: Integer;
  Ready: THandle;
  Waited: Integer;
begin
  Pid := StrToIntDef(ExpandConstant('{param:NOTIFYPID|0}'), 0);
  if Pid > 0 then
  begin
    Ready := OpenEvent($0002 { EVENT_MODIFY_STATE }, False, 'pViewer.UpdateReady.' + IntToStr(Pid));
    if Ready <> 0 then
    begin
      SetEvent(Ready);
      CloseHandle(Ready);
    end;
  end;
  if ExpandConstant('{param:RELAUNCH|0}') = '1' then
  begin
    Waited := 0;
    while CheckForMutexes('pViewer.Running') and (Waited < 15000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
end;

function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

function RelaunchParameters(Param: String): String;
var
  FileToOpen: String;
begin
  FileToOpen := ExpandConstant('{param:OPEN|}');
  if FileToOpen <> '' then
    Result := AddQuotes(FileToOpen)
  else
    Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanProgramFolder;
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('openwith') then
    RemoveOpenWith;
end;

#if Flavor == "Light"
{ Looks for a release (not preview) x64 .NET 10 Desktop Runtime. On ARM64 Windows the x64 runtime
  lives in dotnet\x64, the plain dotnet folder holds the native ARM64 one. }
function HasDesktopRuntime(Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0) then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ The places the app host itself looks in: DOTNET_ROOT, the registered install location and the
  standard folder. A per-user copy (dotnet-install in %LocalAppData%) is not among them: pViewer
  would not start with it, so it does not count. }
function IsDesktopRuntimeInstalled: Boolean;
var
  Registered: String;
begin
  Result := HasDesktopRuntime(GetEnv('DOTNET_ROOT_X64')) or HasDesktopRuntime(GetEnv('DOTNET_ROOT'));
  if Result then
    Exit;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Registered) then
    Result := HasDesktopRuntime(Registered);
  if Result then
    Exit;
  if IsArm64 then
    Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet\x64'))
  else
    Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet'));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntimeInstalled then
    case SuppressibleMsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNOCANCEL, IDNO) of
      IDYES:
        begin
          ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
          Result := False;
        end;
      IDCANCEL:
        Result := False;
    end;
  if Result then
    ReleasePViewer;
end;
#else
function InitializeSetup: Boolean;
begin
  ReleasePViewer;
  Result := True;
end;
#endif
