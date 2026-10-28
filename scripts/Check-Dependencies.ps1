# Dependency gate: fails when a vulnerable NuGet package is found.
# ASCII only on purpose: PowerShell 5.1 misparses UTF-8 without BOM.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Check-Dependencies.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Check-Dependencies.ps1 -TopLevelOnly
#
# Exit codes: 0 = pass, 1 = vulnerable package found, 2 = tooling failure.
param(
    [string]$Project = 'MyFirstMafAgent.Api\MyFirstMafAgent.Api.csproj',
    [switch]$TopLevelOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
$temp = Join-Path $root '.dep-audit.json'
try {
    if (-not (Test-Path $Project)) {
        Write-Host "Project not found: $Project" -ForegroundColor Red
        exit 2
    }

    $dotnetArgs = @('list', $Project, 'package', '--format', 'json', '--vulnerable')
    if (-not $TopLevelOnly) { $dotnetArgs += '--include-transitive' }

    # `dotnet list package --vulnerable` exits 0 even when problems exist,
    # so the exit code is useless here; the report must be parsed.
    & dotnet @dotnetArgs 2>&1 | Out-File -FilePath $temp -Encoding utf8

    if (-not (Test-Path $temp)) {
        Write-Host 'dotnet produced no report.' -ForegroundColor Red
        exit 2
    }

    $report = Get-Content $temp -Raw | ConvertFrom-Json
    $hits = @()
    $total = 0

    foreach ($scannedProject in $report.projects) {
        foreach ($framework in $scannedProject.frameworks.PSObject.Properties) {
            $packages = @()
            if ($framework.Value.topLevelPackages) { $packages += $framework.Value.topLevelPackages }
            if ($framework.Value.transitivePackages) { $packages += $framework.Value.transitivePackages }

            foreach ($package in $packages) {
                $total++
                if ($package.vulnerabilities) {
                    foreach ($vulnerability in $package.vulnerabilities) {
                        $hits += [pscustomobject]@{
                            Package  = $package.id
                            Version  = $package.resolvedVersion
                            Severity = $vulnerability.severity
                            Advisory = $vulnerability.advisoryurl
                        }
                    }
                }
            }
        }
    }

    Write-Host ("Scanned {0} package reference(s)." -f $total)
    if ($total -eq 0) {
        # dotnet omits the framework/package tree entirely when nothing is vulnerable,
        # so an empty report means "clean", not "nothing was scanned".
        Write-Host 'Report is empty: dotnet omits the package tree when no advisory matches.' -ForegroundColor DarkGray
    }

    if ($hits.Count -gt 0) {
        Write-Host ("Found {0} vulnerabilit(ies):" -f $hits.Count) -ForegroundColor Yellow
        $hits | Sort-Object Severity, Package | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'Fix by pinning a patched version at the top level (see Api csproj for the precedent).' -ForegroundColor Yellow
        exit 1
    }

    Write-Host 'No known vulnerabilities.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ("Dependency scan failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 2
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
    Pop-Location
}
