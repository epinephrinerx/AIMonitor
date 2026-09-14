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
    # Was a pointer to build.ps1, which existed mainly to bootstrap this and
    # otherwise built the retired ClaudeUsageMonitor. One script, one job.
    Write-Host "Creating virtual environment..." -ForegroundColor Cyan
    python -m venv .venv
    if (-not (Test-Path $python)) {
        Write-Error "python -m venv did not produce $python. Is Python on PATH?"
    }
    & $python -m pip install --upgrade pip --quiet
    & $python -m pip install -r requirements.txt
    if ($LASTEXITCODE -ne 0) {
        Write-Error "pip install -r requirements.txt failed with exit code $LASTEXITCODE"
    }
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
# The version Inno will stamp on the output, read from the script itself so
# this check cannot drift from it.
$issText = Get-Content "installer\AIUsageMonitor.iss" -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') {
    Write-Error "Could not read AppVersion from the .iss"
}
$appVersion = $Matches[1]
$expected = "installer_out\AIUsageMonitor-Setup-$appVersion.exe"
if (Test-Path $expected) { Remove-Item -Force $expected }

& $iscc "installer\AIUsageMonitor.iss"
# ISCC is a native exe, so a failure does not trip $ErrorActionPreference.
# Without this the script sailed past a real failure - "Resource update
# error: EndUpdateResource failed" while OneDrive held the output folder -
# and then reported the PREVIOUS version's installer as if it were new,
# because the summary below just globs for the newest matching file.
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup failed with exit code $LASTEXITCODE. If this is 'EndUpdateResource failed', the output folder was locked - OneDrive sync or antivirus - and a retry usually succeeds."
}
if (-not (Test-Path $expected)) {
    Write-Error "Inno Setup reported success but $expected was not produced."
}

Write-Host ""
$portable = Get-Item "dist\AIUsageMonitor.exe" -ErrorAction SilentlyContinue
if ($portable) {
    Write-Host ("Portable  {0} ({1:N1} MB)" -f $portable.FullName, ($portable.Length / 1MB)) -ForegroundColor Green
}
$setup = Get-Item $expected -ErrorAction SilentlyContinue
if ($setup) {
    Write-Host ("Installer {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
    Write-Host "Per-user install - no admin rights needed."
} else {
    Write-Error "Inno Setup finished but no installer was produced."
}
