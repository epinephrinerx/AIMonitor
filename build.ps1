<#
    Builds dist\ClaudeUsageMonitor.exe - a single standalone executable that
    needs no Python installation on the target machine.

    Usage:  .\build.ps1
#>

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$python = ".\.venv\Scripts\python.exe"

if (-not (Test-Path $python)) {
    Write-Host "Creating virtual environment..." -ForegroundColor Cyan
    python -m venv .venv
    & $python -m pip install --upgrade pip --quiet
    & $python -m pip install -r requirements.txt
}

Write-Host "Generating application icon..." -ForegroundColor Cyan
& $python tools\make_icon.py

# PyInstaller's own --clean fails with "Access is denied" when a sync client
# (OneDrive) or a still-running copy of the app holds the build directory, so
# clear it here with retries instead.
Get-Process ClaudeUsageMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($dir in @("build", "dist")) {
    for ($attempt = 1; $attempt -le 5 -and (Test-Path $dir); $attempt++) {
        try {
            Remove-Item -Recurse -Force $dir -ErrorAction Stop
        } catch {
            Write-Host "  $dir is locked, retrying ($attempt/5)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
    if (Test-Path $dir) {
        Write-Error "Could not remove '$dir'. Close the app or pause OneDrive sync and retry."
    }
}

Write-Host "Building executable (this takes a minute)..." -ForegroundColor Cyan
& $python -m PyInstaller ClaudeUsageMonitor.spec --noconfirm

$exe = Join-Path $PSScriptRoot "dist\ClaudeUsageMonitor.exe"
if (Test-Path $exe) {
    $size = "{0:N1} MB" -f ((Get-Item $exe).Length / 1MB)
    Write-Host ""
    Write-Host "Built $exe ($size)" -ForegroundColor Green
    Write-Host "Copy that single file anywhere - it needs no Python."
} else {
    Write-Error "Build finished but $exe was not produced."
}
