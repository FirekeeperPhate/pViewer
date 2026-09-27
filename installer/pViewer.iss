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
AppPublisherURL=https://github.com/MarcoTrombetta/pViewer
AppSupportURL=https://github.com/MarcoTrombetta/pViewer/issues
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
  if not FileExists(App + '{#AppExe}') then
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanProgramFolder;
end;

#if Flavor == "Light"
{ Looks for a release (not preview) x64 .NET 10 Desktop Runtime. On ARM64 Windows the x64 runtime
  lives in dotnet\x64, the plain dotnet folder holds the native ARM64 one. }
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  if IsArm64 then
    Root := ExpandConstant('{commonpf64}\dotnet\x64')
  else
    Root := ExpandConstant('{commonpf64}\dotnet');
  if FindFirst(Root + '\shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
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

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if IsDesktopRuntimeInstalled then
    Exit;
  case SuppressibleMsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNOCANCEL, IDNO) of
    IDYES:
      begin
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
        Result := False;
      end;
    IDCANCEL:
      Result := False;
  end;
end;
#endif
