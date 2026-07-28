; MikeBrowser installer (Inno Setup). Per-user install (no UAC prompt): drops the app in
; %LocalAppData%\Programs\MikeBrowser, creates a Desktop + Start-menu shortcut with the icon,
; and registers MikeBrowser as a browser so it appears in Windows "Default apps".

#define AppName "MikeBrowser"
#define AppVer "0.1.0"

[Setup]
AppId={{7B2E9C10-4E2A-4E7C-9E2A-8F3A1C2D4E5B}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=MikeOS
AppPublisherURL=https://browser.osmike.com
DefaultDirName={localappdata}\Programs\MikeBrowser
DefaultGroupName=MikeBrowser
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
OutputDir=.
OutputBaseFilename=MikeBrowserSetup
SetupIconFile=src\MikeBrowserWin\assets\mikebrowser.ico
UninstallDisplayIcon={app}\mikebrowser.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "publish\MikeBrowser.exe";                      DestDir: "{app}"; Flags: ignoreversion
Source: "src\MikeBrowserWin\assets\mikebrowser.ico";    DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MikeBrowser";        Filename: "{app}\MikeBrowser.exe"; IconFilename: "{app}\mikebrowser.ico"
Name: "{autodesktop}\MikeBrowser";  Filename: "{app}\MikeBrowser.exe"; IconFilename: "{app}\mikebrowser.ico"

[Registry]
; --- Register as a browser (per-user) so it shows in Settings > Default apps ---
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser"; ValueType: string; ValueData: "MikeBrowser"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\DefaultIcon"; ValueType: string; ValueData: "{app}\mikebrowser.ico,0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\shell\open\command"; ValueType: string; ValueData: """{app}\MikeBrowser.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "MikeBrowser"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Your bookmarks & passwords, synced. Light on CPU and memory."
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\mikebrowser.ico,0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\Capabilities\URLAssociations"; ValueType: string; ValueName: "http";  ValueData: "MikeBrowserHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\MikeBrowser\Capabilities\URLAssociations"; ValueType: string; ValueName: "https"; ValueData: "MikeBrowserHTML"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "MikeBrowser"; ValueData: "Software\Clients\StartMenuInternet\MikeBrowser\Capabilities"; Flags: uninsdeletevalue
; --- ProgID that actually opens the URL ---
Root: HKCU; Subkey: "Software\Classes\MikeBrowserHTML"; ValueType: string; ValueData: "MikeBrowser Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MikeBrowserHTML\DefaultIcon"; ValueType: string; ValueData: "{app}\mikebrowser.ico,0"
Root: HKCU; Subkey: "Software\Classes\MikeBrowserHTML\shell\open\command"; ValueType: string; ValueData: """{app}\MikeBrowser.exe"" ""%1"""

[Run]
Filename: "{app}\MikeBrowser.exe"; Description: "Launch MikeBrowser now"; Flags: nowait postinstall skipifsilent
; Win11 requires the user to confirm the default in Settings — open it for them.
Filename: "ms-settings:defaultapps"; Description: "Set MikeBrowser as your default browser"; Flags: shellexec postinstall skipifsilent
