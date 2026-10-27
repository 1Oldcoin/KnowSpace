using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// OAuth 客户端。
///
/// <para>
/// 三种来源共用一张表：预注册、CIMD 解析结果（可缓存）、DCR 注册。
/// 公开客户端不存 secret——存了也只是给自己制造泄露面。
/// </para>
/// </summary>
[SugarTable("oauth_clients")]
public sealed class OAuthClientSchemaEntity
{
    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_name", ColumnDataType = "text", IsNullable = false)]
    public string ClientName { get; set; } = string.Empty;

    /// <summary>回调地址（JSON 数组）。换码时必须与授权时完全一致。</summary>
    [SugarColumn(ColumnName = "redirect_uris", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string RedirectUris { get; set; } = "[]";

    [SugarColumn(ColumnName = "grant_types", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string GrantTypes { get; set; } = "[]";

    /// <summary>客户端被允许请求的 scope（JSON 数组）。</summary>
    [SugarColumn(ColumnName = "scopes", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Scopes { get; set; } = "[]";

    [SugarColumn(ColumnName = "token_endpoint_auth_method", ColumnDataType = "text", IsNullable = false, DefaultValue = "'none'")]
    public string TokenEndpointAuthMethod { get; set; } = "none";

    /// <summary><c>preregistered</c> / <c>cimd</c> / <c>dcr</c>。</summary>
    [SugarColumn(ColumnName = "source", ColumnDataType = "text", IsNullable = false, DefaultValue = "'dcr'")]
    public string Source { get; set; } = "dcr";

    /// <summary>CIMD 的元数据文档地址（= client_id）。</summary>
    [SugarColumn(ColumnName = "metadata_uri", ColumnDataType = "text", IsNullable = true)]
    public string? MetadataUri { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 授权同意：用户 → 客户端 → 知识库范围。
///
/// <para>
/// 这张表是「业务层授权」的落地，OAuth 的 scope 只是它的输入之一。
/// 撤销后已签发的访问令牌在下一次请求校验时就失效——不需要等 token 过期。
/// </para>
/// </summary>
[SugarTable("oauth_consents")]
[SugarIndex("ix_oauth_consents_subject_client", nameof(Subject), OrderByType.Asc, nameof(ClientId), OrderByType.Asc, false)]
public sealed class OAuthConsentSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "subject", ColumnDataType = "text", IsNullable = false)]
    public string Subject { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "scopes", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Scopes { get; set; } = "[]";

    /// <summary>允许访问的知识库 ID（JSON 数组）。空数组 = 不限制。</summary>
    [SugarColumn(ColumnName = "knowledge_base_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string KnowledgeBaseIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    [SugarColumn(ColumnName = "revoked_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>等待用户确认的授权请求。POST 回执只引用它的不可猜 ID（兼作 CSRF 令牌）。</summary>
[SugarTable("oauth_authorization_requests")]
public sealed class OAuthAuthorizationRequestSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_name", ColumnDataType = "text", IsNullable = false)]
    public string ClientName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "redirect_uri", ColumnDataType = "text", IsNullable = false)]
    public string RedirectUri { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "response_type", ColumnDataType = "text", IsNullable = false)]
    public string ResponseType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "scope", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Scope { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "state", ColumnDataType = "text", IsNullable = true)]
    public string? State { get; set; }

    [SugarColumn(ColumnName = "code_challenge", ColumnDataType = "text", IsNullable = false)]
    public string CodeChallenge { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "code_challenge_method", ColumnDataType = "text", IsNullable = false, DefaultValue = "'S256'")]
    public string CodeChallengeMethod { get; set; } = "S256";

    [SugarColumn(ColumnName = "resource", ColumnDataType = "text", IsNullable = false)]
    public string Resource { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "subject", ColumnDataType = "text", IsNullable = false)]
    public string Subject { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "expires_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false, DefaultValue = "'pending'")]
    public string Status { get; set; } = "pending";
}

/// <summary>授权码：只存 SHA-256 哈希，一次性、短命。</summary>
[SugarTable("oauth_authorization_codes")]
public sealed class OAuthAuthorizationCodeSchemaEntity
{
    [SugarColumn(ColumnName = "code_hash", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string CodeHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "subject", ColumnDataType = "text", IsNullable = false)]
    public string Subject { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "redirect_uri", ColumnDataType = "text", IsNullable = false)]
    public string RedirectUri { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "scopes", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Scopes { get; set; } = "[]";

    [SugarColumn(ColumnName = "knowledge_base_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string KnowledgeBaseIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "code_challenge", ColumnDataType = "text", IsNullable = false)]
    public string CodeChallenge { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "code_challenge_method", ColumnDataType = "text", IsNullable = false, DefaultValue = "'S256'")]
    public string CodeChallengeMethod { get; set; } = "S256";

    [SugarColumn(ColumnName = "resource", ColumnDataType = "text", IsNullable = false)]
    public string Resource { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "consent_id", ColumnDataType = "text", IsNullable = false)]
    public string ConsentId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "expires_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "consumed_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>刷新令牌：只存哈希。每次刷新都轮换，旧的写 <c>revoked_at</c>。</summary>
[SugarTable("oauth_refresh_tokens")]
[SugarIndex("ix_oauth_refresh_tokens_client", nameof(ClientId), OrderByType.Asc, false)]
public sealed class OAuthRefreshTokenSchemaEntity
{
    [SugarColumn(ColumnName = "token_hash", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string TokenHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "subject", ColumnDataType = "text", IsNullable = false)]
    public string Subject { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "consent_id", ColumnDataType = "text", IsNullable = false)]
    public string ConsentId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "scopes", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Scopes { get; set; } = "[]";

    [SugarColumn(ColumnName = "resource", ColumnDataType = "text", IsNullable = false)]
    public string Resource { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "expires_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "revoked_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? RevokedAt { get; set; }

    [SugarColumn(ColumnName = "replaced_by_hash", ColumnDataType = "text", IsNullable = true)]
    public string? ReplacedByHash { get; set; }
}

/// <summary>
/// 已吊销的访问令牌 jti。访问令牌是自包含的，撤销只能靠黑名单；
/// 过期后由后台清理，不会无限增长。
/// </summary>
[SugarTable("oauth_revoked_jti")]
public sealed class OAuthRevokedJtiSchemaEntity
{
    [SugarColumn(ColumnName = "jti", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string Jti { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "expires_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "revoked_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset RevokedAt { get; set; }
}

/// <summary>
/// 认证事件审计（与工具调用审计 <c>agent_client_calls</c> 分开）。
/// 记录事件类型与结果，<b>不记录任何凭据明文</b>。
/// </summary>
[SugarTable("oauth_auth_events")]
[SugarIndex("ix_oauth_auth_events_occurred_at", nameof(OccurredAt), OrderByType.Desc, false)]
public sealed class OAuthAuthEventSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset OccurredAt { get; set; }

    [SugarColumn(ColumnName = "event_type", ColumnDataType = "text", IsNullable = false)]
    public string EventType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsNullable = true)]
    public string? ClientId { get; set; }

    [SugarColumn(ColumnName = "subject", ColumnDataType = "text", IsNullable = true)]
    public string? Subject { get; set; }

    /// <summary><c>success</c> / <c>denied</c> / <c>failed</c>。</summary>
    [SugarColumn(ColumnName = "outcome", ColumnDataType = "text", IsNullable = false)]
    public string Outcome { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "remote_address", ColumnDataType = "text", IsNullable = true)]
    public string? RemoteAddress { get; set; }

    /// <summary>事件细节（客户端名称、scope、知识库列表等）。不含凭据。</summary>
    [SugarColumn(ColumnName = "detail", ColumnDataType = "text", IsNullable = true)]
    public string? Detail { get; set; }

    [SugarColumn(ColumnName = "error", ColumnDataType = "text", IsNullable = true)]
    public string? Error { get; set; }
}
