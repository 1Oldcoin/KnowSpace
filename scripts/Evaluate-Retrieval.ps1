#Requires -Version 5.1
<#
.SYNOPSIS
    Re-runnable retrieval evaluation: compares pure vector search against hybrid
    (vector + lexical RRF) on the same fixture.

.DESCRIPTION
    For every case in the fixture the script calls POST /api/search with a fixed
    mode, then scores the ranked result list:

      hit          top-K contains a chunk whose fileName is in expectFiles
                   (and, when expectText is set, whose content contains it)
      recall@K     hits / answerable cases
      MRR          mean of 1/rank of the first hit (0 when nothing matched)

    Cases with "expectAbstain": true are scored separately: the correct behaviour
    is that the server refuses to answer (abstained = true). They never count
    toward recall, and answerable cases that get abstained show up as recall loss
    -- that is the price of abstaining, and the report has to show both sides.

    The point is a *repeatable* baseline: run it before a retrieval change, archive
    the JSON, run it again, diff the numbers. Hand-tuning the fixture to make a
    number go up defeats the purpose.

.PARAMETER BaseUrl
    API base URL. Default http://127.0.0.1:5056.

.PARAMETER Fixture
    Fixture JSON (see scripts/retrieval-eval/default.json). Its knowledgeBaseId is
    used unless -KnowledgeBaseId overrides it.

.PARAMETER Modes
    Modes to compare: vector, lexical, hybrid, rerank. Default: vector + hybrid.

.PARAMETER Limit
    Top-K. Default 5 (the chat default).

.PARAMETER OutFile
    Optional path to write the raw JSON report (for diffing runs).

.PARAMETER ListFiles
    Only print the Ready documents of the knowledge base (helps authoring fixtures).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -ListFiles

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -OutFile C:\Temp\eval-hybrid.json
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5056',
    [string]$Fixture = '',
    [string]$KnowledgeBaseId = '',
    [string[]]$Modes = @('vector', 'hybrid'),
    [int]$Limit = 5,
    [string]$OutFile = '',
    [switch]$ListFiles
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

# `powershell -File ... -Modes vector,lexical,hybrid` hands the whole thing over as ONE
# string, so split it here. Without this the harness silently runs a single mode named
# "vector,lexical,hybrid" and the comparison table has one row.
$modeList = @()
foreach ($entry in $Modes) {
    foreach ($part in ([string]$entry).Split(',')) {
        $trimmed = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) { $modeList += $trimmed }
    }
}
if ($modeList.Count -eq 0) { $modeList = @('vector', 'hybrid') }
$Modes = $modeList

if ([string]::IsNullOrWhiteSpace($Fixture)) {
    $Fixture = Join-Path $PSScriptRoot 'retrieval-eval\default.json'
}
if (-not (Test-Path -LiteralPath $Fixture)) {
    throw "Fixture not found: $Fixture"
}

# Fixtures contain Chinese text; read as UTF-8 explicitly (PowerShell 5.1 defaults to ANSI).
$raw = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Fixture).Path, [System.Text.Encoding]::UTF8)
$spec = $raw | ConvertFrom-Json

$kb = $KnowledgeBaseId
if ([string]::IsNullOrWhiteSpace($kb)) { $kb = $spec.knowledgeBaseId }
# Fixtures seeded by Seed-RetrievalCorpus.ps1 leave knowledgeBaseId empty and
# record the real id next to the fixture, so the harness needs no arguments.
if ([string]::IsNullOrWhiteSpace($kb)) {
    $idFile = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Fixture).Path) 'corpus.kb.txt'
    if (Test-Path -LiteralPath $idFile) { $kb = ([System.IO.File]::ReadAllText($idFile)).Trim() }
}
if ([string]::IsNullOrWhiteSpace($kb)) { throw 'No knowledgeBaseId in fixture, no corpus.kb.txt next to it, and none passed via -KnowledgeBaseId.' }

function Invoke-ApiJson {
    param(
        [string]$Method,
        [string]$Path,
        $Body
    )

    $uri = "$BaseUrl$Path"
    if ($null -eq $Body) {
        return Invoke-RestMethod -Uri $uri -Method $Method -TimeoutSec 120
    }

    $json = $Body | ConvertTo-Json -Depth 6
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Uri $uri -Method $Method -ContentType 'application/json; charset=utf-8' -Body $bytes -TimeoutSec 120
}

if ($ListFiles) {
    $documents = Invoke-ApiJson -Method Get -Path "/api/documents?knowledgeBaseId=$kb" -Body $null
    Write-Host ("Ready documents in knowledge base {0}:" -f $kb)
    foreach ($document in $documents) {
        if ($document.status -eq 'Ready') {
            Write-Host ('  - {0}  ({1} chunks)' -f $document.fileName, $document.chunkCount)
        }
    }
    return
}

function Test-Hit {
    param($Chunk, $Case)

    $expected = @()
    if ($Case.expectFiles) { $expected = @($Case.expectFiles) }

    $nameMatch = $false
    if ($expected.Count -gt 0) {
        foreach ($name in $expected) {
            if ([string]::Equals([string]$Chunk.fileName, [string]$name, [System.StringComparison]::OrdinalIgnoreCase)) {
                $nameMatch = $true
                break
            }
        }
    }

    if (-not $nameMatch) { return $false }

    if ($Case.expectText) {
        $content = [string]$Chunk.content
        if ($content.IndexOf([string]$Case.expectText, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
    }

    return $true
}

$report = [ordered]@{
    fixture     = (Resolve-Path -LiteralPath $Fixture).Path
    name        = $spec.name
    knowledgeBaseId = $kb
    limit       = $Limit
    startedAt   = (Get-Date).ToString('o')
    modes       = @()
}

$summaryLines = @()

foreach ($mode in $Modes) {
    Write-Host ''
    Write-Host ("=== mode: {0} (top {1}) ===" -f $mode, $Limit)
    Write-Host ('{0,-42} {1,-8} {2}' -f 'query', 'rank', 'top hit')
    Write-Host ('-' * 96)

    $hits = 0
    $mrrSum = 0.0
    $answerable = 0
    $abstainCases = 0
    $abstainCorrect = 0
    $rows = @()
    $strategy = ''
    # Per-category buckets: a single recall number hides the fact that vector and
    # lexical fail on opposite kinds of questions.
    $categoryStats = [ordered]@{}

    foreach ($case in $spec.cases) {
        $expectAbstain = $false
        if ($case.expectAbstain) { $expectAbstain = [bool]$case.expectAbstain }

        $response = Invoke-ApiJson -Method Post -Path '/api/search' -Body ([ordered]@{
            query           = $case.query
            limit           = $Limit
            knowledgeBaseId = $kb
            mode            = $mode
        })
        if ($response.strategy) { $strategy = [string]$response.strategy }

        $abstained = $false
        if ($null -ne $response.abstained) { $abstained = [bool]$response.abstained }
        $topScore = $null
        if ($null -ne $response.topScore) { $topScore = [double]$response.topScore }

        $rank = 0
        $top = ''
        $position = 0
        foreach ($chunk in @($response.results)) {
            $position++
            $topName = [string]$chunk.fileName
            if ($position -eq 1) { $top = $topName }
            if ($rank -eq 0 -and (Test-Hit -Chunk $chunk -Case $case)) { $rank = $position }
        }

        $category = 'uncategorised'
        if ($case.category) { $category = [string]$case.category }
        if (-not $categoryStats.Contains($category)) {
            $categoryStats[$category] = [pscustomobject]@{ category = $category; cases = 0; hits = 0; mrrSum = 0.0; abstainCases = 0; abstainCorrect = 0 }
        }
        $bucket = $categoryStats[$category]
        $bucket.cases++

        if ($expectAbstain) {
            # No-answer cases: the correct behaviour is to abstain. They must not
            # enter the recall denominator, otherwise a successful refusal would be
            # counted as a missed retrieval.
            $abstainCases++
            $bucket.abstainCases++
            if ($abstained) {
                $abstainCorrect++
                $bucket.abstainCorrect++
            }
        }
        else {
            $answerable++
            if ($rank -gt 0) {
                $hits++
                $mrrSum += 1.0 / $rank
                $bucket.hits++
                $bucket.mrrSum += 1.0 / $rank
            }
        }

        $label = [string]$case.query
        if ($label.Length -gt 40) { $label = $label.Substring(0, 40) }
        $marker = '-'
        if ($expectAbstain) { $marker = $(if ($abstained) { 'abstain-ok' } else { 'answered!' }) }
        elseif ($abstained) { $marker = 'abstained' }
        elseif ($rank -gt 0) { $marker = [string]$rank }
        Write-Host ('{0,-42} {1,-8} {2}' -f $label, $marker, $top)

        $rows += [ordered]@{
            query         = [string]$case.query
            category      = $category
            expectAbstain = $expectAbstain
            abstained     = $abstained
            topScore      = $topScore
            rank          = $rank
            topHit        = $top
            expected      = @($case.expectFiles)
            warning       = [string]$response.warning
        }
    }

    $recall = if ($answerable -gt 0) { [math]::Round(($hits / $answerable), 4) } else { 0 }
    $mrr = if ($answerable -gt 0) { [math]::Round(($mrrSum / $answerable), 4) } else { 0 }
    $abstainRate = if ($abstainCases -gt 0) { [math]::Round(($abstainCorrect / $abstainCases), 4) } else { 0 }

    Write-Host ''
    Write-Host ("recall@{0} = {1}  ({2}/{3} answerable)   MRR = {4}   abstain = {5}/{6}   strategy = {7}" -f $Limit, $recall, $hits, $answerable, $mrr, $abstainCorrect, $abstainCases, $strategy)

    $categoryRows = @()
    foreach ($key in $categoryStats.Keys) {
        $bucket = $categoryStats[$key]
        $categoryRows += [pscustomobject]@{
            category      = $bucket.category
            recall        = if ($bucket.cases -gt 0) { [math]::Round($bucket.hits / $bucket.cases, 4) } else { 0 }
            mrr           = if ($bucket.cases -gt 0) { [math]::Round($bucket.mrrSum / $bucket.cases, 4) } else { 0 }
            hits          = $bucket.hits
            cases         = $bucket.cases
            abstainCases  = $bucket.abstainCases
            abstainCorrect = $bucket.abstainCorrect
        }
    }

    if ($categoryRows.Count -gt 0) {
        Write-Host ''
        Write-Host ('per category ({0}):' -f $mode)
        $categoryRows | Format-Table -AutoSize | Out-String | Write-Host
    }

    $summaryLines += [pscustomobject]@{
        mode          = $mode
        strategy      = $strategy
        recall        = $recall
        mrr           = $mrr
        hits          = $hits
        cases         = $answerable
        abstainOk     = $abstainCorrect
        abstainCases  = $abstainCases
    }

    $report.modes += [ordered]@{
        mode       = $mode
        strategy   = $strategy
        recall     = $recall
        mrr        = $mrr
        hits       = $hits
        cases      = $answerable
        abstainOk  = $abstainCorrect
        abstainCases = $abstainCases
        categories = $categoryRows
        rows       = $rows
    }
}

Write-Host ''
Write-Host '=== comparison ==='
$summaryLines | Format-Table -AutoSize | Out-String | Write-Host

# Threshold calibration: only modes that report topScore can be used. For each
# candidate threshold T: an answerable case is "cost" when its topScore < T (it
# would be refused despite being answerable), and a no-answer case is a "win"
# when its topScore < T. Printing the whole sweep is more honest than hard-coding
# one number -- the reader can see how flat or steep the trade-off is.
$scoreRows = @()
foreach ($modeEntry in $report.modes) {
    foreach ($row in $modeEntry.rows) {
        if ($null -eq $row.topScore) { continue }
        if ([double]$row.topScore -le 0) { continue }
        $scoreRows += [pscustomobject]@{
            mode          = $modeEntry.mode
            expectAbstain = [bool]$row.expectAbstain
            topScore      = [double]$row.topScore
        }
    }
}

if ($scoreRows.Count -gt 0) {
    Write-Host ''
    Write-Host '=== abstain threshold sweep (needs a rerank mode) ==='
    Write-Host 'T      answerable-refused   no-answer-refused   (per mode)'

    $sweep = @()
    foreach ($threshold in @(0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70)) {
        foreach ($modeName in @($scoreRows | ForEach-Object { $_.mode } | Select-Object -Unique)) {
            $modeRows = @($scoreRows | Where-Object { $_.mode -eq $modeName })
            $answerableRows = @($modeRows | Where-Object { -not $_.expectAbstain })
            $noAnswerRows = @($modeRows | Where-Object { $_.expectAbstain })
            $refusedAnswerable = @($answerableRows | Where-Object { $_.topScore -lt $threshold }).Count
            $refusedNoAnswer = @($noAnswerRows | Where-Object { $_.topScore -lt $threshold }).Count

            Write-Host ('{0,-6} {1,-20} {2,-19} {3}' -f $threshold, ("{0}/{1}" -f $refusedAnswerable, $answerableRows.Count), ("{0}/{1}" -f $refusedNoAnswer, $noAnswerRows.Count), $modeName)

            $sweep += [ordered]@{
                mode               = $modeName
                threshold          = $threshold
                refusedAnswerable  = $refusedAnswerable
                answerable         = $answerableRows.Count
                refusedNoAnswer    = $refusedNoAnswer
                noAnswer           = $noAnswerRows.Count
            }
        }
    }
    $report.abstainSweep = $sweep
}

$report.finishedAt = (Get-Date).ToString('o')

if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
    $json = $report | ConvertTo-Json -Depth 8
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    [System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("JSON report written to {0}" -f $OutFile)
}
