<#
.SYNOPSIS
    Build, publish and optionally package AIMonitor 2.0.

.DESCRIPTION
    Full pipeline:
      1. Restore & build (Debug, to verify nothing is broken)
      2. Run unit tests
      3. Publish win-x64 self-contained single-file release build
      4. Optionally compile the Inno Setup installer (requires iscc.exe on PATH
         or installed at the default location)

.PARAMETER SkipTests
    Skip step 2 (unit tests). Not recommended for release builds.

.PARAMETER SkipInstaller
    Skip step 4 (Inno Setup compilation). Use when iscc is not installed.

.EXAMPLE
    .\build_installer.ps1
    .\build_installer.ps1 -SkipTests
    .\build_installer.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$IsccPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root    = $PSScriptRoot
$Sln     = Join-Path $Root 'AIMonitor.sln'
$WpfProj = Join-Path $Root 'src\AIMonitor.Presentation.Wpf\AIMonitor.Presentation.Wpf.csproj'
$PubOut  = Join-Path $Root 'publish\win-x64'
$IssFile = Join-Path $Root 'installer\AIMonitor2.iss'
$buildProperties = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'Directory.Build.props'))
$Version = $buildProperties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'VersionPrefix must be a three-part numeric version.' }

function Step([string]$label) { Write-Host "`n=== $label ===" -ForegroundColor Cyan }

# ── 1. Restore & build ────────────────────────────────────────────────────────
Step "Restore & build (Debug)"
dotnet build $Sln -c Debug --no-incremental -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# ── 2. Tests ──────────────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Step "Unit tests"
    dotnet test $Sln --no-build -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

# ── 3. Publish release ────────────────────────────────────────────────────────
Step "Publish win-x64 self-contained"
# Refuse to clean anything outside the workspace, including junction/symlink targets.
$resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
$resolvedOutput = [IO.Path]::GetFullPath($PubOut)
if (-not $resolvedOutput.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish output is outside the repository.'
}
foreach ($candidate in @((Join-Path $Root 'publish'), $PubOut)) {
    if ((Test-Path -LiteralPath $candidate) -and
        ((Get-Item -LiteralPath $candidate).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Publish output must not be a junction or symbolic link.'
    }
}
if (Test-Path -LiteralPath $PubOut) { Remove-Item -LiteralPath $PubOut -Recurse -Force }
dotnet publish $WpfProj `
    /p:PublishProfile=win-x64-self-contained `
    -o $PubOut `
    -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$ExePath = Join-Path $PubOut 'AIUsageMonitor.exe'
if (-not (Test-Path $ExePath)) { throw "Published exe not found at $ExePath" }
$sizeMB = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
Write-Host "  Published: $ExePath ($sizeMB MB)" -ForegroundColor Green
Copy-Item -LiteralPath $ExePath -Destination (Join-Path $PubOut "AIUsageMonitor $Version.exe")
foreach ($document in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $Root $document) -Destination $PubOut
}

# ── 4. Installer ──────────────────────────────────────────────────────────────
if (-not $SkipInstaller) {
    Step "Inno Setup — compile installer"

    # Look for iscc in common locations
    $iscc = if ($IsccPath) { Get-Command $IsccPath -ErrorAction Stop } else { Get-Command iscc -ErrorAction SilentlyContinue }
    if (-not $iscc) {
        $iscc = Get-Command 'C:\Program Files (x86)\Inno Setup 6\iscc.exe' -ErrorAction SilentlyContinue
    }

    if (-not $iscc) {
        throw 'iscc.exe not found. Supply -IsccPath or explicitly use -SkipInstaller.'
    }
    else {
        & $iscc "/DAppVersion=$Version" $IssFile
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

        $InstallerOut = Join-Path $Root 'installer_out'
        $Setup = Get-ChildItem $InstallerOut -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($Setup) {
            $setupMB = [math]::Round($Setup.Length / 1MB, 1)
            Write-Host "  Installer: $($Setup.FullName) ($setupMB MB)" -ForegroundColor Green
        }
    }
}

Step "Done"
Write-Host "Publish output : $PubOut"
Write-Host "Run smoke test : $ExePath"
