; Inno Setup script for AI Usage Monitor 2.0 (C# / .NET 10 edition).
;
; Design choices (see Docs/ARCHITECTURE.md §8, Docs/FEATURE_PARITY.md PAR-035):
;
; * Per-user install by default — no UAC prompt, no admin rights needed.
;   The app reads the current user's own AI-tool logins and writes to
;   HKCU / %LOCALAPPDATA%, so a machine-wide install buys nothing.
;   A power user who wants a machine-wide install can override in the wizard.
;
; * New AppId (distinct from Python 1.x GUID) — side-by-side capable.
;   Running `AIMonitor2.iss` will NOT uninstall the legacy Python edition.
;   If the user wants to retire the old copy they do so manually.
;   Choosing a new GUID means the two products never accidentally upgrade
;   each other.
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
; === Version — keep in sync with Directory.Build.props VersionPrefix ===
#define AppVersion     "2.0.0"

; === Fixed constants — do not change without understanding the impact ===
#define AppName        "AI Usage Monitor"
#define AppShortName   "AIUsageMonitor2"
#define AppPublisher   "Apichart Chantanis"
#define AppExe         "AIUsageMonitor.exe"
; Source is the dotnet publish output directory.
#define SourceDir      "..\publish\win-x64"

[Setup]
; New GUID — side-by-side with Python 1.x edition (2F8A6D51...).
; Do NOT reuse the legacy GUID or 2.x users will silently overwrite 1.x.
AppId={{B1C4F2E8-3DA7-4F9B-82CE-7A60E3D1F554}
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
CloseApplications=yes
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

[Code]
{ -----------------------------------------------------------------------
  Force-close a running copy before touching its files.
  CloseApplications only prompts interactively; a /SILENT run would leave
  a locked exe, making the whole install directory unremovable.
  ----------------------------------------------------------------------- }
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + '{#AppExe}',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
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
