#Requires -Version 5.1
<#
.SYNOPSIS
    Desktop contract smoke test: local API token gate, strict CSP and graceful shutdown.

.DESCRIPTION
    Starts the published API exactly the way the Electron shell does (random loopback port,
    token via environment variable, ready file), then asserts:
      1. /api/* without the token   -> 401
      2. /api/* with a wrong token  -> 401
      3. /api/health with the token -> 200
      4. index.html carries the strict Content-Security-Policy header
      5. POST /api/desktop/shutdown with the token -> the process exits on its own

    The token never appears in the ready file, on the command line, or in this script's output.

.PARAMETER ApiExe
    Published MyFirstMafAgent.Api.exe. Required.

.PARAMETER Environment
    ASPNETCORE_ENVIRONMENT for the child process. Default Development (loads user secrets,
    so no portable PostgreSQL is started).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-DesktopContract.ps1 `
        -ApiExe C:\Temp\mafapi\MyFirstMafAgent.Api.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiExe,
    [string]$Environment = 'Development',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ApiExe)) { throw "API executable not found: $ApiExe" }
$ApiExe = (Resolve-Path -LiteralPath $ApiExe).Path

$token = -join (1..32 | ForEach-Object { '{0:x2}' -f (Get-Random -Minimum 0 -Maximum 256) })
$readyFile = Join-Path ([System.IO.Path]::GetTempPath()) ('maf-ready-' + [Guid]::NewGuid().ToString('N') + '.json')
$failures = New-Object System.Collections.Generic.List[string]

function Add-Result([string]$name, [bool]$passed, [string]$detail) {
    if ($passed) {
        Write-Host ("PASS  {0}  {1}" -f $name, $detail) -ForegroundColor Green
    }
    else {
        Write-Host ("FAIL  {0}  {1}" -f $name, $detail) -ForegroundColor Red
        $failures.Add($name)
    }
}

$assertionIndex = 0
function Assert-True([bool]$condition, [string]$description) {
    $script:assertionIndex++
    Add-Result ("assert-" + $script:assertionIndex) $condition $description
}

function Invoke-Probe([string]$uri, [string]$method = 'GET', [string]$tokenValue = $null) {
    $headers = @{}
    if ($tokenValue) { $headers['X-Api-Token'] = $tokenValue }

    $request = @{
        Uri             = $uri
        Method          = $method
        Headers         = $headers
        UseBasicParsing = $true
        TimeoutSec      = 60
    }

    try {
        $response = Invoke-WebRequest @request
        return [pscustomobject]@{
            Status      = [int]$response.StatusCode
            Headers     = $response.Headers
            ContentType = $response.Headers['Content-Type']
        }
    }
    catch {
        $status = 0
        if ($null -ne $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        return [pscustomobject]@{ Status = $status; Headers = @{}; ContentType = $null }
    }
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $ApiExe
$startInfo.WorkingDirectory = Split-Path -Parent $ApiExe
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Arguments = '--urls http://127.0.0.1:0 --no-browser --desktop-ready-file "{0}"' -f $readyFile
$startInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = $Environment
$startInfo.EnvironmentVariables['DESKTOP_API_TOKEN'] = $token
$startInfo.EnvironmentVariables['DESKTOP_STRICT_CSP'] = '1'

$apiLog = New-Object System.Text.StringBuilder
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo

# 注意：不要用 add_OutputDataReceived 事件处理器。事件处理器需要在 PowerShell runspace
# 里执行，而主线程正在轮询 ready 文件，导致读取线程阻塞，子进程写满 stdout 管道后会卡在启动阶段。
# 使用 .NET 的 ReadToEndAsync 在后台线程持续排空管道。
$stdoutTask = $null
$stderrTask = $null

function Get-ApiLog {
    param($outTask, $errTask)
    $text = New-Object System.Text.StringBuilder
    foreach ($task in @($outTask, $errTask)) {
        if ($null -ne $task) {
            try {
                if ($task.IsCompleted) { [void]$text.AppendLine($task.GetAwaiter().GetResult()) }
            }
            catch { }
        }
    }
    return $text.ToString()
}

try {
    if (-not $process.Start()) { throw 'Failed to start the API process.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    Write-Host ("Started API (pid {0}); waiting for ready file..." -f $process.Id)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $baseUrl = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $readyFile) {
            try {
                $payload = Get-Content -LiteralPath $readyFile -Raw | ConvertFrom-Json
                if ($payload.url) { $baseUrl = [string]$payload.url; break }
            }
            catch { }
        }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }

    if (-not $baseUrl) {
        Add-Result 'ready-file' $false 'API never reported a listening URL.'
        Write-Host '--- API log ---'
        Write-Host (Get-ApiLog $stdoutTask $stderrTask)
        throw 'Startup failed.'
    }

    Add-Result 'ready-file' $true $baseUrl
    Assert-True ($payload.url -match '^http://127\.0\.0\.1:\d+$') 'ready-file uses loopback and a concrete port'
    Assert-True ([string]$payload.url -notmatch '65535') 'random port assigned by the OS'
    Assert-True ((Get-Content -LiteralPath $readyFile -Raw) -notmatch 'DESKTOP|token') 'ready file contains no token'

    $noToken = Invoke-Probe "$baseUrl/api/health"
    Add-Result 'api-without-token-401' ($noToken.Status -eq 401) ("status=" + $noToken.Status)

    $wrongToken = Invoke-Probe "$baseUrl/api/health" -tokenValue 'not-the-token'
    Add-Result 'api-with-wrong-token-401' ($wrongToken.Status -eq 401) ("status=" + $wrongToken.Status)

    $authorized = Invoke-Probe "$baseUrl/api/health" -tokenValue $token
    Add-Result 'api-with-token-200' ($authorized.Status -eq 200) ("status=" + $authorized.Status)

    $mcpUnauthorized = Invoke-Probe "$baseUrl/mcp"
    Add-Result 'mcp-without-token-401' ($mcpUnauthorized.Status -eq 401) ("status=" + $mcpUnauthorized.Status)

    $document = Invoke-Probe "$baseUrl/"
    $csp = $document.Headers['Content-Security-Policy']
    Add-Result 'static-html-reachable-without-token' ($document.Status -eq 200) ("status=" + $document.Status)
    Add-Result 'strict-csp-present' ([bool]$csp -and $csp -match "script-src 'self'" -and $csp -match "object-src 'none'") ([string]$csp)

    $shutdown = Invoke-Probe "$baseUrl/api/desktop/shutdown" -method 'POST' -tokenValue $token
    Add-Result 'shutdown-accepted' ($shutdown.Status -eq 202 -or $shutdown.Status -eq 200) ("status=" + $shutdown.Status)

    if (-not $process.WaitForExit(20000)) {
        Add-Result 'graceful-exit' $false 'process did not exit within 20s'
    }
    else {
        Add-Result 'graceful-exit' ($process.ExitCode -eq 0) ("exitCode=" + $process.ExitCode)
    }
}
catch {
    Add-Result 'exception' $false $_.Exception.Message
    Write-Host '--- API log ---'
    Write-Host (Get-ApiLog $stdoutTask $stderrTask)
}
finally {
    if (-not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
    Remove-Item -LiteralPath $readyFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ($readyFile + '.tmp') -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'Desktop contract: all checks passed.' -ForegroundColor Green
    exit 0
}

Write-Host ('Desktop contract: {0} check(s) failed: {1}' -f $failures.Count, ($failures -join ', ')) -ForegroundColor Red
exit 1
