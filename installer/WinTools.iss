; WinTools per-user installer (Inno Setup 6.7 or later). Build with installer\Build-Installer.ps1, which publishes the
; self-contained payload into artifacts\installer\payload first. Decisions: docs/adr/ADR-0008-packaging-strategy.md.
;
; - Per user, no elevation: %LOCALAPPDATA%\Programs\WinTools, HKCU uninstall entry, Start Menu shortcut for this user.
; - Never touches user data: settings (%APPDATA%\WinTools) and logs (%LOCALAPPDATA%\WinTools) survive upgrade and
;   uninstall. "Start with Windows" is never turned on here; uninstall removes that Run value only if it points to
;   this installation.
; - A running WinTools is detected through its single-instance mutex; Setup and Uninstall ask the user to exit it and
;   never replace files while it runs.
; - AppId is the product's upgrade identity: never change it, or upgrades become second installations.

#define AppName "WinTools"
; The version comes from Build-Installer.ps1 (-Version, -DisplayLabel, -FileLabel); these are the defaults for a bare ISCC run.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppVersionLabel
  #define AppVersionLabel "0.1.0 Beta"
#endif
#ifndef AppFileLabel
  #define AppFileLabel AppVersion + "-Beta"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\installer\payload"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{6B1BF2F8-4DD0-49D7-B036-8918F771AF80}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersionLabel}
AppPublisher={#AppName}
AppComments=Image tools beta: drag images in File Explorer, press Space, choose an action.
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersionLabel}
VersionInfoDescription={#AppName} {#AppVersionLabel} Setup
UninstallDisplayName={#AppName} {#AppVersionLabel}
UninstallDisplayIcon={app}\WinTools.Host.exe
SetupIconFile=..\src\WinTools.Host\Assets\WinTools.ico
; Per user only: no UAC prompt, and no switch to an all-users installation.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
UsePreviousAppDir=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=WinTools-{#AppFileLabel}-Setup
; The running-instance check below replaces Restart Manager, which cannot ask the windowless host to exit.
CloseApplications=no
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\WinTools.Host.exe"; WorkingDir: "{app}"; Comment: "Drag images in File Explorer, press Space, choose an action"

[Run]
Filename: "{app}\WinTools.Host.exe"; WorkingDir: "{app}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  HostMutex = 'Local\WinTools.Host';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ True when no WinTools host is running, after asking the user to exit it as often as they choose to retry. }
function WinToolsClosed(const Action: String): Boolean;
begin
  Result := True;
  while CheckForMutexes(HostMutex) do
  begin
    if SuppressibleMsgBox('WinTools is running.' + #13#10 + #13#10 +
      'Please exit it first: right-click the WinTools icon in the notification area and choose "Exit WinTools". ' +
      'Then click Retry to continue ' + Action + '.', mbInformation, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := WinToolsClosed('the installation');
end;

{ Checked again just before files are copied, in case WinTools was started while the wizard was open. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not WinToolsClosed('the installation') then
    Result := 'WinTools is still running. Exit it from its tray icon and run Setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := WinToolsClosed('removing WinTools');
end;

{ "Start with Windows" belongs to the user's settings, but a Run value that points to the removed program is stale. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Stored, Ours: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Ours := '"' + ExpandConstant('{app}\WinTools.Host.exe') + '"';
  if RegQueryStringValue(HKCU, RunKey, 'WinTools', Stored) and ((CompareText(Trim(Stored), Ours) = 0) or (CompareText(Trim(Stored), Ours + ' --background') = 0)) then
    RegDeleteValue(HKCU, RunKey, 'WinTools');
end;
