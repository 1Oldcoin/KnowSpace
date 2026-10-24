# Phase 6 end-to-end: MCP as a guarded core storage layer.
$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:5056'

function Invoke-Json([string]$method, [string]$url, $body, [hashtable]$headers = @{}) {
    $params = @{ Uri = $url; Method = $method; UseBasicParsing = $true; TimeoutSec = 180 }
    if ($headers.Count -gt 0) { $params.Headers = $headers }
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

Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(180)

function Mcp([string]$key, $payload) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$base/mcp")
    $json = $payload | ConvertTo-Json -Depth 10 -Compress
    $req.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json')
    $req.Headers.TryAddWithoutValidation('Accept', 'application/json, text/event-stream') | Out-Null
    $req.Headers.TryAddWithoutValidation('MCP-Protocol-Version', '2025-06-18') | Out-Null
    if ($key) { $req.Headers.TryAddWithoutValidation('X-Api-Key', $key) | Out-Null }
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{ Status = [int]$resp.StatusCode; Text = $text }
}

function McpJson($result) {
    $text = $result.Text
    $dataLines = [regex]::Matches($text, '(?m)^data:\s*(.+)$')
    if ($dataLines.Count -gt 0) { $text = $dataLines[$dataLines.Count - 1].Groups[1].Value }
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text | ConvertFrom-Json
}

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0}  {1}" -f $name, $detail) -ForegroundColor Green }
    else { Write-Host ("FAIL  {0}  {1}" -f $name, $detail) -ForegroundColor Red; $script:failures++ }
}

$script:failures = 0

# ---------- knowledge bases ----------
$existing = (Invoke-Json 'GET' "$base/api/knowledge-bases" $null).Content | ConvertFrom-Json
$kbA = ($existing | Where-Object { $_.name -eq 'MCP-KB-A' } | Select-Object -First 1).id
if (-not $kbA) {
    $kbA = ((Invoke-Json 'POST' "$base/api/knowledge-bases" @{ name = 'MCP-KB-A' }).Content | ConvertFrom-Json).id
}
$kbB = ($existing | Where-Object { $_.name -eq 'MCP-KB-B' } | Select-Object -First 1).id
if (-not $kbB) {
    $kbB = ((Invoke-Json 'POST' "$base/api/knowledge-bases" @{ name = 'MCP-KB-B' }).Content | ConvertFrom-Json).id
}
Write-Host "kbA=$kbA kbB=$kbB"

# ---------- cleanup any earlier test clients ----------
$old = (Invoke-Json 'GET' "$base/api/agent-clients" $null).Content | ConvertFrom-Json
foreach ($c in $old.clients) { Invoke-Json 'DELETE' "$base/api/agent-clients/$($c.id)" $null | Out-Null }

# ---------- create clients ----------
$reader = (Invoke-Json 'POST' "$base/api/agent-clients" @{
    name = 'E2E Reader'
    description = 'read-only, scoped to KB A'
    capabilities = @('knowledge:read')
    allowedKnowledgeBaseIds = @($kbA)
    allowedTools = @()
    requestsPerMinute = 0
}).Content | ConvertFrom-Json
$readerKey = $reader.key
Check 'create-reader' ([bool]$readerKey -and $readerKey.StartsWith('maf_')) ("id=" + $reader.client.id + " prefix=" + $reader.client.keyPrefix)

$writer = (Invoke-Json 'POST' "$base/api/agent-clients" @{
    name = 'E2E Writer'
    capabilities = @('knowledge:read', 'knowledge:write')
    allowedKnowledgeBaseIds = @($kbA)
    requestsPerMinute = 0
}).Content | ConvertFrom-Json
$writerKey = $writer.key

$limited = (Invoke-Json 'POST' "$base/api/agent-clients" @{
    name = 'E2E Limited'
    capabilities = @('knowledge:read')
    requestsPerMinute = 2
}).Content | ConvertFrom-Json
$limitedKey = $limited.key

$forbidden = (Invoke-Json 'POST' "$base/api/agent-clients" @{
    name = 'E2E Forbidden'
    capabilities = @('knowledge:read')
    allowedTools = @('list_documents')
}).Content | ConvertFrom-Json
$forbiddenKey = $forbidden.key

# ---------- 1. auth ----------
$noKey = Mcp $null @{ jsonrpc = '2.0'; id = 1; method = 'tools/list'; params = @{} }
Check 'no-key-401' ($noKey.Status -eq 401) ("status=" + $noKey.Status + " body=" + $noKey.Text)

$badKey = Mcp 'maf_thisisnotarealkey000000000000000000' @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
Check 'bad-key-401' ($badKey.Status -eq 401) ("status=" + $badKey.Status)

# ---------- 2. initialize + tools/list ----------
$init = Mcp $readerKey @{
    jsonrpc = '2.0'; id = 3; method = 'initialize'
    params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'e2e'; version = '1.0.0' } }
}
$initJson = McpJson $init
Check 'initialize' ($init.Status -eq 200 -and $initJson.result.serverInfo.name -eq 'MyFirstMafAgent.Knowledge') ("status=" + $init.Status + " server=" + $initJson.result.serverInfo.name)

$notified = Mcp $readerKey @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} }
Write-Host ("notify status=" + $notified.Status)

$tools = McpJson (Mcp $readerKey @{ jsonrpc = '2.0'; id = 4; method = 'tools/list'; params = @{} })
$toolNames = @($tools.result.tools | ForEach-Object { $_.name })
Check 'tools-list' ($toolNames -contains 'search_knowledge_base' -and $toolNames -contains 'upload_document') ("count=" + $toolNames.Count)

# ---------- 3. read path ----------
$bases = McpJson (Mcp $readerKey @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'list_knowledge_bases'; arguments = @{} } })
$baseIds = @((($bases.result.content[0].text | ConvertFrom-Json)) | ForEach-Object { $_.id })
Check 'kb-list-filtered' ($baseIds.Count -eq 1 -and $baseIds[0] -eq $kbA) ("ids=" + ($baseIds -join ','))

$outOfScope = McpJson (Mcp $readerKey @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'list_documents'; arguments = @{ knowledgeBaseId = $kbB } } })
Check 'out-of-scope-denied' ([bool]$outOfScope.error -and $outOfScope.error.code -eq -32003) ("code=" + $outOfScope.error.code + " msg=" + $outOfScope.error.message)

$inScope = McpJson (Mcp $readerKey @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'list_documents'; arguments = @{ knowledgeBaseId = $kbA } } })
Check 'in-scope-ok' (-not $inScope.error) ("text=" + $inScope.result.content[0].text)

# ---------- 4. write denial for read-only client ----------
$readOnlyWrite = McpJson (Mcp $readerKey @{ jsonrpc = '2.0'; id = 8; method = 'tools/call'; params = @{ name = 'upload_document'; arguments = @{ title = 'blocked'; content = 'nope'; knowledgeBaseId = $kbA } } })
Check 'read-only-write-denied' ([bool]$readOnlyWrite.error -and $readOnlyWrite.error.code -eq -32003) ("code=" + $readOnlyWrite.error.code)

# ---------- 5. write path ----------
$upload = McpJson (Mcp $writerKey @{ jsonrpc = '2.0'; id = 9; method = 'tools/call'; params = @{ name = 'upload_document'; arguments = @{ title = 'MCP storage test'; content = "# MCP storage test`n`nInjected by an external agent through MCP."; knowledgeBaseId = $kbA } } })
$uploadText = $upload.result.content[0].text
$documentId = ($uploadText | ConvertFrom-Json).documentId
Check 'upload-ok' ([bool]$documentId) ("documentId=" + $documentId + " text=" + $uploadText)

$status = McpJson (Mcp $writerKey @{ jsonrpc = '2.0'; id = 10; method = 'tools/call'; params = @{ name = 'get_document_status'; arguments = @{ documentId = $documentId } } })
$statusText = ($status.result.content[0].text | ConvertFrom-Json)
Check 'status-readable' ($statusText.documentId -eq $documentId) ("status=" + $statusText.status + " progress=" + $statusText.progress)

# ---------- 6. tool whitelist ----------
$notWhitelisted = McpJson (Mcp $forbiddenKey @{ jsonrpc = '2.0'; id = 11; method = 'tools/call'; params = @{ name = 'search_knowledge_base'; arguments = @{ query = 'mcp' } } })
Check 'whitelist-denied' ([bool]$notWhitelisted.error -and $notWhitelisted.error.code -eq -32003) ("code=" + $notWhitelisted.error.code)

$whitelisted = McpJson (Mcp $forbiddenKey @{ jsonrpc = '2.0'; id = 12; method = 'tools/call'; params = @{ name = 'list_documents'; arguments = @{} } })
Check 'whitelist-allowed' (-not $whitelisted.error) ("ok")

# ---------- 7. rate limit ----------
$r1 = Mcp $limitedKey @{ jsonrpc = '2.0'; id = 20; method = 'tools/call'; params = @{ name = 'list_knowledge_bases'; arguments = @{} } }
$r2 = Mcp $limitedKey @{ jsonrpc = '2.0'; id = 21; method = 'tools/call'; params = @{ name = 'list_knowledge_bases'; arguments = @{} } }
$r3 = Mcp $limitedKey @{ jsonrpc = '2.0'; id = 22; method = 'tools/call'; params = @{ name = 'list_knowledge_bases'; arguments = @{} } }
Check 'rate-limit-429' ($r3.Status -eq 429) ("statuses=" + $r1.Status + "," + $r2.Status + "," + $r3.Status)

# ---------- 8. rotate + disable ----------
$rotated = (Invoke-Json 'POST' "$base/api/agent-clients/$($reader.client.id)/rotate" $null).Content | ConvertFrom-Json
$afterRotateOld = Mcp $readerKey @{ jsonrpc = '2.0'; id = 30; method = 'tools/list'; params = @{} }
$afterRotateNew = Mcp $rotated.key @{ jsonrpc = '2.0'; id = 31; method = 'tools/list'; params = @{} }
Check 'rotate-invalidates-old' ($afterRotateOld.Status -eq 401 -and $afterRotateNew.Status -eq 200) ("old=" + $afterRotateOld.Status + " new=" + $afterRotateNew.Status)

Invoke-Json 'PUT' "$base/api/agent-clients/$($writer.client.id)" @{ enabled = $false } | Out-Null
$disabled = Mcp $writerKey @{ jsonrpc = '2.0'; id = 32; method = 'tools/list'; params = @{} }
Check 'disabled-403' ($disabled.Status -eq 403) ("status=" + $disabled.Status)

# ---------- 9. audit ----------
$calls = (Invoke-Json 'GET' "$base/api/agent-clients/calls?limit=200" $null).Content | ConvertFrom-Json
$denied = @($calls.calls | Where-Object { $_.outcome -eq 'denied' -and $_.tool -eq 'upload_document' })
Check 'audit-recorded' ($calls.calls.Count -gt 10 -and $denied.Count -ge 1) ("rows=" + $calls.calls.Count + " deniedUpload=" + $denied.Count)

$loggedIn = @($calls.calls | Where-Object { $_.knowledgeBaseId -eq $kbA })
Check 'audit-kb-tagged' ($loggedIn.Count -ge 1) ("kbRows=" + $loggedIn.Count)

Write-Host ''
if ($script:failures -eq 0) { Write-Host 'MCP E2E: all checks passed.' -ForegroundColor Green; exit 0 }
Write-Host ("MCP E2E: {0} check(s) failed." -f $script:failures) -ForegroundColor Red
exit 1
