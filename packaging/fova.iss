; Inno Setup script for Fova. Requires Inno Setup 6 (https://jrsoftware.org/isdl.php).
;
; Build the payload FIRST, then point this at it:
;   pwsh packaging/build-release.ps1 -Version 1.0.0
;   iscc packaging/fova.iss /DSourceDir="<repo>\dist\fova-1.0.0" /DAppVersion=1.0.0
;
; First compiled locally 2026-08-21 (Inno Setup 6 via winget). When the .NET 8 Desktop Runtime
; is missing, the wizard downloads and quietly runs Microsoft's runtime installer — see [Code].
; The .zip from build-release.ps1 remains the portable alternative.

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
; The app runs asInvoker (no elevation, loop 512) and writes its config/logs/data caches
; NEXT TO THE EXE — so it must NOT live in a machine-wide Program Files it cannot write to.
; "lowest" makes {autopf} resolve per-user ({localappdata}\Programs\Fova), which is writable.
PrivilegesRequired=lowest
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
; Inno only removes files IT installed. Everything the app writes at runtime — the Config\
; folder (overlay-config.json), runtime-downloaded ddragon icons and refreshed data under
; Data\, logs\, user_config.json, and the ads cache at {localappdata}\Fova — survives an
; uninstall unless listed here (found by a real uninstall on 2026-08-21: Config\ and
; Data\ddragon were left behind with files inside).
Type: filesandordirs; Name: "{app}\Data"
Type: filesandordirs; Name: "{app}\Config"
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\user_config.json"
; Ads cache lives OUTSIDE the install dir ({localappdata}\Fova, not ...\Programs\Fova).
Type: filesandordirs; Name: "{localappdata}\Fova"
; Finally drop the install dir itself if nothing user-made remains in it.
Type: dirifempty; Name: "{app}"

[Code]
{ The framework-dependent package needs the .NET 8 DESKTOP runtime (the plain runtime is not
  enough for WPF). When it is missing, the wizard downloads Microsoft's evergreen x64 runtime
  installer over HTTPS and runs it quietly, so the user gets a one-installer experience. aka.ms
  serves the latest 8.0.x, so there is no fixed hash to pin — transport security plus the
  Microsoft-signed installer are the integrity story for that download. A self-contained package
  (build-release.ps1 -SelfContained) needs none of this — the check is skipped when the runtime
  is bundled inside the exe. }
var
  NeedsDotNet: Boolean;
  DotNetDownloaded: Boolean;
  DownloadPage: TDownloadWizardPage;

function IsDotNetDesktop8Present(): Boolean;
var
  root: String;
  rec: TFindRec;
begin
  Result := False;
  root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(root) then Exit;
  if FindFirst(root + '\8.*', rec) then
  try
    repeat
      if (rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(rec);
  finally
    FindClose(rec);
  end;
end;

function IsSelfContained(): Boolean;
begin
  { A non-single-file self-contained publish drops the host runtime next to the exe. The shape
    build-release.ps1 actually produces (-SelfContained => PublishSingleFile) embeds hostfxr and
    coreclr INSIDE fova.exe, so neither DLL exists on disk - detect that shape by the absence of
    fova.dll, which a framework-dependent publish always leaves beside the exe. Without this,
    every single-file self-contained install got a false ".NET 8 not found" prompt. }
  Result := FileExists(ExpandConstant('{#SourceDir}\hostfxr.dll'))
         or FileExists(ExpandConstant('{#SourceDir}\coreclr.dll'))
         or (not FileExists(ExpandConstant('{#SourceDir}\fova.dll')));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  NeedsDotNet := (not IsSelfContained()) and (not IsDotNetDesktop8Present());
  DotNetDownloaded := False;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and NeedsDotNet and (not DotNetDownloaded) then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
                     'dotnet8-desktop-runtime.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        DotNetDownloaded := True;
      except
        { Offline or blocked: the app still installs; the runtime can be added by hand later. }
        if MsgBox('Could not download the .NET 8 Desktop Runtime (x64).' #13#13
                  'You can install it later from:' #13
                  'https://dotnet.microsoft.com/download/dotnet/8.0/runtime' #13
                  '(choose "Desktop Runtime" — the plain "Runtime" will not run a WPF app).' #13#13
                  'Continue installing Fova anyway?', mbConfirmation, MB_YESNO) = IDNO then
          Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  code: Integer;
begin
  Result := '';
  if not DotNetDownloaded then Exit;
  { The runtime installer elevates itself (machine-wide, Microsoft-signed); Fova itself stays
    asInvoker. 1638 = a newer/equal version is already installed; 3010 = success, reboot needed. }
  if (not Exec(ExpandConstant('{tmp}\dotnet8-desktop-runtime.exe'), '/install /quiet /norestart',
               '', SW_SHOW, ewWaitUntilTerminated, code))
     or ((code <> 0) and (code <> 1638) and (code <> 3010)) then
    MsgBox('The .NET 8 Desktop Runtime installer did not finish (code ' + IntToStr(code) + ').' #13
           'Fova will still be installed; if it does not start, install the runtime from:' #13
           'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', mbInformation, MB_OK)
  else if code = 3010 then
    NeedsRestart := True;
end;
