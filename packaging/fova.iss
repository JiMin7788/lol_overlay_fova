; Inno Setup script for Fova. Requires Inno Setup 6 (https://jrsoftware.org/isdl.php).
;
; Build the payload FIRST, then point this at it:
;   pwsh packaging/build-release.ps1 -Version 1.0.0
;   iscc packaging/fova.iss /DSourceDir="<repo>\dist\fova-1.0.0" /DAppVersion=1.0.0
;
; NOTE: this script has NOT been compiled in this repo's CI — Inno Setup is not installed here.
; It is provided as a starting point; expect to run iscc once and fix whatever it complains about
; before trusting the output. The .zip from build-release.ps1 is the path that IS verified.

#ifndef AppVersion
  #define AppVersion "dev"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<published folder>, e.g. /DSourceDir="C:\dev\...\dist\fova-1.0.0"
#endif

#define AppName    "Fova"
#define AppExeName "fova.exe"
#define AppPublisher "Fova"

[Setup]
AppId={{7F3A1C64-5B2E-4E7B-9D14-2C6A8F0B51D3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=fova-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app itself requests administrator (see app.manifest), so the installer may as well
; install per-machine rather than pretend it is a per-user app.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; Win10 2004 is the floor the app targets.
MinVersion=10.0.19041
LicenseFile=
DisableProgramGroupPage=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything the publish produced, Data/ and Assets/ included.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app writes user_config.json and logs/ next to the exe; remove them so an uninstall
; does not leave a half-populated folder behind.
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\user_config.json"

[Code]
{ The framework-dependent package needs the .NET 8 DESKTOP runtime (the plain runtime is not
  enough for WPF). Rather than silently installing a broken app, check and point at the download.
  A self-contained package (build-release.ps1 -SelfContained) needs none of this — the check is
  skipped when the runtime files are bundled alongside the exe. }
function IsDotNetDesktop8Present(): Boolean;
var
  root: String;
  dirs: TArrayOfString;
  i: Integer;
begin
  Result := False;
  root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(root) then Exit;
  if not GetSubDirNames(root, dirs) then Exit;
  for i := 0 to GetArrayLength(dirs) - 1 do
    if Copy(dirs[i], 1, 2) = '8.' then
    begin
      Result := True;
      Exit;
    end;
end;

function IsSelfContained(): Boolean;
begin
  { A self-contained publish drops the host runtime next to the exe. }
  Result := FileExists(ExpandConstant('{#SourceDir}\hostfxr.dll'))
         or FileExists(ExpandConstant('{#SourceDir}\coreclr.dll'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsSelfContained() then Exit;
  if IsDotNetDesktop8Present() then Exit;

  if MsgBox('Fova needs the .NET 8 Desktop Runtime (x64), which was not found.' #13#13
            'Install it first from:' #13
            'https://dotnet.microsoft.com/download/dotnet/8.0/runtime' #13#13
            'Choose "Desktop Runtime" — the plain "Runtime" will not run a WPF app.' #13#13
            'Continue installing anyway?', mbConfirmation, MB_YESNO) = IDNO then
    Result := False;
end;
