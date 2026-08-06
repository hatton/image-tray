; A deliberately small installer: one exe into a per-user folder, one Start menu
; entry, then launch. There is nothing to configure, so there are no wizard pages
; and no desktop shortcut. Run it with /VERYSILENT for no window at all.
;
; Built by ../build-installer.ps1, which publishes Release first.

#define AppName "Screenshot Tray"
#define AppExeName "ScreenshotTray.exe"
#define SourceExe "..\publish\ScreenshotTray.exe"
#define AppVersion GetVersionNumbersString(SourceExe)

[Setup]
; Never change AppId: it is how an upgrade recognises an existing install.
AppId={{8F3C9A54-1B2D-4E7A-9C61-6D5B0A7E42F1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Hatton
AppSupportURL=https://github.com/hatton/screenshot-tray
DefaultDirName={localappdata}\Programs\ScreenshotTray
DefaultGroupName={#AppName}

; Per-user, so no UAC prompt and no admin rights needed.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible

OutputDir=..\dist
OutputBaseFilename=ScreenshotTraySetup
SetupIconFile=..\src\ScreenshotTray\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; Nothing to ask, nothing to report.
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start menu only. No {autodesktop} entry on purpose.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
; The app writes its own autostart entry when the setting is on. Installing must
; not touch it, but uninstalling should take it away rather than leave a Run
; value pointing at a deleted exe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ScreenshotTray"; Flags: dontcreatekey uninsdeletevalue

[Run]
; No postinstall flag: that would put a checkbox on the finished page we disabled,
; and would skip launching on a silent install.
Filename: "{app}\{#AppExeName}"; Flags: nowait

[Code]

{ The exe is framework-dependent, so without the desktop runtime the install
  would succeed and the app would then fail to start with nothing to explain it. }
function DesktopRuntimeFound: Boolean;
var
  FindRec: TFindRec;
  Pattern: String;
begin
  Result := False;
  Pattern := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*');

  if FindFirst(Pattern, FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := DesktopRuntimeFound;

  if not Result then
    MsgBox(
      'Screenshot Tray needs the .NET 10 Desktop Runtime (x64), which is not installed.' + #13#10#13#10 +
      'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.',
      mbCriticalError, MB_OK);
end;

{ A running copy holds the exe open. taskkill returns as soon as it has asked, so
  give the handle a moment to actually close. }
procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);
end;

{ An upgrade has to replace the exe. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

{ And uninstalling has to delete it. This app lives in the tray, so it is almost
  always running; without this the exe survives, and by then the uninstaller has
  removed itself and nothing is left to retry. }
function InitializeUninstall: Boolean;
begin
  StopRunningApp;
  Result := True;
end;
