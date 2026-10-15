[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$solutionPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'AIMonitor.sln'
$json = dotnet list $solutionPath package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability audit failed to run.' }
$audit = ($json -join "`n") | ConvertFrom-Json -Depth 100
if ($audit.PSObject.Properties.Name -contains 'problems' -and $audit.problems.Count -gt 0) {
    throw 'NuGet vulnerability audit reported problems; inspect the feed/network configuration.'
}
foreach ($project in $audit.projects) {
    if ($project.PSObject.Properties.Name -notcontains 'frameworks') { continue }
    foreach ($framework in $project.frameworks) {
        foreach ($group in @('topLevelPackages', 'transitivePackages')) {
            if ($framework.PSObject.Properties.Name -notcontains $group) { continue }
            foreach ($package in $framework.$group) {
                if ($package.PSObject.Properties.Name -contains 'vulnerabilities' -and $package.vulnerabilities.Count -gt 0) {
                    throw "Vulnerable package: $($package.id) $($package.resolvedVersion)."
                }
            }
        }
    }
}
Write-Host 'NuGet vulnerability audit: no vulnerable packages reported.'
