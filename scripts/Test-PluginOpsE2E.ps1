# Phase 8 end-to-end: plugin drain upgrade, scheduled sync and trusted publisher keys.
#
# What unit tests cannot prove and this script does, against a real database and a real
# plugin child process:
#   1. a two-phase upgrade keeps a long-running invocation alive (no broken pipe),
#   2. draining rejects new invocations until the upgraded process is up,
#   3. the feeder turns "plugin asked for nextSyncAfterSeconds" into a queued job,
#   4. trusted public keys round-trip through the database and are honoured at once.
param(
    [string]$ApiDir = 'C:\Temp\mafapi8',
    [string]$Base = 'http://127.0.0.1:5056',
    [int]$ScheduleWaitSeconds = 90,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:failures = 0
$script:api = $null
$script:kb1 = $null
$script:kb2 = $null
$script:pluginId = 'com.example.sample-platform'
$work = Join-Path $env:TEMP ('plugin-ops-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

# Public key material used by the trusted-key checks. Generated offline with .NET;
# embedded so the script needs no crypto tooling on the machine running it.
$ecPublicPem = @'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGZzc4WDrj3fe2q/a6oSVWj9j+ACx
XQba8TtM0qif0qvv8U9qq+cEUee/LgbDz2EuJg6NaiRiTNMW+apPRWhIkA==
-----END PUBLIC KEY-----
'@

$ecPrivatePem = @'
-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQglzIi8vA4bgDKz/gA
EqxHIgWXXtyk3gNZSKJpgPF8nPWhRANCAAQZnNzhYOuPd97ar9rqhJVaP2P4ALFd
BtrxO0zSqJ/Sq+/xT2qr5wRR578uBsPPYS4mDo1qJGJM0xb5qk9FaEiQ
-----END PRIVATE KEY-----
'@

$rsaPublicPem = @'
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0OgpARU96eaBtD7KlZyl
QqbRQE1qHk8luavDcZAEYvApGr094KBu6bO3scL2wfp7Tn3ttAK+eLnKUmDu8JdM
4LOm/CgPqbURMi0D4GM6JT1LuuaY8TpWALQ9poXWs4zsR3kM8L7whjLA5EPenAaF
GjI5QxFvJSRVVZiKddSg+e4pzzyQ30b8XJbh88NVE+o05/JaD/S6QfkHm3XwxY7V
QSY4S0BjX11K120JTjkeGgN2j1JZN72GdY32VproKY+N3z4W2aCMc9r4+ZGkmreK
iSH2gy0sw4d0j7rjIUxJqve6EAxXe9aITZiDHII2vliZoqHWlWJWZZkOlUEjYfo2
KQIDAQAB
-----END PUBLIC KEY-----
'@

Add-Type -AssemblyName System.Net.Http

function New-JsonClient([int]$timeoutSeconds) {
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
    return $client
}

$http = New-JsonClient 300
$slowHttp = New-JsonClient 120

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0,-36} {1}" -f $name, $detail) -ForegroundColor Green }
    else { Write-Host ("FAIL  {0,-36} {1}" -f $name, $detail) -ForegroundColor Red; $script:failures++ }
}

function Invoke-Json([string]$method, [string]$url, $body) {
    $params = @{ Uri = $url; Method = $method; UseBasicParsing = $true; TimeoutSec = 300 }
    if ($null -ne $body) {
        $params.ContentType = 'application/json; charset=utf-8'
        $params.Body = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 10 -Compress))
    }
    try {
        $r = Invoke-WebRequest @params
        return @{ Status = [int]$r.StatusCode; Content = $r.Content }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return @{ Status = [int]$resp.StatusCode; Content = $sr.ReadToEnd() }
    }
}

function Get-Plugin([string]$id) {
    $raw = (Invoke-Json 'GET' "$Base/api/plugins/$id" $null).Content | ConvertFrom-Json

    # GET /{id} nests the snapshot under `status` and keeps a few live fields at the top level.
    # Flatten it so the checks can just read `.state` / `.version` / `.trustedKey`.
    if ($null -eq $raw) { return $null }
    if ($null -eq $raw.status) { return $raw }

    $flat = [ordered]@{}
    foreach ($property in $raw.status.PSObject.Properties) { $flat[$property.Name] = $property.Value }
    foreach ($property in $raw.PSObject.Properties) {
        if ($property.Name -ne 'status') { $flat[$property.Name] = $property.Value }
    }
    return [pscustomobject]$flat
}

function Wait-PluginState([string]$id, [string]$state, [int]$seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        $detail = Get-Plugin $id
        if ($detail.state -eq $state) { return $detail }
        Start-Sleep -Milliseconds 500
    }
    return $detail
}

function Install-Package([string]$zipPath) {
    $bytes = [System.IO.File]::ReadAllBytes($zipPath)
    $content = New-Object System.Net.Http.MultipartFormDataContent
    $file = New-Object System.Net.Http.ByteArrayContent(@(, $bytes))
    $file.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/zip')
    $content.Add($file, 'file', [System.IO.Path]::GetFileName($zipPath))
    $response = $http.PostAsync("$Base/api/plugins/install", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ Status = [int]$response.StatusCode; Text = $text }
}

function Start-SlowInvoke([int]$delayMs) {
    $body = @{ capabilityId = 'notes.slow-echo'; arguments = @{ delayMs = $delayMs } } |
        ConvertTo-Json -Depth 5 -Compress
    $content = New-Object System.Net.Http.StringContent($body, [System.Text.Encoding]::UTF8, 'application/json')
    return $slowHttp.PostAsync("$Base/api/plugins/$($script:pluginId)/invoke", $content)
}

function Complete-SlowInvoke($task) {
    $response = $task.GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ Status = [int]$response.StatusCode; Text = $text }
}

function New-PluginPackage([string]$version, [string]$target) {
    $out = Join-Path $work ("build-" + $version)
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    & dotnet build 'samples\MyFirstMafAgent.Plugin.SamplePlatform\MyFirstMafAgent.Plugin.SamplePlatform.csproj' `
        -c Release -p:Version=$version -p:OutputPath="$out" --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sample plugin build failed for version $version" }

    # The manifest is copied verbatim from source, so bump its version to match the assembly:
    # the runtime handshake rejects a plugin whose self-reported version differs from the manifest.
    $manifest = Join-Path $out 'manifest.json'
    $text = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8)
    $text = $text -replace '"version":\s*"[0-9]+\.[0-9]+\.[0-9]+"', ('"version": "' + $version + '"')
    [System.IO.File]::WriteAllText($manifest, $text, (New-Object System.Text.UTF8Encoding($false)))

    if (Test-Path $target) { Remove-Item $target -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $target -Force
    return $target
}

function Start-Api {
    $env:Jobs__Enabled = 'true'
    $env:Jobs__MaxConcurrentJobs = '2'
    $env:Jobs__PollIntervalMs = '300'
    $env:Jobs__LeaseSeconds = '30'
    $env:Jobs__FeederIntervalSeconds = '1'
    $env:Plugins__Enabled = 'true'
    $env:Plugins__IntegrityPolicy = 'warn'
    $env:Plugins__ScheduledSyncEnabled = 'true'
    $env:Plugins__MinScheduledSyncIntervalSeconds = '30'
    $env:Plugins__DrainTimeoutSeconds = '20'
    $env:Desktop__OpenBrowser = 'false'
    # NOTE: do not run the API in Development here - appsettings.Development.json points the
    # connection string at an external PostgreSQL on 5432, while this e2e wants the portable one.

    $stdout = Join-Path $work 'api-out.log'
    $stderr = Join-Path $work 'api-err.log'
    $exe = Join-Path $ApiDir 'Zhijian.Api.exe'
    $process = Start-Process -FilePath $exe -WorkingDirectory $ApiDir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    for ($i = 0; $i -lt 360; $i++) {
        if ($process.HasExited) {
            $tail = ''
            if (Test-Path $stderr) { $tail = (Get-Content $stderr -Tail 20) -join [Environment]::NewLine }
            throw "API exited during startup (code $($process.ExitCode)):$([Environment]::NewLine)$tail"
        }
        try {
            $health = Invoke-WebRequest "$Base/api/health" -UseBasicParsing -TimeoutSec 3
            if ($health.StatusCode -eq 200) { return $process }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "API did not become healthy at $Base"
}

function Stop-Api {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try { if (-not $Process.HasExited) { $Process.Kill() } } catch { }
    try { $Process.WaitForExit(30000) | Out-Null } catch { }
}

function Get-SyncJobs {
    $json = (Invoke-Json 'GET' "$Base/api/jobs?type=plugin.sync&limit=200" $null).Content | ConvertFrom-Json
    return @($json.jobs)
}

function Get-Documents([string]$knowledgeBaseId) {
    return @((Invoke-Json 'GET' "$Base/api/documents?knowledgeBaseId=$knowledgeBaseId" $null).Content | ConvertFrom-Json)
}

function Get-Count($value) {
    return ($value | Measure-Object).Count
}

# No DELETE route for knowledge bases, so reuse them by name across runs (a fresh KB each run
# would leak a row every time).
function Get-OrCreate-KnowledgeBase([string]$name) {
    $list = (Invoke-Json 'GET' "$Base/api/knowledge-bases" $null).Content | ConvertFrom-Json
    foreach ($kb in @($list)) {
        if ([string]$kb.name -eq $name) { return [string]$kb.id }
    }

    $created = Invoke-Json 'POST' "$Base/api/knowledge-bases" @{ name = $name }
    if ($created.Status -ne 200) { throw "cannot create knowledge base '$name': $($created.Status) $($created.Content)" }
    return [string](($created.Content | ConvertFrom-Json).id)
}

try {
    Write-Host "work dir: $work"

    if (-not $SkipPublish) {
        Write-Host 'publishing API ...'
        & dotnet publish 'MyFirstMafAgent.Api\MyFirstMafAgent.Api.csproj' -c Release -o $ApiDir --nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    }

    # A stale plugin directory from an earlier run would make the first install look like an
    # upgrade, which is exactly the code path we want to exercise later, not now.
    $pluginRoot = Join-Path $ApiDir 'App_Data\plugins'
    if (Test-Path $pluginRoot) { Remove-Item $pluginRoot -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host 'building plugin packages v1.0.0 and v1.0.1 ...'
    $zipV1 = New-PluginPackage '1.0.0' (Join-Path $work 'sample-v1.0.0.zip')
    $zipV2 = New-PluginPackage '1.0.1' (Join-Path $work 'sample-v1.0.1.zip')

    $script:api = Start-Api
    Check 'api-healthy' $true $Base

    $types = (Invoke-Json 'GET' "$Base/api/jobs/types" $null).Content | ConvertFrom-Json
    Check 'plugin-sync-job-registered' ($types.types -contains 'plugin.sync') ($types.types -join ', ')

    # ───────────── 0. clean slate: remove anything a previous run left behind ─────────────
    $existing = (Invoke-Json 'GET' "$Base/api/plugins" $null).Content | ConvertFrom-Json
    foreach ($item in @($existing.plugins)) {
        Invoke-Json 'DELETE' "$Base/api/plugins/$($item.id)" $null | Out-Null
    }
    foreach ($key in @(((Invoke-Json 'GET' "$Base/api/plugins/trusted-keys" $null).Content | ConvertFrom-Json).keys)) {
        if ($key.keyId -and -not $key.keyId.StartsWith('config:')) {
            Invoke-Json 'DELETE' "$Base/api/plugins/trusted-keys/$($key.keyId)" $null | Out-Null
        }
    }

    $script:kb1 = Get-OrCreate-KnowledgeBase 'PLUGIN-OPS'
    $script:kb2 = Get-OrCreate-KnowledgeBase 'PLUGIN-OPS-ASYNC'
    Write-Host "knowledge bases: $script:kb1 / $script:kb2"

    # Left-over documents from an earlier run would make the item-map counts wrong.
    foreach ($kb in @($script:kb1, $script:kb2)) {
        foreach ($doc in (Get-Documents $kb)) { Invoke-Json 'DELETE' "$Base/api/documents/$($doc.id)" $null | Out-Null }
    }

    # ───────────── 1. install v1.0.0 and get it running ─────────────
    $install = Install-Package $zipV1
    Check 'install-v1' ($install.Status -eq 200 -and ($install.Text | ConvertFrom-Json).version -eq '1.0.0') `
        ("status=" + $install.Status + ' body=' + $install.Text)

    $granted = Invoke-Json 'PUT' "$Base/api/plugins/$($script:pluginId)/permissions" @{ permissions = @('network') }
    Check 'grant-permission' ($granted.Status -eq 200) ("status=" + $granted.Status)

    $running = Wait-PluginState $script:pluginId 'Running' 30
    Check 'plugin-running' ($running.state -eq 'Running') ("state=" + $running.state + ' error=' + $running.lastError)

    $configured = Invoke-Json 'PUT' "$Base/api/plugins/$($script:pluginId)/config" @{
        config = @{ workspace = 'e2e'; syncIntervalSeconds = 30 }
    }
    Check 'configure-sync-interval' ($configured.Status -eq 200) ("status=" + $configured.Status)

    $running = Wait-PluginState $script:pluginId 'Running' 30
    Check 'plugin-running-after-config' ($running.state -eq 'Running') ("state=" + $running.state)

    # ───────────── 2. manual sync: cursor + next interval ─────────────
    $sync1 = (Invoke-Json 'POST' "$Base/api/plugins/$($script:pluginId)/sync" @{
        capabilityId = 'platform.notes.sync'
        knowledgeBaseId = $script:kb1
    }).Content | ConvertFrom-Json

    Check 'sync-created-items' (($sync1.created + $sync1.updated + $sync1.skipped) -eq 2) `
        ("created=" + $sync1.created + ' updated=' + $sync1.updated + ' skipped=' + $sync1.skipped + ' status=' + $sync1.status)
    Check 'sync-records-interval' ($sync1.nextSyncAfterSeconds -eq 30) ("nextSyncAfterSeconds=" + $sync1.nextSyncAfterSeconds)
    Check 'sync-advanced-cursor' ($sync1.nextCursor -eq 'done') ("cursor=" + $sync1.nextCursor)

    $items = (Invoke-Json 'GET' "$Base/api/plugins/$($script:pluginId)/items?knowledgeBaseId=$($script:kb1)" $null).Content | ConvertFrom-Json
    Check 'item-map-written' ((Get-Count $items.items) -eq 2) ("items=" + (Get-Count $items.items))

    # Repeating the sync must not duplicate content. `cursor = "restart"` asks the sample plugin
    # to replay the first page from scratch: the content hash is unchanged, so everything is
    # skipped — and the plugin reports its interval again, which keeps the scheduled-sync check
    # below meaningful (a plain second page would answer "no more pages" and clear the interval).
    $sync2 = (Invoke-Json 'POST' "$Base/api/plugins/$($script:pluginId)/sync" @{
        capabilityId = 'platform.notes.sync'
        knowledgeBaseId = $script:kb1
        cursor = 'restart'
    }).Content | ConvertFrom-Json
    Check 'resync-is-idempotent' ($sync2.created -eq 0 -and $sync2.updated -eq 0 -and $sync2.skipped -eq 2) `
        ("created=" + $sync2.created + ' updated=' + $sync2.updated + ' skipped=' + $sync2.skipped)

    $state = (Invoke-Json 'GET' "$Base/api/plugins/$($script:pluginId)/sync-state" $null).Content | ConvertFrom-Json
    $kb1State = @($state.states) | Where-Object { $_.knowledgeBaseId -eq $script:kb1 } | Select-Object -First 1
    Check 'sync-state-persisted' ($null -ne $kb1State -and $kb1State.lastStatus -eq 'success' -and $kb1State.nextSyncAfterSeconds -eq 30) `
        ("status=" + $kb1State.lastStatus + ' interval=' + $kb1State.nextSyncAfterSeconds)

    # ───────────── 3. drain: in-flight call finishes, new calls are rejected ─────────────
    $slowTask = Start-SlowInvoke 3000
    Start-Sleep -Milliseconds 900

    $inflightDetail = Get-Plugin $script:pluginId
    Check 'in-flight-visible' ($inflightDetail.inFlightInvocations -ge 1) ("inFlight=" + $inflightDetail.inFlightInvocations)

    $drain = (Invoke-Json 'POST' "$Base/api/plugins/$($script:pluginId)/drain" $null).Content | ConvertFrom-Json
    $slowResult = Complete-SlowInvoke $slowTask

    Check 'drain-succeeded' ($drain.drained -eq $true) ("drained=" + $drain.drained + ' inFlight=' + $drain.inFlight)
    Check 'drain-let-inflight-finish' ($slowResult.Status -eq 200 -and ($slowResult.Text | ConvertFrom-Json).result.waitedMs -eq 3000) `
        ("status=" + $slowResult.Status + ' body=' + $slowResult.Text)

    $rejected = Invoke-Json 'POST' "$Base/api/plugins/$($script:pluginId)/invoke" @{
        capabilityId = 'notes.slow-echo'
        arguments = @{ delayMs = 0 }
    }
    Check 'draining-rejects-new-calls' ($rejected.Status -eq 503) ("status=" + $rejected.Status + ' body=' + $rejected.Content)

    $audit = (Invoke-Json 'GET' "$Base/api/plugins/$($script:pluginId)/audit" $null).Content | ConvertFrom-Json
    $drainAudit = @($audit.entries) | Where-Object { $_.action -eq 'drain' } | Select-Object -First 1
    Check 'drain-audited' ($null -ne $drainAudit) ("actions=" + ((@($audit.entries) | Select-Object -ExpandProperty action) -join ','))

    # ───────────── 4. upgrading a drained plugin brings a healthy process back ─────────────
    # The previous step left the host in Draining on purpose: only a successful upgrade/restart
    # may clear it, otherwise an operator could soft-offline a plugin and never bring it back.
    $upgrade = Install-Package $zipV2
    $upgradeBody = $upgrade.Text
    Check 'upgrade-v101' ($upgrade.Status -eq 200 -and ($upgradeBody | ConvertFrom-Json).version -eq '1.0.1') `
        ("status=" + $upgrade.Status + ' body=' + $upgradeBody)

    $afterUpgrade = Wait-PluginState $script:pluginId 'Running' 30
    Check 'upgraded-plugin-running' ($afterUpgrade.state -eq 'Running' -and $afterUpgrade.version -eq '1.0.1') `
        ("state=" + $afterUpgrade.state + ' version=' + $afterUpgrade.version + ' draining=' + $afterUpgrade.draining)

    $afterUpgradeInvoke = Invoke-Json 'POST' "$Base/api/plugins/$($script:pluginId)/invoke" @{
        capabilityId = 'notes.slow-echo'
        arguments = @{ delayMs = 100 }
    }
    Check 'upgrade-cleared-draining' ($afterUpgradeInvoke.Status -eq 200) ("status=" + $afterUpgradeInvoke.Status + ' body=' + $afterUpgradeInvoke.Content)

    $audit = (Invoke-Json 'GET' "$Base/api/plugins/$($script:pluginId)/audit" $null).Content | ConvertFrom-Json
    $upgradeAudit = @($audit.entries) | Where-Object { $_.action -eq 'install' -and $_.detail -like '*drain upgrade*' } | Select-Object -First 1
    Check 'upgrade-audited-as-drain-upgrade' ($null -ne $upgradeAudit) ("detail=" + $upgradeAudit.detail)

    # ───────────── 5. upgrade while an invocation is in flight ─────────────
    # This is the whole point of the two-phase install: the caller must not see a broken pipe.
    $slowTask2 = Start-SlowInvoke 4000
    Start-Sleep -Milliseconds 900

    $downgrade = Install-Package $zipV1
    $slowResult2 = Complete-SlowInvoke $slowTask2

    Check 'upgrade-kept-inflight-call' ($slowResult2.Status -eq 200 -and ($slowResult2.Text | ConvertFrom-Json).result.elapsedMs -ge 3000) `
        ("status=" + $slowResult2.Status + ' body=' + $slowResult2.Text)

    $finalDetail = Wait-PluginState $script:pluginId 'Running' 30
    Check 'downgrade-installed' ($finalDetail.state -eq 'Running' -and $finalDetail.version -eq '1.0.0') `
        ("status=" + $downgrade.Status + ' state=' + $finalDetail.state + ' version=' + $finalDetail.version + ' body=' + $downgrade.Text)

    # ───────────── 5. queued sync through the job center ─────────────
    $queued = (Invoke-Json 'POST' "$Base/api/plugins/$($script:pluginId)/sync-async" @{
        capabilityId = 'platform.notes.sync'
        knowledgeBaseId = $script:kb2
    }).Content | ConvertFrom-Json
    Check 'sync-async-queued' ($queued.type -eq 'plugin.sync' -and $queued.id) ("job=" + $queued.id + ' status=' + $queued.status)

    $queuedFinal = $null
    for ($i = 0; $i -lt 240; $i++) {
        $queuedFinal = @(Get-SyncJobs) | Where-Object { $_.id -eq $queued.id } | Select-Object -First 1
        if ($queuedFinal.status -in @('Succeeded', 'Failed', 'Cancelled')) { break }
        Start-Sleep -Milliseconds 500
    }
    Check 'sync-job-succeeded' ($queuedFinal.status -eq 'Succeeded') ("status=" + $queuedFinal.status + ' error=' + $queuedFinal.lastError)

    $kb2Docs = Get-Documents $script:kb2
    Check 'sync-job-ingested' ((Get-Count $kb2Docs) -eq 2) ("documents=" + (Get-Count $kb2Docs))

    # ───────────── 7. scheduled sync: the feeder enqueues without anyone clicking ─────────────
    # The feeder re-arms the *existing* job for a scope (same idempotency key), so "a new id
    # appeared" is the wrong signal: watch the scope's job timestamps instead.
    $kb1Baseline = @(Get-SyncJobs) | Where-Object { $_.knowledgeBaseId -eq $script:kb1 } |
        ForEach-Object { [DateTimeOffset]$_.updatedAt } | Sort-Object | Select-Object -Last 1
    if ($null -eq $kb1Baseline) { $kb1Baseline = [DateTimeOffset]::MinValue }

    Write-Host ("waiting up to {0}s for the feeder to enqueue the next scheduled sync ..." -f $ScheduleWaitSeconds)

    $scheduled = $null
    for ($i = 0; $i -lt $ScheduleWaitSeconds; $i++) {
        $fresh = @(Get-SyncJobs) | Where-Object {
            $_.knowledgeBaseId -eq $script:kb1 -and [DateTimeOffset]$_.updatedAt -gt $kb1Baseline
        }
        if (@($fresh).Count -gt 0) { $scheduled = @($fresh)[0]; break }
        Start-Sleep -Seconds 1
    }
    Check 'feeder-enqueued-scheduled-sync' ($null -ne $scheduled) ("job=" + $(if ($scheduled) { $scheduled.id } else { 'none' }))

    if ($scheduled) {
        $scheduledFinal = $null
        for ($i = 0; $i -lt 240; $i++) {
            $scheduledFinal = @(Get-SyncJobs) | Where-Object { $_.id -eq $scheduled.id } | Select-Object -First 1
            if ($scheduledFinal.status -in @('Succeeded', 'Failed', 'Cancelled')) { break }
            Start-Sleep -Milliseconds 500
        }
        Check 'scheduled-sync-finished' ($scheduledFinal.status -eq 'Succeeded') `
            ("status=" + $scheduledFinal.status + ' attempts=' + $scheduledFinal.attempts + ' error=' + $scheduledFinal.lastError)
    }

    # ───────────── 7. trusted publisher keys ─────────────
    $before = @(((Invoke-Json 'GET' "$Base/api/plugins/trusted-keys" $null).Content | ConvertFrom-Json).keys)
    Check 'trusted-keys-empty' ((Get-Count $before) -eq 0) ("keys=" + (Get-Count $before))

    $put = Invoke-Json 'PUT' "$Base/api/plugins/trusted-keys/e2e-ec" @{
        pluginId = 'com.example'
        algorithm = 'ecdsa'
        publicKeyPem = $ecPublicPem
        note = 'e2e publisher'
    }
    $putBody = $put.Content | ConvertFrom-Json
    Check 'trusted-key-saved' ($put.Status -eq 200 -and $putBody.fingerprint.Length -eq 16) `
        ("status=" + $put.Status + ' fingerprint=' + $putBody.fingerprint)

    $withKey = Get-Plugin $script:pluginId
    Check 'plugin-sees-trusted-key' ($null -ne $withKey.trustedKey -and $withKey.trustedKey.fingerprint -eq $putBody.fingerprint) `
        ("trustedKey=" + $withKey.trustedKey.fingerprint)

    $privateRejected = Invoke-Json 'PUT' "$Base/api/plugins/trusted-keys/e2e-ec" @{
        pluginId = 'com.example'
        algorithm = 'ecdsa'
        publicKeyPem = $ecPrivatePem
    }
    Check 'private-key-rejected' ($privateRejected.Status -eq 400) ("status=" + $privateRejected.Status + ' body=' + $privateRejected.Content)

    $badAlgorithm = Invoke-Json 'PUT' "$Base/api/plugins/trusted-keys/e2e-ec" @{
        pluginId = 'com.example'
        algorithm = 'dsa'
        publicKeyPem = $ecPublicPem
    }
    Check 'bad-algorithm-rejected' ($badAlgorithm.Status -eq 400) ("status=" + $badAlgorithm.Status)

    $rsaSaved = Invoke-Json 'PUT' "$Base/api/plugins/trusted-keys/e2e-rsa" @{
        pluginId = 'org.other'
        algorithm = 'rsa'
        publicKeyPem = $rsaPublicPem
    }
    Check 'rsa-key-saved' ($rsaSaved.Status -eq 200) ("status=" + $rsaSaved.Status)

    $configDelete = Invoke-Json 'DELETE' "$Base/api/plugins/trusted-keys/config:com.example" $null
    Check 'config-key-undeletable' ($configDelete.Status -eq 400) ("status=" + $configDelete.Status)

    $deleted = Invoke-Json 'DELETE' "$Base/api/plugins/trusted-keys/e2e-ec" $null
    $deletedAgain = Invoke-Json 'DELETE' "$Base/api/plugins/trusted-keys/e2e-ec" $null
    Invoke-Json 'DELETE' "$Base/api/plugins/trusted-keys/e2e-rsa" $null | Out-Null
    $noKey = Get-Plugin $script:pluginId

    Check 'trusted-key-deleted' ($deleted.Status -eq 204 -and $deletedAgain.Status -eq 404) `
        ("first=" + $deleted.Status + ' second=' + $deletedAgain.Status)
    Check 'plugin-loses-trusted-key' ($null -eq $noKey.trustedKey) ("trustedKey=" + $noKey.trustedKey)

    $finalKeys = @(((Invoke-Json 'GET' "$Base/api/plugins/trusted-keys" $null).Content | ConvertFrom-Json).keys)
    Check 'trusted-keys-clean' ((Get-Count $finalKeys) -eq 0) ("keys=" + (Get-Count $finalKeys))

    Write-Host ("jobs seen: " + (@(Get-SyncJobs) | Measure-Object).Count)

    # Cleanup happens while the API is still up: uninstall drops the plugin rows and its
    # item map, and the synced documents are deleted explicitly (no cascade in the domain).
    foreach ($kb in @($script:kb1, $script:kb2)) {
        if (-not $kb) { continue }
        foreach ($doc in (Get-Documents $kb)) {
            Invoke-Json 'DELETE' "$Base/api/documents/$($doc.id)" $null | Out-Null
        }
    }

    $uninstalled = Invoke-Json 'DELETE' "$Base/api/plugins/$($script:pluginId)" $null
    Check 'uninstall-clean' ($uninstalled.Status -eq 204) ("status=" + $uninstalled.Status)

    $remaining = @(((Invoke-Json 'GET' "$Base/api/plugins" $null).Content | ConvertFrom-Json).plugins)
    Check 'no-plugins-left' ((Get-Count $remaining) -eq 0) ("plugins=" + (Get-Count $remaining))
}
finally {
    Stop-Api -Process $script:api

    if ($script:failures -gt 0) {
        Write-Host "work dir kept for diagnosis: $work" -ForegroundColor Yellow
        $err = Join-Path $work 'api-err.log'
        if (Test-Path $err) { Write-Host '--- api stderr (tail) ---'; (Get-Content $err -Tail 40) | ForEach-Object { Write-Host $_ } }
    }
    else {
        try { Remove-Item -Path $work -Recurse -Force } catch { }
    }

    try { $http.Dispose() } catch { }
    try { $slowHttp.Dispose() } catch { }
}

if ($script:failures -gt 0) {
    Write-Host ("{0} check(s) failed" -f $script:failures) -ForegroundColor Red
    exit 1
}

Write-Host 'all plugin operations checks passed' -ForegroundColor Green
exit 0
