; Inno Setup script for the Chrono installer. Built by scripts\build-installer.ps1, which passes
; /DAppVersion and /DSourceDir. It installs for the current user only, so it never asks for admin rights.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Chrono"
#endif

#ifndef AppName
  #define AppName "Chrono"
#endif
#define AppExe "ChronoRecorder.exe"
; Identity and the "Start with Windows" entry. Only tests of this script change them, so a test install can never
; overwrite or remove a real one.
#ifndef AppGuid
  #define AppGuid "{{6F1D2C58-9B47-4E0A-8C3B-5A7D1E4F2B90}"
#endif
#ifndef RunValue
  #define RunValue "Chrono"
#endif
; The process closed, and the cache folder removed, on install and uninstall. Only tests of this script change them.
#ifndef ProcessName
  #define ProcessName "ChronoRecorder.exe"
#endif
#ifndef DataDir
  #define DataDir "{localappdata}\Chrono"
#endif

[Setup]
AppId={#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Chrono
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Chrono-Setup
SetupIconFile=..\client\ChronoRecorder\Assets\chrono.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start Chrono"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Logs, thumbnails and the WebView2 cache. Clips, config.json and library.json are left alone.
Type: filesandordirs; Name: "{#DataDir}"

[Code]
// Closes every running Chrono, wherever it was started from, together with the FFmpeg it launched.
// Leaving one running would lock the files being replaced or removed, and a new launch would only
// hand over to it instead of starting the new version.
procedure StopChrono();
var
  ResultCode: Integer;
  Tries: Integer;
begin
  for Tries := 1 to 5 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#ProcessName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // taskkill returns 128 when there is no such process, which means Chrono is closed.
    if ResultCode = 128 then Break;
    Sleep(400);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopChrono();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopChrono();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Removed: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    // "Start with Windows" writes this value; leaving it would point Windows at a program that is gone.
    Removed := RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}');
    Log('Removed the Start with Windows entry: ' + IntToStr(Ord(Removed)));
  end;
end;
