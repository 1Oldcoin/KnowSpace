namespace MyFirstMafAgent.Api.OAuth;

/// <summary>已注册客户端的来源，用于解释「这个 client_id 是怎么来的」。</summary>
public static class OAuthClientSources
{
    /// <summary>手工预注册（企业内部客户端等）。优先级最高，不查 CIMD。</summary>
    public const string PreRegistered = "preregistered";

    /// <summary>由客户端 URL 元数据文档解析而来（CIMD）。</summary>
    public const string Cimd = "cimd";

    /// <summary>动态客户端注册（DCR）。兜底路径。</summary>
    public const string Dynamic = "dcr";
}

/// <summary>一个 OAuth 客户端。公开客户端（<c>token_endpoint_auth_method=none</c>）没有 secret。</summary>
public sealed record OAuthClientRegistration(
    string ClientId,
    string ClientName,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    string TokenEndpointAuthMethod,
    string Source,
    string? MetadataUri,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool SupportsGrant(string grantType) =>
        GrantTypes.Count == 0 || GrantTypes.Contains(grantType, StringComparer.Ordinal);

    public bool IsPublicClient =>
        string.Equals(TokenEndpointAuthMethod, "none", StringComparison.Ordinal);
}

/// <summary>
/// 一次授权同意的结果。这是<b>业务层授权</b>的唯一真实来源：
/// OAuth 只决定「谁被授权」，具体能碰哪些知识库、哪些能力，全看这一行。
///
/// <para>
/// <see cref="KnowledgeBaseIds"/> 为空表示不限制（与 <c>maf_</c> 客户端的语义保持一致）；
/// 撤销时写 <see cref="RevokedAt"/>，已签发的访问令牌在下一次校验时就失效。
/// </para>
/// </summary>
public sealed record OAuthConsent(
    string Id,
    string Subject,
    string ClientId,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsActive => RevokedAt is null;
}

/// <summary>授权码（落库时只存哈希）。一次性，短命。</summary>
public sealed record OAuthAuthorizationCode(
    string CodeHash,
    string ClientId,
    string Subject,
    string RedirectUri,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    string CodeChallenge,
    string CodeChallengeMethod,
    string Resource,
    string ConsentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt);

/// <summary>刷新令牌（落库时只存哈希）。轮换后旧的立即失效。</summary>
public sealed record OAuthRefreshToken(
    string TokenHash,
    string ClientId,
    string Subject,
    string ConsentId,
    IReadOnlyList<string> Scopes,
    string Resource,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? ReplacedByHash)
{
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>等待用户确认的授权请求（同意页背后的记录）。</summary>
public sealed record OAuthAuthorizationRequest(
    string Id,
    string ClientId,
    string ClientName,
    string RedirectUri,
    string ResponseType,
    string Scope,
    string? State,
    string CodeChallenge,
    string CodeChallengeMethod,
    string Resource,
    string Subject,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Status)
{
    public const string StatusPending = "pending";
    public const string StatusApproved = "approved";
    public const string StatusDenied = "denied";

    public bool IsPending(DateTimeOffset now) =>
        string.Equals(Status, StatusPending, StringComparison.Ordinal) && ExpiresAt > now;
}

/// <summary>认证事件类型。与工具调用审计（<c>agent_client_calls</c>）分开查。</summary>
public static class OAuthAuthEventTypes
{
    public const string Discovery = "discovery";
    public const string CimdFetch = "cimd_fetch";
    public const string ClientRegistered = "client_registered";
    public const string AuthorizationRequested = "authorization_requested";
    public const string ConsentGranted = "consent_granted";
    public const string ConsentDenied = "consent_denied";
    public const string ConsentRevoked = "consent_revoked";
    public const string TokenIssued = "token_issued";
    public const string TokenRefreshed = "token_refreshed";
    public const string TokenRevoked = "token_revoked";
    public const string TokenRejected = "token_rejected";
}

/// <summary>认证事件结果。</summary>
public static class OAuthAuthEventOutcomes
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

/// <summary>
/// 一条认证事件审计。
///
/// <para>
/// <b>绝不记录</b> access token、refresh token、授权码或 client secret 的明文，
/// 只记录事件的标识（前缀/哈希片段）与结果。
/// </para>
/// </summary>
public sealed record OAuthAuthEvent(
    long Id,
    DateTimeOffset OccurredAt,
    string EventType,
    string? ClientId,
    string? Subject,
    string Outcome,
    string? RemoteAddress,
    string? Detail,
    string? Error)
{
    public static OAuthAuthEvent Pending(
        string eventType,
        string? clientId,
        string? subject,
        string outcome,
        string? remoteAddress,
        string? detail,
        string? error) =>
        new(0, DateTimeOffset.UtcNow, eventType, clientId, subject, outcome, remoteAddress, detail, error);
}

/// <summary>OAuth 相关持久化端口。授权服务器状态都落库，重启不丢授权码/刷新令牌。</summary>
public interface IOAuthStore
{
    Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true);

    // ── 客户端 ──
    Task<OAuthClientRegistration?> GetClientAsync(string clientId, CancellationToken cancellationToken);
    Task CreateClientAsync(OAuthClientRegistration client, CancellationToken cancellationToken);
    Task UpdateClientAsync(OAuthClientRegistration client, CancellationToken cancellationToken);
    Task<int> CountClientsAsync(CancellationToken cancellationToken);

    // ── 同意（业务层授权） ──
    Task<OAuthConsent?> GetConsentAsync(string consentId, CancellationToken cancellationToken);
    Task<OAuthConsent?> FindActiveConsentAsync(string subject, string clientId, CancellationToken cancellationToken);
    Task CreateConsentAsync(OAuthConsent consent, CancellationToken cancellationToken);
    Task UpdateConsentAsync(OAuthConsent consent, CancellationToken cancellationToken);
    Task RevokeConsentAsync(string consentId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<OAuthConsent>> ListConsentsAsync(string subject, CancellationToken cancellationToken);

    // ── 待确认的授权请求 ──
    Task CreateAuthorizationRequestAsync(OAuthAuthorizationRequest request, CancellationToken cancellationToken);
    Task<OAuthAuthorizationRequest?> GetAuthorizationRequestAsync(string id, CancellationToken cancellationToken);
    Task UpdateAuthorizationRequestStatusAsync(string id, string status, CancellationToken cancellationToken);

    // ── 授权码 ──
    Task CreateCodeAsync(OAuthAuthorizationCode code, CancellationToken cancellationToken);

    /// <summary>取出并标记已使用；已用/过期返回 null（一次性语义靠数据库条件更新保证）。</summary>
    Task<OAuthAuthorizationCode?> ConsumeCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken);

    // ── 刷新令牌 ──
    Task CreateRefreshTokenAsync(OAuthRefreshToken token, CancellationToken cancellationToken);
    Task<OAuthRefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);
    Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByHash, DateTimeOffset revokedAt, CancellationToken cancellationToken);

    // ── 访问令牌吊销（jti 黑名单，到期自动清理） ──
    Task RevokeJtiAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<bool> IsJtiRevokedAsync(string jti, CancellationToken cancellationToken);

    // ── 认证事件审计 ──
    Task RecordAuthEventAsync(OAuthAuthEvent authEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<OAuthAuthEvent>> QueryAuthEventsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>清理过期的授权码、刷新令牌与 jti 黑名单。</summary>
    Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
