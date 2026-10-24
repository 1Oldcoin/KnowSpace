#Requires -Version 5.1
<#
.SYNOPSIS
    End-to-end contract test for a plugin process (JSON-RPC over stdio).

.DESCRIPTION
    Starts a plugin executable exactly the way the core does (stdin/stdout pipes, no shell),
    then exercises the full protocol:
      initialize -> capabilities/list -> health -> invoke (agent tool) -> invoke (platform sync)
      -> unknown capability (structured error) -> shutdown -> clean exit.

    stderr is NOT redirected on purpose: plugin logs stay visible and the PowerShell runspace
    never blocks on a full pipe (see the OutputDataReceived trap documented in repo memory).

.PARAMETER PluginExe
    Plugin executable. Defaults to the sample platform plugin build output.

.PARAMETER TimeoutSeconds
    Per-response timeout. Default 15.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-PluginContract.ps1
#>
[CmdletBinding()]
param(
    [string]$PluginExe = 'samples\MyFirstMafAgent.Plugin.SamplePlatform\bin\Debug\net10.0\MyFirstMafAgent.Plugin.SamplePlatform.exe',
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PluginExe)) {
    throw "Plugin executable not found: $PluginExe (build samples first: dotnet build samples\MyFirstMafAgent.Plugin.SamplePlatform)"
}

$PluginExe = (Resolve-Path -LiteralPath $PluginExe).Path
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
    Add-Result ('assert-' + $script:assertionIndex) $condition $description
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $PluginExe
$startInfo.WorkingDirectory = Split-Path -Parent $PluginExe
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$startInfo.EnvironmentVariables['MYFIRSTMAFAGENT_PLUGIN_ID'] = 'com.example.sample-platform'
$startInfo.EnvironmentVariables['MYFIRSTMAFAGENT_PROTOCOL'] = '1.0.0'

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo

function Send-Rpc([string]$json) {
    $process.StandardInput.WriteLine($json)
    $process.StandardInput.Flush()
}

function Read-Rpc {
    param([string]$expectedId = '')

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $task = $process.StandardOutput.ReadLineAsync()
        if (-not $task.Wait([Math]::Max(100, [int](($deadline - (Get-Date)).TotalMilliseconds)))) {
            throw "timed out waiting for a response (expected id '$expectedId')"
        }

        $line = $task.Result
        if ($null -eq $line) { throw 'plugin closed stdout unexpectedly' }
        if ($line.Length -eq 0) { continue }

        $node = $line | ConvertFrom-Json
        if (-not $expectedId -or ([string]$node.id) -eq $expectedId) { return $node }
    }

    throw "timed out waiting for a response (expected id '$expectedId')"
}

try {
    if (-not $process.Start()) { throw 'failed to start the plugin process' }
    Write-Host ("Started plugin (pid {0})" -f $process.Id)

    # 1. initialize
    Send-Rpc '{"jsonrpc":"2.0","id":"1","method":"plugin/initialize","params":{"protocolVersion":"1.0.0","coreVersion":"10.0","dataDirectory":"C:\\Temp\\maf-plugin-data"}}'
    $init = Read-Rpc '1'
    Add-Result 'initialize' ($null -ne $init.result) ("pluginId=" + $init.result.pluginId + " v" + $init.result.pluginVersion)
    Assert-True ($init.result.pluginId -eq 'com.example.sample-platform') 'plugin id matches manifest'
    Assert-True ($init.result.pluginVersion -eq '1.0.0') 'plugin version matches manifest'
    Assert-True ($init.result.capabilities.Count -eq 2) 'two capabilities declared'

    # 2. capabilities/list
    Send-Rpc '{"jsonrpc":"2.0","id":"2","method":"plugin/capabilities/list"}'
    $list = Read-Rpc '2'
    $kinds = @($list.result.capabilities | ForEach-Object { $_.kind })
    Add-Result 'capabilities/list' ($kinds.Count -eq 2) ($kinds -join ',')
    Assert-True ($kinds -contains 'platform-connector') 'platform connector declared'
    Assert-True ($kinds -contains 'agent-tool') 'agent tool declared'

    # 3. health
    Send-Rpc '{"jsonrpc":"2.0","id":"3","method":"plugin/health"}'
    $health = Read-Rpc '3'
    Add-Result 'health' ($health.result.status -eq 'healthy') ('status=' + $health.result.status)

    # 4. agent tool invoke
    Send-Rpc '{"jsonrpc":"2.0","id":"4","method":"plugin/capabilities/invoke","params":{"capabilityId":"notes.search","arguments":{"query":"插件","maxResults":5}}}'
    $search = Read-Rpc '4'
    $searchItems = @($search.result.result.items)
    Add-Result 'invoke(agent-tool)' ($searchItems.Count -ge 1) ('matches=' + $searchItems.Count)
    Assert-True ($search.result.capabilityId -eq 'notes.search') 'response echoes capabilityId'

    # 5. platform sync invoke
    Send-Rpc '{"jsonrpc":"2.0","id":"5","method":"plugin/capabilities/invoke","params":{"capabilityId":"platform.notes.sync","arguments":{}}}'
    $sync = Read-Rpc '5'
    $syncItems = @($sync.result.result.items)
    Add-Result 'invoke(platform-connector)' ($syncItems.Count -eq 2) ('items=' + $syncItems.Count)
    Assert-True ($syncItems[0].contentHash.Length -eq 64) 'items carry a sha256 content hash'
    Assert-True ($syncItems[0].externalId.Length -gt 0) 'items carry an externalId'
    Assert-True ($sync.result.result.nextCursor -eq 'done') 'pagination cursor returned'

    # 6. unknown capability -> structured error
    Send-Rpc '{"jsonrpc":"2.0","id":"6","method":"plugin/capabilities/invoke","params":{"capabilityId":"notes.delete"}}'
    $unknown = Read-Rpc '6'
    Add-Result 'unknown-capability-error' ($unknown.error.code -eq -32005) ('code=' + $unknown.error.code)

    # 7. unknown method -> structured error
    Send-Rpc '{"jsonrpc":"2.0","id":"7","method":"plugin/nope"}'
    $unknownMethod = Read-Rpc '7'
    Add-Result 'unknown-method-error' ($unknownMethod.error.code -eq -32601) ('code=' + $unknownMethod.error.code)

    # 8. shutdown
    Send-Rpc '{"jsonrpc":"2.0","id":"8","method":"plugin/shutdown"}'
    $shutdown = Read-Rpc '8'
    Add-Result 'shutdown' ($shutdown.result.ok -eq $true) 'plugin acknowledged shutdown'

    if (-not $process.WaitForExit(15000)) {
        Add-Result 'clean-exit' $false 'process did not exit within 15s'
    }
    else {
        Add-Result 'clean-exit' ($process.ExitCode -eq 0) ('exitCode=' + $process.ExitCode)
    }
}
catch {
    Add-Result 'exception' $false $_.Exception.Message
}
finally {
    if (-not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
    $process.Dispose()
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'Plugin contract: all checks passed.' -ForegroundColor Green
    exit 0
}

Write-Host ('Plugin contract: {0} check(s) failed: {1}' -f $failures.Count, ($failures -join ', ')) -ForegroundColor Red
exit 1
