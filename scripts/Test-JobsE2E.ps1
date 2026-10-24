# Phase 7 end-to-end: long-task concurrency + job center against a real database.
#
# Verifies what unit tests cannot: multiple lanes actually run at the same time,
# progress lands in the table, SSE streams, cancel is respected (and not resurrected
# by the feeder), retry works, and a killed process is recovered by lease expiry.
param(
    [string]$ApiDir = 'C:\Temp\mafapi7',
    [string]$Base = 'http://127.0.0.1:5056',
    [int]$Documents = 10
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:failures = 0
$script:api = $null
$work = Join-Path $env:TEMP ('jobs-e2e-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(5)

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0,-38} {1}" -f $name, $detail) -ForegroundColor Green }
    else { Write-Host ("FAIL  {0,-38} {1}" -f $name, $detail) -ForegroundColor Red; $script:failures++ }
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

function Start-Api {
    param([switch]$SchedulerDisabled)

    # Small lease + short poll so recovery/cancel are observable within seconds.
    # NOTE: Jobs validation requires PollIntervalMs >= 200 and LeaseSeconds >= 30.
    $env:Jobs__Enabled = if ($SchedulerDisabled) { 'false' } else { 'true' }
    $env:Jobs__MaxConcurrentJobs = '3'
    $env:Jobs__PollIntervalMs = '300'
    $env:Jobs__LeaseSeconds = '30'
    $env:Jobs__RetryBaseSeconds = '2'
    $env:Jobs__FeederIntervalSeconds = '1'
    $env:Desktop__OpenBrowser = 'false'

    $stdout = Join-Path $work 'api-out.log'
    $stderr = Join-Path $work 'api-err.log'
    $exe = Join-Path $ApiDir 'MyFirstMafAgent.Api.exe'
    $process = Start-Process -FilePath $exe -WorkingDirectory $ApiDir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    for ($i = 0; $i -lt 360; $i++) {
        if ($process.HasExited) {
            $tail = ''
            if (Test-Path $stderr) { $tail = (Get-Content $stderr -Tail 15) -join [Environment]::NewLine }
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
    param([System.Diagnostics.Process]$Process, [switch]$Force)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) { $Process.Kill() }
    } catch { }
    try { $Process.WaitForExit(30000) | Out-Null } catch { }
}

function Upload-Document([string]$path, [string]$knowledgeBaseId) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $content = New-Object System.Net.Http.MultipartFormDataContent
    $file = New-Object System.Net.Http.ByteArrayContent(@(, $bytes))
    $file.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/markdown')
    $content.Add($file, 'file', [System.IO.Path]::GetFileName($path))
    $response = $http.PostAsync("$Base/api/documents?knowledgeBaseId=$knowledgeBaseId", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ Status = [int]$response.StatusCode; Text = $text }
}

function Get-Jobs {
    $json = (Invoke-Json 'GET' "$Base/api/jobs?type=document.ingest&limit=200" $null).Content | ConvertFrom-Json
    return @($json.jobs)
}

function Get-Counts {
    return (Invoke-Json 'GET' "$Base/api/jobs/counts" $null).Content | ConvertFrom-Json
}

function Get-Documents([string]$knowledgeBaseId) {
    return @((Invoke-Json 'GET' "$Base/api/documents?knowledgeBaseId=$knowledgeBaseId" $null).Content | ConvertFrom-Json)
}

function New-Markdown([string]$path, [string]$title, [int]$sections) {
    $lines = @("# $title", '')
    for ($i = 1; $i -le $sections; $i++) {
        $lines += "## Section $i"
        $lines += ''
        $lines += "Paragraph $i explains how the job scheduler claims work with an optimistic CAS update, "
        $lines += "renews a lease while the handler runs, and backs off exponentially when a handler fails."
        $lines += ''
    }
    Set-Content -Path $path -Value $lines -Encoding UTF8
}

try {
    Write-Host "work dir: $work"

    # ───────────── 1. publish sanity: scheduler starts with a real handler ─────────────
    $script:api = Start-Api
    Check 'api-healthy' $true $Base

    $types = (Invoke-Json 'GET' "$Base/api/jobs/types" $null).Content | ConvertFrom-Json
    Check 'job-types-registered' ($types.types -contains 'document.ingest') ($types.types -join ', ')

    $kb = (Invoke-Json 'POST' "$Base/api/knowledge-bases" @{ name = 'JOBS-E2E' }).Content | ConvertFrom-Json
    $kbId = $kb.id
    Write-Host "knowledge base: $kbId"

    # ───────────── 2. burst upload: several ingests must run at the same time ─────────────
    $uploaded = @()
    for ($i = 1; $i -le $Documents; $i++) {
        $path = Join-Path $work ("doc{0:d2}.md" -f $i)
        New-Markdown $path ("Concurrency Note {0}" -f $i) 40
        $result = Upload-Document $path $kbId
        if ($result.Status -ne 200) { throw "upload failed: $($result.Status) $($result.Text)" }
        $uploaded += ($result.Text | ConvertFrom-Json)
    }
    Check 'upload-accepted' ($uploaded.Count -eq $Documents -and ($uploaded | Where-Object { $_.status -ne 'Queued' }).Count -eq 0) ("count=" + $uploaded.Count)
    Check 'api-still-alive' (-not $script:api.HasExited) ("exitCode=" + $(if ($script:api.HasExited) { $script:api.ExitCode } else { 'running' }))

    $maxRunning = 0
    $sawProgress = $false
    # Do not stop early: right after the uploads the feeder has not enqueued anything yet.
    for ($i = 0; $i -lt 300; $i++) {
        $counts = Get-Counts
        if ($counts.running -gt $maxRunning) { $maxRunning = $counts.running }
        $jobs = Get-Jobs
        if (($jobs | Where-Object { $_.progress -gt 0 -and $_.progress -lt 100 }).Count -gt 0) { $sawProgress = $true }
        if ($counts.succeeded -ge $Documents -and $counts.queued -eq 0 -and $counts.running -eq 0) { break }
        Start-Sleep -Milliseconds 100
    }

    # Sampling can miss short windows, so prove concurrency from the rows themselves:
    # the largest number of jobs whose [startedAt, completedAt] windows overlap.
    $jobs = Get-Jobs
    $edges = @()
    foreach ($job in $jobs) {
        if (-not $job.startedAt) { continue }
        $start = ([DateTimeOffset]$job.startedAt).ToUnixTimeMilliseconds()
        $end = if ($job.completedAt) { ([DateTimeOffset]$job.completedAt).ToUnixTimeMilliseconds() } else { $start + 1 }
        if ($end -le $start) { $end = $start + 1 }
        $edges += [pscustomobject]@{ at = $start; delta = 1 }
        $edges += [pscustomobject]@{ at = $end; delta = -1 }
    }
    $running = 0
    $maxOverlap = 0
    foreach ($edge in ($edges | Sort-Object at, @{ Expression = 'delta' })) {
        $running += $edge.delta
        if ($running -gt $maxOverlap) { $maxOverlap = $running }
    }

    Check 'parallel-lanes' ($maxOverlap -ge 2 -and $maxRunning -ge 2) ("max overlap = $maxOverlap, max sampled running = $maxRunning")
    Check 'progress-reported' $sawProgress 'at least one job reported 1..99 progress'

    # ───────────── 3. every job finishes and every document is readable ─────────────
    for ($i = 0; $i -lt 300; $i++) {
        $docs = Get-Documents $kbId
        if (($docs | Where-Object { $_.status -ne 'Ready' }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    $docs = Get-Documents $kbId
    $notReady = @($docs | Where-Object { $_.status -ne 'Ready' })
    Check 'documents-ready' ($notReady.Count -eq 0) ("ready=" + ($docs.Count - $notReady.Count) + "/" + $docs.Count)

    $jobs = Get-Jobs
    $docJobs = @($jobs | Where-Object { $uploaded.id -contains $_.subjectId })
    $succeeded = @($docJobs | Where-Object { $_.status -eq 'Succeeded' }).Count
    Check 'jobs-succeeded' ($succeeded -eq $Documents -and $docJobs.Count -eq $Documents) ("jobs=" + $docJobs.Count + " succeeded=" + $succeeded)

    $duplicated = @($docJobs | Group-Object subjectId | Where-Object { $_.Count -gt 1 })
    Check 'no-duplicate-jobs' ($duplicated.Count -eq 0) ("groups with >1 job: " + $duplicated.Count)

    # ───────────── 4. SSE stream ─────────────
    $sseClient = New-Object System.Net.Http.HttpClient
    $sseClient.Timeout = [TimeSpan]::FromMinutes(2)
    $sseResponse = $sseClient.GetAsync("$Base/api/jobs/stream", [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $sseStream = $sseResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $sseReader = New-Object System.IO.StreamReader($sseStream)

    # Read the snapshot frame first (a fresh subscriber always gets one immediately).
    $frames = @()
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (($frames -contains 'event: snapshot') -and (@($frames | Where-Object { $_ -like 'data: *' }).Count -gt 0)) { break }
        $read = $sseReader.ReadLineAsync()
        if ($read.Wait(5000)) {
            if ($null -eq $read.Result) { break }
            if ($read.Result.Length -gt 0) { $frames += $read.Result }
        } else { break }
    }
    Check 'sse-snapshot' ($frames -contains 'event: snapshot') (($frames | Select-Object -First 2) -join ' | ')

    # Now create fresh work and wait for a live `job` frame.
    $sseDoc = Join-Path $work 'sse-doc.md'
    New-Markdown $sseDoc 'SSE Note' 60
    $sseUpload = (Upload-Document $sseDoc $kbId).Text | ConvertFrom-Json

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and ($frames -notcontains 'event: job')) {
        $read = $sseReader.ReadLineAsync()
        if ($read.Wait(6000)) {
            if ($null -eq $read.Result) { break }
            if ($read.Result.Length -gt 0) { $frames += $read.Result }
        } else { break }
    }
    $jobFrames = @($frames | Where-Object { $_ -eq 'event: job' }).Count
    Check 'sse-frames' ($jobFrames -ge 1) ("job frames=$jobFrames, lines=" + $frames.Count)

    # ───────────── 5. cancel is honoured and not resurrected ─────────────
    # Cancelling a *running* ingest is inherently racy: parsing a text file finishes in about a
    # second, so the cancel often arrives after the work is done (cancellation is cooperative and
    # delivered by the heartbeat). To test the contract deterministically we park the scheduler:
    # with Jobs:Enabled=false jobs are enqueued but never claimed, so the job is Queued.
    $sseReader.Dispose()
    $sseClient.Dispose()
    Stop-Api -Process $script:api
    $script:api = Start-Api -SchedulerDisabled

    $cancelPath = Join-Path $work 'cancel-me.md'
    New-Markdown $cancelPath 'Cancellation Note' 40
    $cancelDoc = (Upload-Document $cancelPath $kbId).Text | ConvertFrom-Json
    $cancelDocId = $cancelDoc.id

    $created = (Invoke-Json 'POST' "$Base/api/jobs" @{
        type = 'document.ingest'
        payload = @{ documentId = $cancelDocId }
        priority = 0
        # Same identity the feeder derives, otherwise the feeder would just create a second job
        # for the same document and the "cancel is not resurrected" rule would never be exercised.
        idempotencyKey = "document.ingest:$cancelDocId"
        subjectId = $cancelDocId
        knowledgeBaseId = $kbId
    }).Content | ConvertFrom-Json
    Check 'job-enqueued-while-paused' ($created.status -eq 'Queued') ("job=" + $created.id + " status=" + $created.status)

    $cancelled = (Invoke-Json 'POST' "$Base/api/jobs/$($created.id)/cancel" $null).Content | ConvertFrom-Json
    Check 'cancel-queued-job' ($cancelled.status -eq 'Cancelled') ("status=" + $cancelled.status)

    $conflict = Invoke-Json 'POST' "$Base/api/jobs/$($created.id)/cancel" $null
    Check 'cancel-is-idempotent' ($conflict.Status -eq 409) ("second cancel -> " + $conflict.Status)

    # Restart with the scheduler enabled: the feeder must NOT resurrect the cancelled job,
    # it must mark the orphaned document as failed instead.
    Stop-Api -Process $script:api
    $script:api = Start-Api
    Start-Sleep -Seconds 6

    $afterCancel = @(Get-Jobs) | Where-Object { $_.subjectId -eq $cancelDocId }
    Check 'cancel-not-resurrected' (@($afterCancel).Count -eq 1 -and ($afterCancel | Select-Object -First 1).status -eq 'Cancelled') ("jobs=" + @($afterCancel).Count + " status=" + ($afterCancel | Select-Object -First 1).status)

    $docAfterCancel = (Get-Documents $kbId) | Where-Object { $_.id -eq $cancelDocId } | Select-Object -First 1
    Check 'document-not-stuck' ($docAfterCancel.status -eq 'Failed') ("document status=" + $docAfterCancel.status + " error=" + $docAfterCancel.error)

    # ───────────── 6. manual retry of the cancelled job really re-runs the document ─────────────
    $retried = (Invoke-Json 'POST' "$Base/api/jobs/$($created.id)/retry" $null).Content | ConvertFrom-Json
    Check 'retry-requeued' ($retried.status -eq 'Queued') ("status=" + $retried.status)

    $retryFinal = $null
    for ($i = 0; $i -lt 240; $i++) {
        $retryFinal = @(Get-Jobs) | Where-Object { $_.id -eq $created.id } | Select-Object -First 1
        if ($retryFinal.status -in @('Succeeded', 'Failed')) { break }
        Start-Sleep -Milliseconds 500
    }
    Check 'retry-succeeds' ($retryFinal.status -eq 'Succeeded') ("status=" + $retryFinal.status + " error=" + $retryFinal.lastError)

    $docAfterRetry = (Get-Documents $kbId) | Where-Object { $_.id -eq $cancelDocId } | Select-Object -First 1
    Check 'retry-parsed-document' ($docAfterRetry.status -eq 'Ready' -and $docAfterRetry.chunkCount -gt 0) ("status=" + $docAfterRetry.status + " chunks=" + $docAfterRetry.chunkCount)

    # ───────────── 7. crash recovery: kill the API mid-run and restart ─────────────
    $crashIds = @()
    for ($i = 1; $i -le 4; $i++) {
        $path = Join-Path $work ("crash{0}.md" -f $i)
        New-Markdown $path ("Crash Recovery Note {0}" -f $i) 400
        $crashIds += ((Upload-Document $path $kbId).Text | ConvertFrom-Json).id
    }

    # Kill as soon as something is actually running: this leaves Running rows with a live lease,
    # which only the lease-expiry recovery can rescue after restart.
    $sawRunning = $false
    for ($i = 0; $i -lt 400; $i++) {
        $counts = Get-Counts
        if ($counts.running -gt 0) { $sawRunning = $true; break }
        Start-Sleep -Milliseconds 100
    }
    Check 'crash-setup-running' $sawRunning 'a job was running when the process was killed'
    Stop-Api -Process $script:api -Force
    $script:api = $null

    $script:api = Start-Api

    # Recovery needs: lease expiry (30s) + grace (15s) + the job re-running itself.
    $settled = $false
    for ($i = 0; $i -lt 480; $i++) {
        $counts = Get-Counts
        $docs = Get-Documents $kbId
        $pending = @($docs | Where-Object { $crashIds -contains $_.id -and $_.status -ne 'Ready' })
        if ($pending.Count -eq 0 -and $counts.running -eq 0 -and $counts.queued -eq 0) { $settled = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Check 'crash-recovered' $settled ("counts: running=" + $counts.running + " queued=" + $counts.queued)

    $crashJobs = @(Get-Jobs) | Where-Object { $crashIds -contains $_.subjectId }
    $crashDone = @($crashJobs | Where-Object { $_.status -eq 'Succeeded' }).Count
    Check 'crash-jobs-succeeded' ($crashDone -eq 4) ("succeeded=" + $crashDone + "/4 jobs=" + @($crashJobs).Count)

    $stranded = @(Get-Jobs) | Where-Object { $_.status -eq 'Running' }
    Check 'no-stranded-running' ($stranded.Count -eq 0) ("running=" + @($stranded).Count)

    $finalCounts = Get-Counts
    Write-Host ("final counts: queued={0} running={1} succeeded={2} failed={3} cancelled={4}" -f $finalCounts.queued, $finalCounts.running, $finalCounts.succeeded, $finalCounts.failed, $finalCounts.cancelled)
}
finally {
    try { if ($sseReader) { $sseReader.Dispose() } } catch { }
    try { if ($sseClient) { $sseClient.Dispose() } } catch { }
    Stop-Api -Process $script:api

    # cleanup: documents then the knowledge base (no DELETE route for knowledge bases)
    try {
        $kbId = $kbId
        if ($kbId -and $null -ne $script:api -and -not $script:api.HasExited) {
            foreach ($doc in (Get-Documents $kbId)) { Invoke-Json 'DELETE' "$Base/api/documents/$($doc.id)" $null | Out-Null }
        }
    } catch { Write-Warning "cleanup failed: $($_.Exception.Message)" }

    try { Remove-Item -Path $work -Recurse -Force } catch { }
    try { $http.Dispose() } catch { }
}

if ($script:failures -gt 0) {
    Write-Host ("{0} check(s) failed" -f $script:failures) -ForegroundColor Red
    exit 1
}

Write-Host 'All job system checks passed.' -ForegroundColor Green
exit 0
