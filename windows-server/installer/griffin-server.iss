; Inno Setup script for Griffin Stream Server
; Build with: iscc griffin-server.iss  (or via windows-server\build-release.ps1)
;
; Design notes:
; - Installs PER-USER into %LOCALAPPDATA%\GriffinStream so the server can write its
;   runtime files (server.pfx, authorized_keys.txt, password.hash) next to the exe
;   WITHOUT any code changes and WITHOUT requiring administrator rights.
; - The optional Windows Firewall rule is the only step that needs elevation; it is
;   launched separately with a UAC prompt so the main install stays non-elevated.
; - In-app updates launch this Setup with /VERYSILENT; AppMutex + CloseApplications
;   coordinate a clean replace without force-close dialogs when the app exits first.

#define AppName "Griffin Stream Server"
#define AppPublisher "Griffin Stream"
#define AppExeName "Server.exe"
#define AppUrl "https://griffinstream.app"
; AppVersion can be overridden from the command line: iscc /DAppVersion=1.0.0 ...
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
; SourceDir is the published, self-contained server folder. Override with /DSourceDir=...
#ifndef SourceDir
  #define SourceDir "..\..\dist\server"
#endif

[Setup]
AppId={{8F3B2E1C-7A4D-4C9E-9B2A-2D6E5C1A9B07}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/download
AppContact=griffinstream.app@gmail.com
AppCopyright=Copyright (C) 2026 {#AppPublisher}
AppComments=Low-latency remote desktop server for the Griffin Stream Android app.
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}
DefaultDirName={localappdata}\GriffinStream
DefaultGroupName=Griffin Stream
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=GriffinStreamServer-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
SetupIconFile=griffin.ico
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
LicenseFile=LICENSE.txt
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\griffin.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Match Mutex created in Server Program.cs.
AppMutex=Local\GriffinStreamServer
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; All tasks below are OPTIONAL and unchecked by default. The server runs fine without any of them;
; enable only what you need.
Name: "desktopicon"; Description: "Create a desktop shortcut (optional)"; GroupDescription: "Optional shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Griffin Stream Server automatically when I sign in (optional)"; GroupDescription: "Optional startup:"; Flags: unchecked
Name: "firewall"; Description: "Open Windows Firewall for TCP port 8888 — optional, only needed to accept connections from other devices (requires administrator approval)"; GroupDescription: "Optional network access:"; Flags: unchecked

[Files]
; Bundles the entire self-contained publish output (Server.exe, .NET runtime, ffmpeg.exe, DLLs).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "griffin.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "firewall-allow.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "firewall-remove.ps1"; DestDir: "{app}"; Flags: ignoreversion
; Install README but do not auto-open (no isreadme — that defaults the Finished checkbox on).
Source: "README-SERVER.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Griffin Stream Server"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\griffin.ico"
Name: "{group}\Griffin Stream Website"; Filename: "{#AppUrl}"
Name: "{group}\Uninstall Griffin Stream Server"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Griffin Stream Server"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\griffin.ico"; Tasks: desktopicon

[Registry]
; Optional per-user autostart (no elevation needed). Removed automatically on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GriffinStreamServer"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Optional: add the firewall rule. Launched elevated via a UAC prompt so the main installer
; can remain a non-elevated per-user install.
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -Command ""Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -NoProfile -File \""{app}\firewall-allow.ps1\""'"""; \
  Flags: runhidden postinstall; Tasks: firewall; Description: "Add Windows Firewall rule"
; Optional README open — unchecked by default (first-time users are not pushed into Notepad).
Filename: "{app}\README-SERVER.txt"; Description: "View README (optional)"; \
  Flags: postinstall shellexec skipifsilent skipifdoesntexist unchecked
Filename: "{app}\{#AppExeName}"; Description: "Launch Griffin Stream Server now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort removal of the firewall rule on uninstall (elevated prompt).
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -Command ""Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -NoProfile -File \""{app}\firewall-remove.ps1\""'"""; \
  Flags: runhidden; RunOnceId: "RemoveGriffinFirewall"

[UninstallDelete]
; Remove runtime-generated leftovers the installer doesn't track. User data (paired devices +
; license) is handled separately by the uninstall prompt in [Code] below.
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\crash_log.txt"
Type: dirifempty; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Offer a full clean wipe. The ViGEmBus gamepad driver is a separate, shared kernel driver we
  // never installed, so it is intentionally left in place (removing it could break other apps).
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox(
      'Also remove your paired devices and Pro license from this PC?' + #13#10#13#10 +
      'Choose No if you plan to reinstall and want to keep your pairings and license.',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      DeleteFile(ExpandConstant('{app}\server.pfx'));
      DeleteFile(ExpandConstant('{app}\authorized_keys.txt'));
      DeleteFile(ExpandConstant('{app}\password.hash'));
      // License cache lives in the Roaming profile, not the install folder.
      DelTree(ExpandConstant('{userappdata}\GriffinStream'), True, True, True);
    end;
  end;
end;
