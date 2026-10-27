#requires -Version 5.1
<#
.SYNOPSIS
    端到端验证 MCP 的 OAuth 2.1 流程（DCR → 授权 → 换令牌 → 调用 /mcp）。

.DESCRIPTION
    只用 BCL（System.Net.Http），不依赖任何模块。默认走 DCR（动态注册）路径，
    因此一次就能验证：元数据发现、PKCE(S256)、授权同意、授权码换令牌、受众绑定、
    以及 /mcp 的 Bearer 校验。

.PARAMETER BaseUrl
    服务基地址，必须与 Mcp:OAuth:Issuer 完全一致（受众绑定校验会比对）。

.PARAMETER KnowledgeBaseId
    授权范围内只勾一个知识库；留空表示「全部知识库」。

.PARAMETER ClientId
    复用已注册客户端（例如 CIMD 地址或预注册 ID）；留空则走 /oauth/register。

.PARAMETER Scope
    请求的 scope，默认 knowspace:read（别名会规范化为 knowledge:read）。

.EXAMPLE
    powershell -NoProfile -File scripts\Test-McpOAuth.ps1 -BaseUrl http://127.0.0.1:5057
#>
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$KnowledgeBaseId = '',
    [string]$ClientId = '',
    [string]$Scope = 'knowspace:read',
    [string]$RedirectUri = 'http://127.0.0.1:5199/cb',
    [switch]$SkipMcpCall,
    [switch]$VerifyRevocation
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$BaseUrl = $BaseUrl.TrimEnd('/')
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)

function Write-Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }

function Fail([string]$text) {
    Write-Host "FAIL: $text" -ForegroundColor Red
    exit 1
}

# ── 1. 元数据发现 ──
Write-Step '1/6 受保护资源元数据（RFC 9728）'
$pr = $http.GetStringAsync("$BaseUrl/.well-known/oauth-protected-resource").Result
Write-Host $pr
$resource = ($pr | ConvertFrom-Json).resource

Write-Step '2/6 授权服务器元数据（RFC 8414）'
$as = $http.GetStringAsync("$BaseUrl/.well-known/oauth-authorization-server").Result
Write-Host $as
$asMeta = $as | ConvertFrom-Json
if (-not $asMeta.client_id_metadata_document_supported) { Fail 'metadata 未声明 CIMD 支持' }

# ── 2. 客户端 ──
Write-Step '3/6 客户端注册（预注册 → CIMD → DCR）'
if ([string]::IsNullOrWhiteSpace($ClientId)) {
    $dcrBody = @{
        client_name              = 'OAuth smoke test'
        redirect_uris            = @($RedirectUri)
        grant_types              = @('authorization_code', 'refresh_token')
        scope                    = $Scope
        token_endpoint_auth_method = 'none'
    } | ConvertTo-Json -Depth 5

    $content = New-Object System.Net.Http.StringContent($dcrBody, [Text.Encoding]::UTF8, 'application/json')
    $response = $http.PostAsync("$BaseUrl/oauth/register", $content).Result
    $payload = $response.Content.ReadAsStringAsync().Result
    if (-not $response.IsSuccessStatusCode) { Fail "DCR 失败：$payload" }
    $ClientId = ($payload | ConvertFrom-Json).client_id
}
Write-Host "client_id = $ClientId"

# ── 3. PKCE ──
$verifier = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._'
$sha = [System.Security.Cryptography.SHA256]::Create()
$challenge = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))).
    TrimEnd('=').Replace('+', '-').Replace('/', '_')

# ── 4. 授权请求 + 同意 ──
Write-Step '4/6 授权请求与同意'
$query = 'client_id={0}&redirect_uri={1}&response_type=code&scope={2}&state=smoke&code_challenge={3}&code_challenge_method=S256&resource={4}' -f
    [Uri]::EscapeDataString($ClientId),
    [Uri]::EscapeDataString($RedirectUri),
    [Uri]::EscapeDataString($Scope),
    [Uri]::EscapeDataString($challenge),
    [Uri]::EscapeDataString($resource)

$authorizeHtml = $http.GetStringAsync("$BaseUrl/oauth/authorize?$query").Result
$match = [regex]::Match($authorizeHtml, 'name="request_id" value="([^"]+)"')
if (-not $match.Success) { Fail "未拿到同意页（响应：$($authorizeHtml.Substring(0, [Math]::Min(400, $authorizeHtml.Length)))）" }
$requestId = $match.Groups[1].Value

$formPairs = New-Object 'System.Collections.Generic.List[string]'
$formPairs.Add('request_id=' + [Uri]::EscapeDataString($requestId))
$formPairs.Add('decision=approve')
$formPairs.Add('scopes=' + [Uri]::EscapeDataString($Scope))

if (-not [string]::IsNullOrWhiteSpace($KnowledgeBaseId)) {
    $formPairs.Add('knowledgeBaseIds=' + [Uri]::EscapeDataString($KnowledgeBaseId))
}
else {
    # 服务端要求「至少选一个知识库；勾满全部 = 不限范围」。
    # 同意页默认全选，所以照抄页面上的每个知识库 id 即可复现「勾满」的效果。
    # 注意：表单用的是同名多值（knowledgeBaseIds 重复出现），不能拼成逗号分隔的单个值。
    $kbMatches = [regex]::Matches($authorizeHtml, 'name="knowledgeBaseIds" value="([^"]+)"')
    foreach ($kbMatch in $kbMatches) {
        $formPairs.Add('knowledgeBaseIds=' + [Uri]::EscapeDataString($kbMatch.Groups[1].Value))
    }
}

$formBody = $formPairs -join '&'
$approveResponse = $http.PostAsync("$BaseUrl/oauth/authorize",
    (New-Object System.Net.Http.StringContent($formBody, [System.Text.Encoding]::UTF8, 'application/x-www-form-urlencoded'))).Result
$location = $approveResponse.Headers.Location
if ($location -eq $null) {
    Fail "同意失败（HTTP $([int]$approveResponse.StatusCode)）：$($approveResponse.Content.ReadAsStringAsync().Result)"
}

$codeMatch = [regex]::Match($location.Query, 'code=([^&]+)')
if (-not $codeMatch.Success) { Fail "回调里没有 code：$location" }
$code = [Uri]::UnescapeDataString($codeMatch.Groups[1].Value)
Write-Host "authorization code 已获取（$($code.Length) 字符）"

# ── 5. 换令牌 ──
Write-Step '5/6 授权码换令牌'
$tokenForm = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$tokenForm['grant_type'] = 'authorization_code'
$tokenForm['code'] = $code
$tokenForm['redirect_uri'] = $RedirectUri
$tokenForm['client_id'] = $ClientId
$tokenForm['code_verifier'] = $verifier
$tokenForm['resource'] = $resource

$tokenResponse = $http.PostAsync("$BaseUrl/oauth/token",
    (New-Object System.Net.Http.FormUrlEncodedContent($tokenForm))).Result
$tokenBody = $tokenResponse.Content.ReadAsStringAsync().Result
if (-not $tokenResponse.IsSuccessStatusCode) { Fail "换令牌失败：$tokenBody" }

$tokens = $tokenBody | ConvertFrom-Json
Write-Host "token_type=$($tokens.token_type) expires_in=$($tokens.expires_in) scope=$($tokens.scope)"
Write-Host "access_token 前缀 = $($tokens.access_token.Substring(0, [Math]::Min(24, $tokens.access_token.Length)))…"

# ── 6. 调用 /mcp ──
if (-not $SkipMcpCall) {
    Write-Step '6/6 用 Bearer 调用 /mcp (tools/list)'
    $mcpRequest = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $resource)
    $mcpRequest.Headers.TryAddWithoutValidation('Authorization', "Bearer $($tokens.access_token)") | Out-Null
    $mcpRequest.Headers.TryAddWithoutValidation('Accept', 'application/json, text/event-stream') | Out-Null
    $mcpRequest.Content = New-Object System.Net.Http.StringContent(
        '{"jsonrpc":"2.0","id":1,"method":"tools/list"}', [Text.Encoding]::UTF8, 'application/json')

    $mcpResponse = $http.SendAsync($mcpRequest).Result
    $mcpBody = $mcpResponse.Content.ReadAsStringAsync().Result
    Write-Host "HTTP $([int]$mcpResponse.StatusCode)"
    Write-Host $mcpBody.Substring(0, [Math]::Min(600, $mcpBody.Length))
    if (-not $mcpResponse.IsSuccessStatusCode) { Fail '/mcp 调用失败' }
}

# ── 7. 撤销授权后令牌立即失效（业务层闸门与协议层分离的验收） ──
if ($VerifyRevocation) {
    Write-Step '7/7 撤销授权 → 同一个令牌应被立即拒绝'

    $consents = $http.GetStringAsync("$BaseUrl/api/mcp/oauth-consents").Result | ConvertFrom-Json
    $consent = $consents.consents | Where-Object { $_.clientId -eq $ClientId -and $_.revokedAt -eq $null } |
        Select-Object -First 1
    if ($null -eq $consent) { Fail '找不到可撤销的同意记录' }

    $revokeResponse = $http.PostAsync("$BaseUrl/api/mcp/oauth-consents/$($consent.id)/revoke", $null).Result
    if (-not $revokeResponse.IsSuccessStatusCode) { Fail "撤销失败：HTTP $([int]$revokeResponse.StatusCode)" }

    $afterRequest = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $resource)
    $afterRequest.Headers.TryAddWithoutValidation('Authorization', "Bearer $($tokens.access_token)") | Out-Null
    $afterRequest.Headers.TryAddWithoutValidation('Accept', 'application/json, text/event-stream') | Out-Null
    $afterRequest.Content = New-Object System.Net.Http.StringContent(
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}', [Text.Encoding]::UTF8, 'application/json')

    $after = $http.SendAsync($afterRequest).Result
    Write-Host "撤销后 HTTP $([int]$after.StatusCode)"
    if ([int]$after.StatusCode -ne 401) { Fail '撤销后令牌仍然可用：业务层闸门失效' }
}

Write-Host 'OK：OAuth 全流程通过' -ForegroundColor Green

