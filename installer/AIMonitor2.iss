; Inno Setup script for AI Usage Monitor 2.0 (C# / .NET 10 edition).
;
; Design choices (see Docs/ARCHITECTURE.md §8, Docs/FEATURE_PARITY.md PAR-035):
;
; * Per-user install by default — no UAC prompt, no admin rights needed.
;   The app reads the current user's own AI-tool logins and writes to
;   HKCU / %LOCALAPPDATA%, so a machine-wide install buys nothing.
;   A power user who wants a machine-wide install can override in the wizard.
;
; * Preserve Python 1.3.3 AppId and directory for in-place upgrade (ADR-0003).
;   Never remove legacy settings or credentials.
;
; * Startup-registry entry is owned by the app, not the installer (PAR-024).
;   Toggling "Start with Windows" in Settings writes / removes the Run entry.
;   No installer Task here — two writers for one value causes user confusion.
;
; * Build this script AFTER running the publish step:
;       dotnet publish src\AIMonitor.Presentation.Wpf\AIMonitor.Presentation.Wpf.csproj ^
;              /p:PublishProfile=win-x64-self-contained ^
;              -o publish\win-x64
;   Then compile the installer:
;       iscc installer\AIMonitor2.iss
;
; * The resulting installer lands in installer_out\AIUsageMonitor2-Setup-{version}.exe
;
; VersionPrefix is passed by build_installer.ps1 from Directory.Build.props.
#ifndef AppVersion
  #error Build using build_installer.ps1 (AppVersion must come from Directory.Build.props)
#endif

; === Fixed constants — do not change without understanding the impact ===
#define AppName        "AI Usage Monitor"
#define AppShortName   "AIUsageMonitor"
#define AppPublisher   "Apichart Chantanis"
#define AppExe         "AIUsageMonitor.exe"
; Source is the dotnet publish output directory.
#define SourceDir      "..\publish\win-x64"

[Setup]
; Same product as Python 1.3.3; Inno reuses the existing directory/uninstall identity.
AppId={{2F8A6D51-9C74-4B3E-A1D6-58E07C4B9A12}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user by default; admin override available in wizard (PAR-035 compat)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Windows 10 1809+ required (.NET 10 / WPF prerequisite)
MinVersion=10.0.17763

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Default paths
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Output
OutputDir=..\installer_out
OutputBaseFilename=AIUsageMonitor Setup {#AppVersion}
SetupIconFile=..\assets\icon.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Offer to close a running copy rather than failing on locked files.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

; NOTE: No "start with Windows" task. The app manages its own Run entry in
; Settings (PAR-024). Having two writers for one registry value is how you
; produce a startup entry the user cannot cleanly remove.

[Files]
; Main executable (single-file self-contained publish output)
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

; Legal documents that must ship with a GPL-3.0 binary (PAR-034)
Source: "..\README.md";               DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";                  DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md";  DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start Menu
Name: "{group}\{#AppName}";          Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} Readme";   Filename: "{sys}\notepad.exe"; \
    Parameters: """{app}\README.md"""; IconFilename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} Licence";  Filename: "{sys}\notepad.exe"; \
    Parameters: """{app}\LICENSE.txt"""; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
; Desktop (optional — user-selected Task)
Name: "{autodesktop}\{#AppName}";    Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; \
    Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove log files that the app may write beside the executable.
Type: files; Name: "{app}\*.log"
Type: files; Name: "{app}\preserve-startup-intent"

[Code]
var
  StartupWasEnabled: Boolean;
  StartupCaptured: Boolean;
  ExistingInstallation: Boolean;

{ -----------------------------------------------------------------------
  Force-close a running copy before touching its files.
  CloseApplications only prompts interactively; a /SILENT run would leave
  a locked exe, making the whole install directory unremovable.
  ----------------------------------------------------------------------- }
procedure StopRunningApp();
var
  Locator, Services, Processes, Process: Variant;
  I: Integer;
begin
  { Only the executable in this installation, never another same-named app. }
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT * FROM Win32_Process WHERE Name = ''{#AppExe}''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then
        if CompareText(Process.ExecutablePath, ExpandConstant('{app}\{#AppExe}')) = 0 then
          Process.Terminate();
    end;
  except
    Log('Could not stop the installed executable; Restart Manager handles install file locks.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Capture once before closing the old process; retain intent across retries. }
  if not StartupCaptured then
  begin
    StartupWasEnabled := RegValueExists(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'AIUsageMonitor');
    ExistingInstallation :=
      RegKeyExists(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2F8A6D51-9C74-4B3E-A1D6-58E07C4B9A12}_is1') or
      RegKeyExists(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2F8A6D51-9C74-4B3E-A1D6-58E07C4B9A12}_is1') or
      RegKeyExists(HKCU32, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2F8A6D51-9C74-4B3E-A1D6-58E07C4B9A12}_is1') or
      RegKeyExists(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2F8A6D51-9C74-4B3E-A1D6-58E07C4B9A12}_is1');
    StartupCaptured := True;
  end;
  StopRunningApp();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and ExistingInstallation then
    if not SaveStringToFile(ExpandConstant('{app}\preserve-startup-intent'), 'Preserve Windows startup intent after upgrade.', False) then
      RaiseException('Could not record startup intent for this upgrade.');
  if (CurStep = ssPostInstall) and StartupCaptured and StartupWasEnabled then
    if not RegWriteStringValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'AIUsageMonitor',
      '"' + ExpandConstant('{app}\{#AppExe}') + '"') then
      RaiseException('Could not preserve the existing Start with Windows entry.');
  { An absent entry stays absent. No writes to the legacy settings tree. }
end;

{ Remove the app's own startup entry so an uninstall does not leave
  Windows trying to launch a program that no longer exists (PAR-024). }
procedure RemoveStartupEntry();
begin
  RegDeleteValue(HKEY_CURRENT_USER,
                 'Software\Microsoft\Windows\CurrentVersion\Run',
                 'AIUsageMonitor');
end;

{ Settings live in %LOCALAPPDATA%\AIUsageMonitor\ and include DPAPI-sealed
  secrets.  Silently deleting them would destroy credentials; silently
  keeping them leaves secrets in an orphaned folder.  Ask, once, at the end.
  Under /SUPPRESSMSGBOXES we skip the question and keep the data — the
  user has not explicitly consented to destroy credentials in a silent run. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp();
    exit;
  end;

  if CurUninstallStep <> usPostUninstall then
    exit;

  RemoveStartupEntry();

  if UninstallSilent then
    exit;

  DataDir := ExpandConstant('{localappdata}\AIUsageMonitor');
  if not DirExists(DataDir) then
    exit;

  if MsgBox(
    'Also remove your saved settings and stored API keys?' + #13#10#13#10 +
    'This deletes theme, refresh interval, window positions and any ' +
    'OpenAI or Gemini keys you stored in ' + DataDir + '.' + #13#10#13#10 +
    'Choose No to keep them for a future reinstall.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;
