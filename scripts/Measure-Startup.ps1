#Requires -Version 5.1
<#
.SYNOPSIS
    Measure cold-start latency of the published API sidecar.

.DESCRIPTION
    Starts MyFirstMafAgent.Api.exe exactly the way the Electron shell does
    (random-ish loopback port, --desktop-ready-file, --no-browser), then records:

      t1 = process start -> ready file appears   (DB + schema + plugins + Kestrel done)
      t2 = process start -> /api/health returns 200

    Prints a small table so a change can be compared before/after. Run it twice:
    the first run pays for a cold page cache / antivirus scan, the second one is
    the number users actually feel.

.PARAMETER ApiExe
    Path to MyFirstMafAgent.Api.exe. Defaults to publish\desktop (then bin\Debug).

.PARAMETER Runs
    How many times to measure. Default 3.

.PARAMETER Port
    Loopback port to bind. Default 5099.
#>
[CmdletBinding()]
param(
    [string]$ApiExe,
    [int]$Runs = 3,
    [int]$Port = 5099
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $ApiExe) {
    $candidates = @(
        (Join-Path $root 'publish\desktop\MyFirstMafAgent.Api.exe'),
        (Join-Path $root 'MyFirstMafAgent.Api\bin\Release\net10.0\MyFirstMafAgent.Api.exe'),
        (Join-Path $root 'MyFirstMafAgent.Api\bin\Debug\net10.0\MyFirstMafAgent.Api.exe')
    )
    $ApiExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $ApiExe -or -not (Test-Path $ApiExe)) {
    throw "MyFirstMafAgent.Api.exe not found. Run scripts\Publish-Portable.ps1 -Target desktop first."
}

Write-Host "API: $ApiExe"
Write-Host ""

$results = @()
$scratch = Join-Path $env:TEMP 'maf-startup'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

for ($i = 1; $i -le $Runs; $i++) {
    $ready = Join-Path $scratch ("ready-{0}.json" -f $i)
    $stdout = Join-Path $scratch ("out-{0}.txt" -f $i)
    $stderr = Join-Path $scratch ("err-{0}.txt" -f $i)
    Remove-Item $ready, $stdout, $stderr -ErrorAction SilentlyContinue

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $ApiExe `
        -ArgumentList @('--urls', "http://127.0.0.1:$Port", '--no-browser', '--desktop-ready-file', $ready) `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr

    $readySeconds = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $ready) { $readySeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2); break }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 25
    }

    $healthSeconds = $null
    if ($readySeconds) {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $response = Invoke-WebRequest "http://127.0.0.1:$Port/api/health" -TimeoutSec 2 -UseBasicParsing
                if ($response.StatusCode -eq 200) {
                    $healthSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
                    break
                }
            } catch { }
            Start-Sleep -Milliseconds 25
        }
    }

    # Stop the instance we started (it owns its own portable PostgreSQL).
    try { Invoke-WebRequest "http://127.0.0.1:$Port/api/desktop/shutdown" -Method Post -TimeoutSec 5 -UseBasicParsing | Out-Null } catch { }
    if (-not $proc.HasExited) { try { $proc.WaitForExit(15000) | Out-Null } catch { } }
    if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }

    $phases = @()
    if (Test-Path $stdout) {
        $phases = Get-Content $stdout -Encoding UTF8 | Where-Object { $_ -like '*[startup]*' }
    }

    $results += [pscustomobject]@{
        Run    = $i
        Ready  = $readySeconds
        Health = $healthSeconds
    }

    Write-Host ("run {0}: ready {1}s, health {2}s" -f $i, $readySeconds, $healthSeconds)
    foreach ($phase in $phases) { Write-Host "    $phase" }
}

Write-Host ""
$results | Format-Table -AutoSize
