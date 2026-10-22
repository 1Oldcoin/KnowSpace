#Requires -Version 5.1
<#
.SYNOPSIS
    Layer dependency check for the modular monolith.

.DESCRIPTION
    Fails when a lower layer reaches into a higher one:
      Domain      -> no SqlSugar, no ASP.NET Core, no Application/Infrastructure/Persistence/Endpoints
      Application -> no SqlSugar, no Infrastructure/Persistence/Endpoints
      Endpoints   -> no SqlSugar (data access must go through application services)

.PARAMETER Root
    Repository root. Defaults to the parent folder of this script.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Check-Architecture.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) { $scriptPath = $MyInvocation.MyCommand.Path }
    $Root = Split-Path -Parent (Split-Path -Parent $scriptPath)
}

$apiRoot = Join-Path $Root 'MyFirstMafAgent.Api'
if (-not (Test-Path -LiteralPath $apiRoot)) {
    throw "API project not found under $Root"
}

$rules = @(
    @{
        Folder    = 'Domain'
        Forbidden = @(
            'using SqlSugar',
            'Microsoft.AspNetCore',
            'MyFirstMafAgent.Api.Infrastructure',
            'MyFirstMafAgent.Api.Persistence',
            'MyFirstMafAgent.Api.Endpoints',
            'MyFirstMafAgent.Api.Application'
        )
    },
    @{
        Folder    = 'Application'
        Forbidden = @(
            'using SqlSugar',
            'MyFirstMafAgent.Api.Infrastructure',
            'MyFirstMafAgent.Api.Persistence',
            'MyFirstMafAgent.Api.Endpoints'
        )
    },
    @{
        Folder    = 'Endpoints'
        Forbidden = @('using SqlSugar')
    }
)

$violations = New-Object System.Collections.Generic.List[string]

foreach ($rule in $rules) {
    $folder = Join-Path $apiRoot $rule.Folder
    if (-not (Test-Path -LiteralPath $folder)) { continue }

    foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -Filter *.cs -File) {
        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            $lineNumber++
            foreach ($pattern in $rule.Forbidden) {
                if ($line.IndexOf($pattern, [StringComparison]::Ordinal) -ge 0) {
                    $relative = $file.FullName.Substring($apiRoot.Length + 1)
                    $violations.Add(('{0}:{1}: [{2}] {3}' -f $relative, $lineNumber, $rule.Folder, $line.Trim()))
                    break
                }
            }
        }
    }
}

if ($violations.Count -eq 0) {
    Write-Host 'Architecture check passed: no forbidden dependencies found.'
    exit 0
}

Write-Host ('Architecture check failed: {0} violation(s).' -f $violations.Count)
foreach ($violation in $violations) {
    Write-Host ('  ' + $violation)
}
exit 1
