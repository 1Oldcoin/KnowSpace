using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.OAuth;

/// <summary>令牌端点响应（RFC 6749 §5.1）。</summary>
public sealed record OAuthTokenResponse(
    string AccessToken,
    int ExpiresIn,
    string Scope,
    string? RefreshToken,
    string TokenType = "Bearer");

/// <summary>动态客户端注册请求（RFC 7591 的子集）。</summary>
public sealed record DynamicClientRegistrationRequest(
    string? ClientName,
    IReadOnlyList<string>? RedirectUris,
    IReadOnlyList<string>? GrantTypes,
    string? Scope,
    string? TokenEndpointAuthMethod);

/// <summary>授权请求（<c>/oauth/authorize</c> 的查询参数）。</summary>
public sealed record AuthorizeBeginRequest(
    string? ClientId,
    string? RedirectUri,
    string? ResponseType,
    string? Scope,
    string? State,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? Resource);

/// <summary>
/// OAuth 2.1 授权服务器 + 资源服务器校验的用例层。
///
/// <para>
/// 客户端注册顺序严格按「预注册 → CIMD → DCR → 手动输入」：
/// <see cref="EnsureClientAsync"/> 先查库（预注册与已缓存的 CIMD），
/// 库里没有且 <c>client_id</c> 是 HTTPS URL 时才去拉 CIMD 文档；
/// DCR 是独立端点，只作为不支持 CIMD 的客户端的兜底。
/// </para>
///
/// <para>
/// 每一次安全判定都留认证事件审计；<b>任何凭据明文都不落库、不进日志</b>，
/// 只记录事件类型、客户端、结果与原因。
/// </para>
/// </summary>
public sealed class OAuthService(
    IOAuthStore store,
    OAuthTokenService tokens,
    CimdClientMetadataResolver cimd,
    IKnowledgeBaseDirectory knowledgeBases,
    IOptions<McpOAuthOptions> options,
    ILogger<OAuthService> logger)
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private readonly McpOAuthOptions _options = options.Value;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    // ───────────────────────── 客户端 ─────────────────────────

    /// <summary>
    /// 解析 <c>client_id</c>：库里已有就直接用；没有则尝试 CIMD。
    /// 返回 null 表示「不认识的客户端」——调用方应回 <c>invalid_client</c>。
    /// </summary>
    public async Task<OAuthClientRegistration?> EnsureClientAsync(
        string? clientId,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var existing = await store.GetClientAsync(clientId, cancellationToken);
        if (existing is not null)
        {
            var isCimd = string.Equals(existing.Source, OAuthClientSources.Cimd, StringComparison.Ordinal);
            var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _options.Cimd.CacheMinutes));
            if (!isCimd || DateTimeOffset.UtcNow - existing.UpdatedAt < staleAfter)
                return existing;

            // CIMD 缓存过期：重新拉一次，让客户端能改自己的回调地址。
            var refreshed = await cimd.ResolveAsync(clientId, cancellationToken);
            if (refreshed.Success && refreshed.Client is not null)
            {
                var updated = refreshed.Client with { CreatedAt = existing.CreatedAt };
                await store.UpdateClientAsync(updated, cancellationToken);
                await RecordAsync(OAuthAuthEventTypes.CimdFetch, clientId, null,
                    OAuthAuthEventOutcomes.Success, remoteAddress,
                    $"客户端名称：{updated.ClientName}；来源：CIMD（刷新）", null);
                return updated;
            }

            // 拉取失败时保留旧记录（客户端站点短暂不可用不应导致已授权会话失效），但要留痕。
            await RecordAsync(OAuthAuthEventTypes.CimdFetch, clientId, null,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "CIMD 刷新失败，沿用缓存", refreshed.Error);
            return existing;
        }

        // 库里没有 → 只有「像一个元数据文档地址」才走 CIMD。普通字符串 client_id 直接拒绝。
        if (!CimdClientMetadataResolver.IsAllowedClientIdUrl(clientId, _options.Cimd.AllowInsecureHttp))
            return null;

        var registeredCount = await store.CountClientsAsync(cancellationToken);
        if (registeredCount >= _options.MaxRegisteredClients)
        {
            await RecordAsync(OAuthAuthEventTypes.CimdFetch, clientId, null,
                OAuthAuthEventOutcomes.Denied, remoteAddress, "客户端数量已达上限", null);
            return null;
        }

        var result = await cimd.ResolveAsync(clientId, cancellationToken);
        if (!result.Success || result.Client is null)
        {
            await RecordAsync(OAuthAuthEventTypes.CimdFetch, clientId, null,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "CIMD 解析失败", result.Error);
            return null;
        }

        await store.CreateClientAsync(result.Client, cancellationToken);
        await RecordAsync(OAuthAuthEventTypes.CimdFetch, clientId, null,
            OAuthAuthEventOutcomes.Success, remoteAddress,
            $"客户端名称：{result.Client.ClientName}；来源：CIMD", null);

        return result.Client;
    }

    /// <summary>动态客户端注册（兜底路径）。</summary>
    public async Task<(OAuthClientRegistration? Client, OAuthError? Error)> RegisterClientAsync(
        DynamicClientRegistrationRequest request,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (!_options.AllowDynamicRegistration)
            return (null, new OAuthError(OAuthError.InvalidRequest, "动态客户端注册已关闭，请使用 CIMD 或预注册客户端。"));

        var redirectUris = request.RedirectUris?.Where(OAuthProtocol.IsValidRedirectUri).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (redirectUris.Count == 0)
            return (null, new OAuthError("invalid_redirect_uri", "至少需要一个合法的 redirect_uri（绝对地址；非回环必须是 HTTPS）。"));

        if (redirectUris.Count > 10)
            return (null, new OAuthError("invalid_redirect_uri", "redirect_uri 数量超过上限（10）。"));

        var grantTypes = request.GrantTypes?.Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (grantTypes.Count == 0) grantTypes = ["authorization_code", "refresh_token"];
        if (grantTypes.Any(type => type is not ("authorization_code" or "refresh_token")))
            return (null, new OAuthError("invalid_client_metadata", "只支持 authorization_code 与 refresh_token 两种授权类型。"));

        if (!string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod)
            && !string.Equals(request.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
        {
            return (null, new OAuthError("invalid_client_metadata", "仅支持公开客户端（token_endpoint_auth_method=none）。"));
        }

        var requestedScopes = OAuthScopes.Parse(request.Scope);
        if (requestedScopes.Count == 0) requestedScopes = [OAuthScopes.DefaultScope];

        var unknown = OAuthScopes.ParseUnknown(request.Scope);
        if (unknown.Count > 0)
            return (null, OAuthError.InvalidScopeError($"不支持的 scope：{string.Join(' ', unknown)}"));

        if (await store.CountClientsAsync(cancellationToken) >= _options.MaxRegisteredClients)
            return (null, new OAuthError("invalid_client_metadata", "客户端数量已达上限。"));

        var now = DateTimeOffset.UtcNow;
        var client = new OAuthClientRegistration(
            "dcr_" + NewSecret(16),
            string.IsNullOrWhiteSpace(request.ClientName) ? "未命名客户端" : Trim(request.ClientName!, 200),
            redirectUris,
            grantTypes,
            OAuthScopes.Sort(requestedScopes),
            "none",
            OAuthClientSources.Dynamic,
            null,
            now,
            now);

        await store.CreateClientAsync(client, cancellationToken);
        await RecordAsync(OAuthAuthEventTypes.ClientRegistered, client.ClientId, null,
            OAuthAuthEventOutcomes.Success, remoteAddress,
            $"来源：DCR；名称：{client.ClientName}；回调：{string.Join(", ", redirectUris)}", null);

        return (client, null);
    }

    // ───────────────────────── 授权请求与同意 ─────────────────────────

    /// <summary>校验授权请求并落一条待确认记录。任何一项不合法都不进入同意页。</summary>
    public async Task<(OAuthAuthorizationRequest? Request, OAuthError? Error)> BeginAuthorizationAsync(
        AuthorizeBeginRequest request,
        OAuthServerContext context,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ResponseType, "code", StringComparison.Ordinal))
            return (null, new OAuthError(OAuthError.UnsupportedResponseType, "只支持 response_type=code。"));

        if (string.IsNullOrWhiteSpace(request.RedirectUri) || !OAuthProtocol.IsValidRedirectUri(request.RedirectUri))
            return (null, OAuthError.InvalidRequestError("redirect_uri 非法。"));

        var client = await EnsureClientAsync(request.ClientId, remoteAddress, cancellationToken);
        if (client is null)
            return (null, OAuthError.InvalidClientError("未知的 client_id。"));

        if (!OAuthProtocol.RedirectUriMatches(client.RedirectUris, request.RedirectUri))
            return (null, OAuthError.InvalidRequestError("redirect_uri 与注册值不一致。"));

        if (!client.SupportsGrant("authorization_code"))
            return (null, new OAuthError(OAuthError.UnauthorizedClient, "该客户端未被允许使用授权码模式。"));

        // PKCE：OAuth 2.1 对公开客户端强制，且只接受 S256。
        if (string.IsNullOrWhiteSpace(request.CodeChallenge))
            return (null, OAuthError.InvalidRequestError("缺少 code_challenge：公开客户端必须使用 PKCE（S256）。"));

        if (!string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            return (null, new OAuthError(OAuthError.InvalidRequest, "code_challenge_method 必须是 S256。"));

        // RFC 8707：授权请求必须带 resource，且必须就是本资源。
        if (string.IsNullOrWhiteSpace(request.Resource))
            return (null, OAuthError.InvalidTargetError("缺少 resource 参数（RFC 8707 受众绑定是强制要求）。"));

        if (!string.Equals(request.Resource.Trim().TrimEnd('/'), context.ResourceString, StringComparison.Ordinal))
            return (null, OAuthError.InvalidTargetError($"resource 必须等于 {context.ResourceString}。"));

        var requestedScopes = OAuthScopes.Parse(request.Scope);
        if (requestedScopes.Count == 0) requestedScopes = [OAuthScopes.DefaultScope];

        var unknownScopes = OAuthScopes.ParseUnknown(request.Scope);
        if (unknownScopes.Count > 0)
            return (null, OAuthError.InvalidScopeError($"不支持的 scope：{string.Join(' ', unknownScopes)}"));

        if (client.Scopes.Count > 0)
        {
            var notAllowed = requestedScopes
                .Where(scope => !client.Scopes.Contains(scope, StringComparer.Ordinal))
                .ToList();

            if (notAllowed.Count > 0)
                return (null, OAuthError.InvalidScopeError($"客户端未被允许请求：{string.Join(' ', notAllowed)}"));
        }

        var now = DateTimeOffset.UtcNow;
        var pending = new OAuthAuthorizationRequest(
            NewSecret(24),
            client.ClientId,
            client.ClientName,
            request.RedirectUri,
            "code",
            string.Join(' ', OAuthScopes.Sort(requestedScopes)),
            request.State,
            request.CodeChallenge!,
            "S256",
            context.ResourceString,
            _options.OwnerSubject,
            now,
            now.AddSeconds(Math.Clamp(_options.AuthorizationRequestLifetimeSeconds, 60, 3600)),
            OAuthAuthorizationRequest.StatusPending);

        await store.CreateAuthorizationRequestAsync(pending, cancellationToken);
        await PurgeOccasionallyAsync(now, cancellationToken);
        await RecordAsync(OAuthAuthEventTypes.AuthorizationRequested, client.ClientId, _options.OwnerSubject,
            OAuthAuthEventOutcomes.Success, remoteAddress,
            $"scope：{pending.Scope}", null);

        return (pending, null);
    }

    /// <summary>
    /// 用户点「同意」：写入/更新同意记录，生成授权码。
    /// 返回的 <c>code</c> 只在这一刻存在明文。
    /// </summary>
    public async Task<(string? Code, OAuthAuthorizationRequest? Request, OAuthError? Error)> ApproveAsync(
        string requestId,
        IReadOnlyList<string>? approvedScopes,
        IReadOnlyList<Guid>? knowledgeBaseIds,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await store.GetAuthorizationRequestAsync(requestId, cancellationToken);
        if (pending is null || !pending.IsPending(now))
            return (null, null, OAuthError.InvalidRequestError("授权请求不存在或已过期。"));

        var requested = OAuthScopes.Parse(pending.Scope);
        var approved = OAuthScopes.Sort((approvedScopes ?? requested)
            .Select(OAuthScopes.Canonicalize)
            .Where(scope => scope is not null)
            .Select(scope => scope!)
            .Where(scope => requested.Contains(scope, StringComparer.Ordinal)));

        if (approved.Count == 0)
            return (null, pending, new OAuthError(OAuthError.InvalidScope, "至少需要批准一项 scope。"));

        // 知识库范围：勾满 = 不限制（空数组），这样以后新建的库也自动在范围内。
        var all = await knowledgeBases.ListAsync(cancellationToken);
        var selected = (knowledgeBaseIds ?? []).Distinct().ToList();
        var invalid = selected.Where(id => all.All(kb => kb.Id != id)).ToList();
        if (invalid.Count > 0)
            return (null, pending, OAuthError.InvalidRequestError($"知识库不存在：{string.Join(", ", invalid)}"));

        if (selected.Count == 0 && all.Count > 0)
            return (null, pending, OAuthError.InvalidRequestError(
                "至少选择一个知识库；若要授权全部（含以后新建的库），请把列表里的知识库全部勾选。"));

        var scopeList = selected.Count == all.Count ? [] : selected;

        var existing = await store.FindActiveConsentAsync(pending.Subject, pending.ClientId, cancellationToken);
        OAuthConsent consent;
        if (existing is null)
        {
            consent = new OAuthConsent(
                "oc_" + NewSecret(16),
                pending.Subject,
                pending.ClientId,
                approved,
                scopeList,
                now,
                now,
                null);

            await store.CreateConsentAsync(consent, cancellationToken);
        }
        else
        {
            // 重新同意 = 覆盖旧范围（而不是取并集）：用户缩小权限时必须真的缩小。
            consent = existing with
            {
                Scopes = approved,
                KnowledgeBaseIds = scopeList,
                UpdatedAt = now,
                RevokedAt = null
            };

            await store.UpdateConsentAsync(consent, cancellationToken);
        }

        var code = NewSecret(32);
        await store.CreateCodeAsync(
            new OAuthAuthorizationCode(
                HashSecret(code),
                pending.ClientId,
                pending.Subject,
                pending.RedirectUri,
                approved,
                scopeList,
                pending.CodeChallenge,
                pending.CodeChallengeMethod,
                pending.Resource,
                consent.Id,
                now,
                now.AddSeconds(Math.Clamp(_options.AuthorizationCodeLifetimeSeconds, 30, 900)),
                null),
            cancellationToken);

        await store.UpdateAuthorizationRequestStatusAsync(requestId, OAuthAuthorizationRequest.StatusApproved, cancellationToken);

        await RecordAsync(OAuthAuthEventTypes.ConsentGranted, pending.ClientId, pending.Subject,
            OAuthAuthEventOutcomes.Success, remoteAddress,
            JsonSerializer.Serialize(new
            {
                scopes = approved,
                knowledgeBases = scopeList.Count == 0 ? ["*"] : scopeList.Select(id => id.ToString("D")).ToArray()
            }, AuditJson),
            null);

        return (code, pending, null);
    }

    /// <summary>用户点「拒绝」：不写同意记录。</summary>
    public async Task<(OAuthAuthorizationRequest? Request, OAuthError? Error)> DenyAsync(
        string requestId,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await store.GetAuthorizationRequestAsync(requestId, cancellationToken);
        if (pending is null || !pending.IsPending(now))
            return (null, OAuthError.InvalidRequestError("授权请求不存在或已过期。"));

        await store.UpdateAuthorizationRequestStatusAsync(requestId, OAuthAuthorizationRequest.StatusDenied, cancellationToken);

        await RecordAsync(OAuthAuthEventTypes.ConsentDenied, pending.ClientId, pending.Subject,
            OAuthAuthEventOutcomes.Denied, remoteAddress, $"scope：{pending.Scope}", null);

        return (pending, null);
    }

    /// <summary>撤销某个同意（管理端调用）。撤销后已签发的令牌立即失效。</summary>
    public async Task<bool> RevokeConsentAsync(string consentId, string? remoteAddress, CancellationToken cancellationToken)
    {
        var consent = await store.GetConsentAsync(consentId, cancellationToken);
        if (consent is null || !consent.IsActive) return false;

        var now = DateTimeOffset.UtcNow;
        await store.RevokeConsentAsync(consentId, now, cancellationToken);

        await RecordAsync(OAuthAuthEventTypes.ConsentRevoked, consent.ClientId, consent.Subject,
            OAuthAuthEventOutcomes.Success, remoteAddress, $"consent：{consentId}", null);

        return true;
    }

    // ───────────────────────── 令牌 ─────────────────────────

    /// <summary>授权码换令牌。</summary>
    public async Task<(OAuthTokenResponse? Tokens, OAuthError? Error)> ExchangeCodeAsync(
        string? code,
        string? clientId,
        string? redirectUri,
        string? codeVerifier,
        string? resource,
        OAuthServerContext context,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(clientId))
            return (null, OAuthError.InvalidRequestError("缺少 code 或 client_id。"));

        if (string.IsNullOrWhiteSpace(resource))
            return (null, OAuthError.InvalidTargetError("缺少 resource 参数（RFC 8707）。"));

        if (!string.Equals(resource.Trim().TrimEnd('/'), context.ResourceString, StringComparison.Ordinal))
            return (null, OAuthError.InvalidTargetError($"resource 必须等于 {context.ResourceString}。"));

        var now = DateTimeOffset.UtcNow;
        var stored = await store.ConsumeCodeAsync(HashSecret(code), now, cancellationToken);
        if (stored is null)
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, clientId, null,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "授权码无效、已使用或已过期", null);
            return (null, OAuthError.InvalidGrantError("授权码无效、已使用或已过期。"));
        }

        if (!string.Equals(stored.ClientId, clientId, StringComparison.Ordinal))
            return (null, OAuthError.InvalidGrantError("授权码不属于该客户端。"));

        if (!OAuthProtocol.RedirectUriMatches([stored.RedirectUri], redirectUri))
            return (null, OAuthError.InvalidGrantError("redirect_uri 与授权时不一致。"));

        if (!OAuthProtocol.VerifyCodeChallenge(codeVerifier, stored.CodeChallenge, stored.CodeChallengeMethod))
            return (null, OAuthError.InvalidGrantError("PKCE 校验失败。"));

        if (!string.Equals(stored.Resource.TrimEnd('/'), context.ResourceString, StringComparison.Ordinal))
            return (null, OAuthError.InvalidTargetError("授权码对应的资源与本次请求不一致。"));

        var consent = await store.GetConsentAsync(stored.ConsentId, cancellationToken);
        if (consent is null || !consent.IsActive)
            return (null, OAuthError.InvalidGrantError("授权已被撤销，请重新授权。"));

        var issued = await IssueTokensAsync(
            stored.ClientId, stored.Subject, consent.Id, stored.Scopes, context, cancellationToken);

        await RecordAsync(OAuthAuthEventTypes.TokenIssued, stored.ClientId, stored.Subject,
            OAuthAuthEventOutcomes.Success, remoteAddress, $"scope：{string.Join(' ', stored.Scopes)}", null);

        return (issued.Response, null);
    }

    /// <summary>刷新令牌（轮换）。</summary>
    public async Task<(OAuthTokenResponse? Tokens, OAuthError? Error)> RefreshAsync(
        string? refreshToken,
        string? clientId,
        string? scope,
        string? resource,
        OAuthServerContext context,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId))
            return (null, OAuthError.InvalidRequestError("缺少 refresh_token 或 client_id。"));

        if (string.IsNullOrWhiteSpace(resource))
            return (null, OAuthError.InvalidTargetError("缺少 resource 参数（RFC 8707）。"));

        if (!string.Equals(resource.Trim().TrimEnd('/'), context.ResourceString, StringComparison.Ordinal))
            return (null, OAuthError.InvalidTargetError($"resource 必须等于 {context.ResourceString}。"));

        var now = DateTimeOffset.UtcNow;
        var stored = await store.GetRefreshTokenAsync(HashSecret(refreshToken), cancellationToken);
        if (stored is null || !stored.IsActive(now))
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, clientId, null,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "刷新令牌无效、已撤销或已过期", null);
            return (null, OAuthError.InvalidGrantError("刷新令牌无效、已撤销或已过期。"));
        }

        if (!string.Equals(stored.ClientId, clientId, StringComparison.Ordinal))
            return (null, OAuthError.InvalidGrantError("刷新令牌不属于该客户端。"));

        var consent = await store.GetConsentAsync(stored.ConsentId, cancellationToken);
        if (consent is null || !consent.IsActive)
        {
            await store.RevokeRefreshTokenAsync(stored.TokenHash, null, now, cancellationToken);
            return (null, OAuthError.InvalidGrantError("授权已被撤销，请重新授权。"));
        }

        // 现在同意记录里的 scope 是权威：用户在同意页缩小了权限，旧令牌也不能超范围。
        var effectiveScopes = OAuthScopes.Parse(scope);
        if (effectiveScopes.Count == 0) effectiveScopes = stored.Scopes;

        var beyond = effectiveScopes
            .Where(requested => !consent.Scopes.Contains(requested, StringComparer.Ordinal))
            .ToList();

        if (beyond.Count > 0)
            return (null, OAuthError.InvalidScopeError($"超出授权范围的 scope：{string.Join(' ', beyond)}"));

        var issued = await IssueTokensAsync(
            stored.ClientId, stored.Subject, consent.Id, effectiveScopes, context, cancellationToken);

        // 轮换：新令牌写成功之后再吊销旧的，并把「换成了谁」记下来便于排查。
        await store.RevokeRefreshTokenAsync(stored.TokenHash, issued.RefreshTokenHash, now, cancellationToken);

        await RecordAsync(OAuthAuthEventTypes.TokenRefreshed, stored.ClientId, stored.Subject,
            OAuthAuthEventOutcomes.Success, remoteAddress, $"scope：{string.Join(' ', effectiveScopes)}", null);

        return (issued.Response, null);
    }

    /// <summary>撤销令牌（RFC 7009）。刷新令牌直接吊销；访问令牌进 jti 黑名单。</summary>
    public async Task RevokeAsync(
        string? token,
        string? clientId,
        OAuthServerContext context,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var now = DateTimeOffset.UtcNow;
        var refresh = await store.GetRefreshTokenAsync(HashSecret(token), cancellationToken);
        if (refresh is not null)
        {
            if (clientId is not null && !string.Equals(refresh.ClientId, clientId, StringComparison.Ordinal)) return;

            await store.RevokeRefreshTokenAsync(refresh.TokenHash, null, now, cancellationToken);
            await RecordAsync(OAuthAuthEventTypes.TokenRevoked, refresh.ClientId, refresh.Subject,
                OAuthAuthEventOutcomes.Success, remoteAddress, "吊销刷新令牌", null);
            return;
        }

        if (tokens.TryValidate(token, context.IssuerString, context.ResourceString, now,
                out var accessToken, out _) && accessToken is not null)
        {
            await store.RevokeJtiAsync(accessToken.Jti, accessToken.ExpiresAt, cancellationToken);
            await RecordAsync(OAuthAuthEventTypes.TokenRevoked, accessToken.ClientId, accessToken.Subject,
                OAuthAuthEventOutcomes.Success, remoteAddress, "吊销访问令牌（jti 黑名单）", null);
        }
    }

    /// <summary>
    /// <c>/mcp</c> 的 Bearer 校验：签名 + 受众 + 有效期 + 同意记录仍然有效。
    /// 返回 null 表示拒绝（调用方回 401 并给出 <c>resource_metadata</c>）。
    /// </summary>
    public async Task<OAuthPrincipal?> AuthenticateBearerAsync(
        string? token,
        OAuthServerContext context,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!tokens.TryValidate(token, context.IssuerString, context.ResourceString, now,
                out var accessToken, out var failure) || accessToken is null)
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, null, null,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "访问令牌校验失败", failure.ToString());

            logger.LogWarning("Rejected MCP bearer token: {Failure}", failure);
            return null;
        }

        if (await store.IsJtiRevokedAsync(accessToken.Jti, cancellationToken))
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, accessToken.ClientId, accessToken.Subject,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "令牌已吊销", null);
            return null;
        }

        if (string.IsNullOrEmpty(accessToken.ConsentId))
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, accessToken.ClientId, accessToken.Subject,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "令牌缺少 consent_id", null);
            return null;
        }

        var consent = await store.GetConsentAsync(accessToken.ConsentId, cancellationToken);
        if (consent is null || !consent.IsActive
            || !string.Equals(consent.ClientId, accessToken.ClientId, StringComparison.Ordinal)
            || !string.Equals(consent.Subject, accessToken.Subject, StringComparison.Ordinal))
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, accessToken.ClientId, accessToken.Subject,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "授权已撤销或主体不一致", null);
            return null;
        }

        var client = await store.GetClientAsync(accessToken.ClientId, cancellationToken);

        // scope 以同意记录为准取交集：同意页缩小范围后，旧令牌不能继续用超范围的能力。
        var effectiveScopes = accessToken.Scopes
            .Where(scope => consent.Scopes.Contains(scope, StringComparer.Ordinal))
            .ToList();

        if (effectiveScopes.Count == 0)
        {
            await RecordAsync(OAuthAuthEventTypes.TokenRejected, accessToken.ClientId, accessToken.Subject,
                OAuthAuthEventOutcomes.Failed, remoteAddress, "令牌 scope 已不在授权范围内", null);
            return null;
        }

        return new OAuthPrincipal(
            accessToken.ClientId,
            client?.ClientName ?? accessToken.ClientId,
            accessToken.Subject,
            consent.Id,
            effectiveScopes,
            consent.KnowledgeBaseIds,
            accessToken.ExpiresAt,
            accessToken.Jti);
    }

    // ───────────────────────── 内部 ─────────────────────────

    private sealed record IssuedTokens(OAuthTokenResponse Response, string RefreshTokenHash);

    private async Task<IssuedTokens> IssueTokensAsync(
        string clientId,
        string subject,
        string consentId,
        IReadOnlyList<string> scopes,
        OAuthServerContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var accessToken = tokens.CreateAccessToken(
            new OAuthAccessTokenRequest(subject, clientId, consentId, scopes, context.ResourceString, context.IssuerString),
            now);

        var refreshToken = NewSecret(32);
        var refreshHash = HashSecret(refreshToken);

        await store.CreateRefreshTokenAsync(
            new OAuthRefreshToken(
                refreshHash,
                clientId,
                subject,
                consentId,
                scopes,
                context.ResourceString,
                now,
                now.AddDays(Math.Max(1, _options.RefreshTokenLifetimeDays)),
                null,
                null),
            cancellationToken);

        var response = new OAuthTokenResponse(
            accessToken,
            Math.Max(1, _options.AccessTokenLifetimeMinutes) * 60,
            string.Join(' ', scopes),
            refreshToken);

        return new IssuedTokens(response, refreshHash);
    }

    private async Task PurgeOccasionallyAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now - _lastPurge < TimeSpan.FromHours(1)) return;
        _lastPurge = now;

        try
        {
            await store.PurgeExpiredAsync(now, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "OAuth 过期数据清理失败（不影响授权流程）。");
        }
    }

    private async Task RecordAsync(
        string eventType,
        string? clientId,
        string? subject,
        string outcome,
        string? remoteAddress,
        string? detail,
        string? error)
    {
        try
        {
            await store.RecordAuthEventAsync(
                OAuthAuthEvent.Pending(eventType, clientId, subject, outcome, remoteAddress, detail, error),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 审计是旁路：写失败只丢这条记录。
            logger.LogDebug(exception, "写认证事件失败：{EventType}", eventType);
        }
    }

    private static string NewSecret(int bytes) =>
        OAuthProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    internal static string HashSecret(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
