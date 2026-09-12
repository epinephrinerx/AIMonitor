; Inno Setup script for Claude Usage Monitor.
;
; Installs PER USER by default, so no UAC prompt and no admin rights are
; needed - the app only ever reads the current user's own Claude Code
; credentials and writes to HKCU, so a machine-wide install buys nothing.
; A user who wants one can still pick it in the wizard.
;
; Build with:  .\build_installer.ps1

#define AppName        "Claude Usage Monitor"
#define AppShortName   "ClaudeUsageMonitor"
#define AppVersion     "1.0.0"
#define AppPublisher   "Apichart Chantanis"
#define AppExe         "ClaudeUsageMonitor.exe"
#define SourceDir      "..\dist\ClaudeUsageMonitor"

[Setup]
; Stable identity: keeps upgrades replacing the same install rather than
; stacking up multiple entries in Apps & features.
AppId={{7E3C1B94-2A5D-4C8E-9F41-6B0D8A2E5C73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; "lowest" installs into %LocalAppData%\Programs with no UAC prompt;
; the dialog lets a user escalate to a machine-wide install if they want one.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\installer_out
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Offer to close a running copy rather than failing on a locked file.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Run at sign-in. HKCU even for a machine-wide install: the app is per-user by
; nature, so starting it for every account on the PC would be wrong.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppShortName}"; \
    ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[Code]
{ Settings live in HKCU\Software\ClaudeUsageMonitor and include DPAPI-sealed
  API keys. Deleting them silently would be surprising, and keeping them
  silently would leave secrets behind - so ask, once, at the end. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    exit;

  { A silent uninstall must never destroy stored credentials. Under
    /SUPPRESSMSGBOXES Inno answers Yes to a MB_YESNO box whatever
    MB_DEFBUTTON2 says, so the question cannot simply be defaulted - it has to
    be skipped, leaving the settings in place. Anyone scripting a silent
    removal can delete the key themselves. }
  if UninstallSilent then
    exit;

  if not RegKeyExists(HKEY_CURRENT_USER, 'Software\ClaudeUsageMonitor') then
    exit;

  if MsgBox('Also remove your saved settings?' + #13#10#13#10 +
            'This clears the theme, refresh interval, window positions, and ' +
            'any OpenAI or Gemini keys you stored.' + #13#10#13#10 +
            'Choose No to keep them for a future reinstall.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER,
                                 'Software\ClaudeUsageMonitor');
end;
