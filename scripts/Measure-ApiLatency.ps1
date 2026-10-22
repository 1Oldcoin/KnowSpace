#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 0 probe: sequential latency for one API endpoint (p50 / p95 / min / max).

.DESCRIPTION
    Runs warmup requests, then N measured requests, and prints a summary you can copy
    into docs/PHASE0-BASELINE.md. Latency only - it does not create concurrency.
    Run it once per endpoint you care about (health, list, search, chat).

.PARAMETER BaseUrl
    API base URL, for example http://127.0.0.1:5056

.PARAMETER Path
    Absolute path, for example /api/knowledge-bases/search

.PARAMETER Method
    GET or POST.

.PARAMETER BodyPath
    Optional JSON file sent as the request body (required for POST search/chat probes).

.PARAMETER Iterations
    Measured requests. Default 30.

.PARAMETER Warmup
    Unmeasured warmup requests. Default 3.

.PARAMETER TimeoutSeconds
    Per request timeout. Default 180.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-ApiLatency.ps1 -Path /api/health

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-ApiLatency.ps1 `
        -Path /api/knowledge-bases/search -Method POST -BodyPath .\search-body.json
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5056',
    [string]$Path = '/api/health',
    [ValidateSet('GET', 'POST')]
    [string]$Method = 'GET',
    [string]$BodyPath = '',
    [int]$Iterations = 30,
    [int]$Warmup = 3,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

if ($Iterations -lt 1) { throw '-Iterations must be >= 1' }
if ($Warmup -lt 0) { throw '-Warmup must be >= 0' }

$body = $null
if (-not [string]::IsNullOrWhiteSpace($BodyPath)) {
    if (-not (Test-Path -LiteralPath $BodyPath)) { throw "Body file not found: $BodyPath" }
    $body = Get-Content -LiteralPath $BodyPath -Raw
}

$uri = ($BaseUrl.TrimEnd('/') + '/' + $Path.TrimStart('/'))
$headers = @{ 'Accept' = 'application/json' }

function Invoke-Probe {
    param([switch]$Measure)

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $request = @{
            Uri         = $uri
            Method      = $Method
            Headers     = $headers
            TimeoutSec  = $TimeoutSeconds
            UseBasicParsing = $true
        }
        if ($null -ne $body) {
            $request['Body'] = $body
            $request['ContentType'] = 'application/json; charset=utf-8'
        }
        $response = Invoke-WebRequest @request
        $watch.Stop()
        if ($Measure) {
            return [pscustomobject]@{ DurationMs = [double]$watch.Elapsed.TotalMilliseconds; Status = [int]$response.StatusCode; Error = $null }
        }
    }
    catch {
        $watch.Stop()
        $status = 0
        if ($null -ne $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($Measure) {
            return [pscustomobject]@{ DurationMs = [double]$watch.Elapsed.TotalMilliseconds; Status = $status; Error = $_.Exception.Message }
        }
        throw
    }
}

Write-Host ("Target : {0} {1}" -f $Method, $uri)
Write-Host ("Warmup : {0} request(s)" -f $Warmup)

for ($i = 0; $i -lt $Warmup; $i++) {
    $null = Invoke-Probe
}

Write-Host ("Measure: {0} request(s)" -f $Iterations)

$samples = New-Object System.Collections.Generic.List[double]
$errors = 0
for ($i = 0; $i -lt $Iterations; $i++) {
    $result = Invoke-Probe -Measure
    $samples.Add($result.DurationMs)
    if ($result.Error) { $errors++ }
    Write-Progress -Activity 'Measuring latency' -Status ('{0}/{1}' -f ($i + 1), $Iterations) -PercentComplete ((($i + 1) / $Iterations) * 100)
}
Write-Progress -Activity 'Measuring latency' -Completed

$sorted = $samples | Sort-Object
function Get-Percentile([double[]]$values, [double]$percentile) {
    if ($values.Count -eq 0) { return 0 }
    $index = [Math]::Ceiling(($percentile / 100.0) * $values.Count) - 1
    if ($index -lt 0) { $index = 0 }
    if ($index -ge $values.Count) { $index = $values.Count - 1 }
    return $values[$index]
}

$mean = ($samples | Measure-Object -Average).Average
$summary = [ordered]@{
    endpoint  = ('{0} {1}' -f $Method, $uri)
    samples   = $samples.Count
    errors    = $errors
    min_ms    = [Math]::Round($sorted[0], 2)
    mean_ms   = [Math]::Round($mean, 2)
    p50_ms    = [Math]::Round((Get-Percentile $sorted 50), 2)
    p95_ms    = [Math]::Round((Get-Percentile $sorted 95), 2)
    max_ms    = [Math]::Round($sorted[$sorted.Count - 1], 2)
    timestamp = (Get-Date).ToString('o')
}

Write-Host ''
$summary | ForEach-Object { $_.GetEnumerator() | ForEach-Object { Write-Host ('{0,-10} {1}' -f $_.Key, $_.Value) } }
Write-Host ''
Write-Host 'Copy these numbers into docs/PHASE0-BASELINE.md (same machine, corpus and query set).'
