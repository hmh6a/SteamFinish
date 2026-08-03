; Inno Setup script for SteamFinish.
;
; Built only in CI — see .github/workflows/release.yml, which passes the version:
;     ISCC /DAppVersion=1.2.0 /O"artifacts" installer\SteamFinish.iss
;
; The wizard shows its normal pages, so the user picks the install folder and
; ticks whether they want a Desktop shortcut.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "SteamFinish"
#define AppPublisher "Hussam Haider"
#define AppExe "SteamFinish.exe"
#define AppUrl "https://github.com/hmh6a/SteamFinish"

[Setup]
; Fixed so that installing a newer build upgrades in place instead of piling up
; a second entry in Apps & features.
AppId={{9C4F1B2E-4E7A-4E63-9E0E-1D2F5A7C8B31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user by default so no administrator prompt is needed; the wizard still
; offers "for all users", which is when it switches to Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\src\SteamFinish\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; A running copy holds its own exe open, so offer to close it rather than failing.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start {#AppName} when Windows starts"; GroupDescription: "Other:"; Flags: unchecked

[Files]
; The single self-contained executable produced by dotnet publish.
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Matches what the app writes itself for "Start with Windows", so the two agree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "SteamFinish"; ValueData: """{app}\{#AppExe}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings and logs are deliberately left behind so reinstalling keeps them.
Type: dirifempty; Name: "{app}"
