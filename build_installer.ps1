<#
    Builds the Windows installer:  installer_out\ClaudeUsageMonitor-Setup-1.0.0.exe

    Produces a one-directory PyInstaller build (fast cold start, unlike the
    portable onefile) and wraps it with Inno Setup.

    Usage:  .\build_installer.ps1
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

# PyInstaller's --clean trips over a locked build dir (OneDrive, or a running
# copy of the app), so clear it here with retries instead.
Get-Process ClaudeUsageMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($dir in @("build", "dist\ClaudeUsageMonitor")) {
    for ($attempt = 1; $attempt -le 5 -and (Test-Path $dir); $attempt++) {
        try {
            Remove-Item -Recurse -Force $dir -ErrorAction Stop
        } catch {
            Write-Host "  $dir is locked, retrying ($attempt/5)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
}

Write-Host "Building application (one-directory)..." -ForegroundColor Cyan
& $python -m PyInstaller ClaudeUsageMonitor-dir.spec --noconfirm

$appDir = Join-Path $PSScriptRoot "dist\ClaudeUsageMonitor"
if (-not (Test-Path (Join-Path $appDir "ClaudeUsageMonitor.exe"))) {
    Write-Error "PyInstaller did not produce dist\ClaudeUsageMonitor\ClaudeUsageMonitor.exe"
}

Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc "installer\ClaudeUsageMonitor.iss"

$setup = Get-ChildItem "installer_out\*.exe" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    $size = "{0:N1} MB" -f ($setup.Length / 1MB)
    Write-Host ""
    Write-Host "Built $($setup.FullName) ($size)" -ForegroundColor Green
    Write-Host "Per-user install - no admin rights needed."
} else {
    Write-Error "Inno Setup finished but no installer was produced."
}
