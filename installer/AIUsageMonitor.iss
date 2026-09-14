; Inno Setup script for AI Usage Monitor.
;
; Installs PER USER by default, so no UAC prompt and no admin rights are
; needed - the app only reads the current user's own AI tool logins and writes
; to HKCU, so a machine-wide install buys nothing. A user who wants one can
; still pick it in the wizard.
;
; Build with:  .\build_ai_installer.ps1

#define AppName        "AI Usage Monitor"
#define AppShortName   "AIUsageMonitor"
#define AppVersion     "1.2.6"
#define AppPublisher   "Apichart Chantanis"
#define AppExe         "AIUsageMonitor.exe"
#define SourceDir      "..\dist\AIUsageMonitor"

[Setup]
; Its own GUID, distinct from the older ClaudeUsageMonitor product, so the two
; never overwrite or uninstall each other.
AppId={{2F8A6D51-9C74-4B3E-A1D6-58E07C4B9A12}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\installer_out
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
; Shown as a page in the wizard before installing.
LicenseFile=..\LICENSE
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

; NOTE: there is deliberately no "start with Windows" task here. The app owns
; that Run entry itself - it is on by default and toggled in Settings - and two
; writers for one registry value is how you get an entry a user cannot remove.

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; PyInstaller puts bundled data under {app}\_internal, which is fine for the
; in-app Readme window but is not somewhere a user would ever look. Put a copy
; in the app root as well, so the Start Menu Readme shortcut below has a real
; file to open.
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

; GPL-3.0 requires the licence to travel with the program, and the Apache-2.0
; and BSD-2-Clause components require their notices reproduced in binary
; distributions. Both are bundled inside the exe as well, but a user looking
; for them will look in the install folder.
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; Opened through Notepad rather than by association: a stock Windows install
; has no handler registered for .md, so a shortcut straight to the file just
; raises the "How do you want to open this file?" picker. The formatted copy is
; the in-app Readme window (header button, or F1).
Name: "{group}\{#AppName} Readme"; Filename: "{sys}\notepad.exe"; \
    Parameters: """{app}\README.md"""; IconFilename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} Licence"; Filename: "{sys}\notepad.exe"; \
    Parameters: """{app}\LICENSE.txt"""; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app writes nothing beside its own files, but a stale Run entry pointing
; at a removed exe would be a dead startup item.
Type: files; Name: "{app}\*.log"

[Code]
{ CloseApplications only prompts in an INTERACTIVE run. A silent install or
  uninstall leaves a running copy holding its own files, which orphans the
  whole install directory - measured at 58 MB of leftovers. By this point the
  user has asked for the app to be installed or removed, so closing it is what
  they meant. }
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AIUsageMonitor.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

{ Remove the app's own start-with-Windows entry, so uninstalling does not leave
  Windows trying to launch a program that is gone. }
procedure RemoveStartupEntry();
begin
  RegDeleteValue(HKEY_CURRENT_USER,
                 'Software\Microsoft\Windows\CurrentVersion\Run',
                 'AIUsageMonitor');
end;

{ Settings live in HKCU\Software\AIUsageMonitor and include DPAPI-sealed API
  keys. Deleting them silently would be surprising, and keeping them silently
  would leave secrets behind - so ask, once, at the end. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Before any file is touched, make sure nothing is holding them open. }
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp();
    exit;
  end;

  if CurUninstallStep <> usPostUninstall then
    exit;

  RemoveStartupEntry();

  { A silent uninstall must never destroy stored credentials. Under
    /SUPPRESSMSGBOXES Inno answers Yes to a MB_YESNO box whatever
    MB_DEFBUTTON2 says, so the question cannot simply be defaulted - it has to
    be skipped, leaving the settings in place. }
  if UninstallSilent then
    exit;

  if not RegKeyExists(HKEY_CURRENT_USER, 'Software\AIUsageMonitor') then
    exit;

  if MsgBox('Also remove your saved settings?' + #13#10#13#10 +
            'This clears the theme, refresh interval, window positions, and ' +
            'any OpenAI or Gemini keys you stored.' + #13#10#13#10 +
            'Choose No to keep them for a future reinstall.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\AIUsageMonitor');
end;
