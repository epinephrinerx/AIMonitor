<#
    Builds both deliverables for AI Usage Monitor:

      dist\AIUsageMonitor.exe                      portable single file
      installer_out\AIUsageMonitor-Setup-1.1.0.exe per-user installer

    The installer wraps a one-directory build, which cold-starts far faster
    than the portable onefile (that one unpacks its whole payload into %TEMP%
    on every launch).

    Usage:  .\build_ai_installer.ps1
#>

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$python = ".\.venv\Scripts\python.exe"
if (-not (Test-Path $python)) {
    Write-Error "No virtual environment. Run .\build.ps1 first to create it."
}

# winget installs Inno Setup per-user by default, so %LocalAppData%\Programs is
# just as likely as Program Files. Check both, then fall back to whatever the
# uninstall entry recorded.
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
foreach ($hive in @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
    "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
    "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")) {
    Get-ChildItem $hive -ErrorAction SilentlyContinue | ForEach-Object {
        $entry = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($entry.DisplayName -like "*Inno Setup*" -and $entry.InstallLocation) {
            $isccCandidates += (Join-Path $entry.InstallLocation "ISCC.exe")
        }
    }
}
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Error "Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup"
}
Write-Host "Using $iscc" -ForegroundColor DarkGray

Write-Host "Generating application icon..." -ForegroundColor Cyan
& $python tools\make_icon.py

# PyInstaller's --clean trips over a locked build directory (OneDrive, or a
# running copy of the app), so clear it here with retries instead.
Get-Process AIUsageMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
foreach ($dir in @("build", "dist\AIUsageMonitor", "dist\AIUsageMonitor.exe")) {
    for ($attempt = 1; $attempt -le 5 -and (Test-Path $dir); $attempt++) {
        try {
            Remove-Item -Recurse -Force $dir -ErrorAction Stop
        } catch {
            Write-Host "  $dir is locked, retrying ($attempt/5)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
}

Write-Host "Building portable single file..." -ForegroundColor Cyan
& $python -m PyInstaller AIUsageMonitor.spec --noconfirm

Write-Host "Building one-directory tree for the installer..." -ForegroundColor Cyan
& $python -m PyInstaller AIUsageMonitor-dir.spec --noconfirm

$appDir = Join-Path $PSScriptRoot "dist\AIUsageMonitor"
if (-not (Test-Path (Join-Path $appDir "AIUsageMonitor.exe"))) {
    Write-Error "PyInstaller did not produce dist\AIUsageMonitor\AIUsageMonitor.exe"
}

Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc "installer\AIUsageMonitor.iss"

Write-Host ""
$portable = Get-Item "dist\AIUsageMonitor.exe" -ErrorAction SilentlyContinue
if ($portable) {
    Write-Host ("Portable  {0} ({1:N1} MB)" -f $portable.FullName, ($portable.Length / 1MB)) -ForegroundColor Green
}
$setup = Get-ChildItem "installer_out\AIUsageMonitor-Setup-*.exe" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    Write-Host ("Installer {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
    Write-Host "Per-user install - no admin rights needed."
} else {
    Write-Error "Inno Setup finished but no installer was produced."
}
