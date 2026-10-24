#Requires -Version 5.1
<#
.SYNOPSIS
    Seed the retrieval-evaluation corpus into a dedicated knowledge base.

.DESCRIPTION
    Creates (or reuses) a knowledge base named by -KnowledgeBaseName, uploads every
    *.md under scripts\retrieval-eval\corpus\, waits until all of them are Ready, and
    writes the knowledge base id to scripts\retrieval-eval\corpus.kb.txt so that
    Evaluate-Retrieval.ps1 -Fixture ...corpus.json works with no extra arguments.

    Uploads use System.Net.Http.MultipartFormDataContent on purpose: the corpus file
    names are Chinese, and `curl.exe -F "file=@C:\...\<cjk>.md"` would send the name in
    the console code page, which the API decodes as UTF-8 and stores as mojibake.
    .NET writes the part name as UTF-8, so the stored fileName matches the fixture.

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misparses UTF-8 sources
    without a BOM, which silently breaks string literals.

.PARAMETER BaseUrl
    API base URL. Default http://127.0.0.1:5056.

.PARAMETER CorpusDir
    Directory with the .md corpus files. Default scripts\retrieval-eval\corpus.

.PARAMETER KnowledgeBaseName
    Name of the knowledge base to seed. Default "retrieval-eval corpus".

.PARAMETER TimeoutSeconds
    Per-upload HTTP timeout. Default 300.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Seed-RetrievalCorpus.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5056',
    [string]$CorpusDir = '',
    [string]$KnowledgeBaseName = 'retrieval-eval corpus',
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($CorpusDir)) {
    $CorpusDir = Join-Path $PSScriptRoot 'retrieval-eval\corpus'
}
if (-not (Test-Path -LiteralPath $CorpusDir)) {
    throw "Corpus directory not found: $CorpusDir"
}

Add-Type -AssemblyName System.Net.Http

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds([Math]::Max(30, $TimeoutSeconds))

function Send-Json {
    param([string]$Method, [string]$Path, $Payload)

    $json = $Payload | ConvertTo-Json -Depth 6
    $content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json')
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method)), "$BaseUrl$Path")
    $request.Content = $content
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{ status = [int]$response.StatusCode; body = $body }
}

function Get-Json {
    param([string]$Path)

    $response = $client.GetAsync("$BaseUrl$Path").GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{ status = [int]$response.StatusCode; body = $body }
}

# ---- 1. knowledge base -------------------------------------------------------
$knowledgeBaseId = ''
$list = Get-Json -Path '/api/knowledge-bases'
# PowerShell 5.1 gotcha: piping the body into ConvertFrom-Json inside @() yields a
# single element that IS the array, so .name would be an array of every name.
$parsedList = $list.body | ConvertFrom-Json
$existing = @()
if ($parsedList -is [System.Array]) { $existing = $parsedList }
elseif ($null -ne $parsedList) { $existing = @($parsedList) }

foreach ($item in $existing) {
    if ([string]::Equals([string]$item.name, $KnowledgeBaseName, [System.StringComparison]::Ordinal)) {
        $knowledgeBaseId = [string]$item.id
    }
}

if ([string]::IsNullOrWhiteSpace($knowledgeBaseId)) {
    $created = Send-Json -Method Post -Path '/api/knowledge-bases' -Payload ([ordered]@{ name = $KnowledgeBaseName })
    if ($created.status -ne 200) {
        throw "Failed to create knowledge base ($($created.status)): $($created.body)"
    }
    $knowledgeBaseId = [string](($created.body | ConvertFrom-Json).id)
    Write-Host "Created knowledge base '$KnowledgeBaseName' -> $knowledgeBaseId"
}
else {
    Write-Host "Reusing knowledge base '$KnowledgeBaseName' -> $knowledgeBaseId"
}

# ---- 2. upload ---------------------------------------------------------------
$files = Get-ChildItem -LiteralPath $CorpusDir -Filter '*.md' -File | Sort-Object Name
if ($files.Count -eq 0) { throw "No .md files under $CorpusDir" }

Write-Host ("Uploading {0} file(s) from {1}" -f $files.Count, $CorpusDir)

# Already-present documents are skipped by file name, so re-running the script after a
# fix does not silently double the corpus (which would fake the retrieval metrics).
$current = Get-Json -Path "/api/documents?knowledgeBaseId=$knowledgeBaseId"
$parsedCurrent = $current.body | ConvertFrom-Json
$present = @()
if ($parsedCurrent -is [System.Array]) { $present = $parsedCurrent }
elseif ($null -ne $parsedCurrent) { $present = @($parsedCurrent) }
$readyNames = @{}
foreach ($document in $present) {
    if ($document.status -eq 'Ready') { $readyNames[[string]$document.fileName] = $true }
}

$documentIds = @{}
foreach ($file in $files) {
    if ($readyNames.ContainsKey($file.Name)) {
        Write-Host ("  skip {0} (already Ready)" -f $file.Name)
        continue
    }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $form = New-Object System.Net.Http.MultipartFormDataContent
    $part = New-Object System.Net.Http.ByteArrayContent(, $bytes)
    $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/markdown')
    $form.Add($part, 'file', $file.Name)

    $url = "$BaseUrl/api/documents?knowledgeBaseId=$knowledgeBaseId"
    $response = $client.PostAsync($url, $form).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $form.Dispose()

    if ([int]$response.StatusCode -ne 200) {
        Write-Warning ("upload failed for {0} ({1}): {2}" -f $file.Name, [int]$response.StatusCode, $body)
        continue
    }

    $document = $body | ConvertFrom-Json
    $documentIds[[string]$document.id] = $file.Name
    Write-Host ("  queued {0}" -f $file.Name)
}

# ---- 3. wait for Ready -------------------------------------------------------
$pending = @($documentIds.Keys)
$deadline = (Get-Date).AddSeconds([Math]::Max(120, $TimeoutSeconds))
while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
    $stillPending = @()
    foreach ($id in $pending) {
        $one = Get-Json -Path "/api/documents/$id"
        if ($one.status -ne 200) { $stillPending += $id; continue }
        $document = $one.body | ConvertFrom-Json
        if ($document.status -eq 'Ready') { continue }
        if ($document.status -eq 'Failed') {
            Write-Warning ("document failed: {0} -> {1}" -f $documentIds[$id], $document.error)
            continue
        }
        $stillPending += $id
    }
    $pending = $stillPending
    if ($pending.Count -gt 0) { Start-Sleep -Seconds 3 }
}

if ($pending.Count -gt 0) {
    Write-Warning ("{0} document(s) did not reach Ready before the deadline" -f $pending.Count)
}

# ---- 4. persist the id for the eval harness ----------------------------------
$idFile = Join-Path (Split-Path -Parent $CorpusDir) 'corpus.kb.txt'
[System.IO.File]::WriteAllText($idFile, $knowledgeBaseId, (New-Object System.Text.UTF8Encoding($false)))

$ready = 0
$all = Get-Json -Path "/api/documents?knowledgeBaseId=$knowledgeBaseId"
$parsedAll = $all.body | ConvertFrom-Json
$documents = @()
if ($parsedAll -is [System.Array]) { $documents = $parsedAll }
elseif ($null -ne $parsedAll) { $documents = @($parsedAll) }
foreach ($document in $documents) {
    if ($document.status -eq 'Ready') { $ready++ }
}

Write-Host ''
Write-Host ("Knowledge base {0}: {1} Ready document(s)" -f $knowledgeBaseId, $ready)
Write-Host ("Id written to {0}" -f $idFile)
Write-Host ''
Write-Host 'Next:'
Write-Host ('  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -Fixture scripts\retrieval-eval\corpus.json -Modes vector,lexical,hybrid -OutFile C:\Temp\eval-corpus.json')
