[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$toolRoot = Join-Path $repository 'artifacts/tools/inno'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$downloadPath = Join-Path $toolRoot 'innosetup-6.7.3.exe'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $downloadPath
$signature = Get-AuthenticodeSignature -LiteralPath $downloadPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
    throw 'Inno Setup publisher signature did not validate.'
}
$compilerRoot = Join-Path $toolRoot 'compiler'
$installerArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/PORTABLE=1', '/NOICONS', '/TASKS=', ('/DIR="' + $compilerRoot + '"'))
$process = Start-Process -FilePath $downloadPath -ArgumentList $installerArgs -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Portable Inno Setup extraction failed: $($process.ExitCode)" }
if (-not (Test-Path -LiteralPath (Join-Path $compilerRoot 'ISCC.exe'))) { throw 'Inno Setup compiler missing.' }
Write-Host "Portable compiler: $compilerRoot/ISCC.exe"
