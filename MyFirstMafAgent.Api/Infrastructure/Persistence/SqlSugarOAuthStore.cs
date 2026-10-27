using System.Text.Json;
using MyFirstMafAgent.Api.OAuth;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// OAuth 授权服务器状态的 SqlSugar 实现（七张表）。
///
/// <para>
/// 一致性取舍：授权码与刷新令牌只存哈希，明文只出现在响应体里一次；
/// 解析 JSON 列失败时按「最小权限」处理——能力/范围解析不出来就是空集合，
/// 不会退化成「不限制」。
/// </para>
/// </summary>
public sealed class SqlSugarOAuthStore(SqlSugarScope database) : IOAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<OAuthClientSchemaEntity>();
        database.CodeFirst.InitTables<OAuthConsentSchemaEntity>();
        database.CodeFirst.InitTables<OAuthAuthorizationRequestSchemaEntity>();
        database.CodeFirst.InitTables<OAuthAuthorizationCodeSchemaEntity>();
        database.CodeFirst.InitTables<OAuthRefreshTokenSchemaEntity>();
        database.CodeFirst.InitTables<OAuthRevokedJtiSchemaEntity>();
        database.CodeFirst.InitTables<OAuthAuthEventSchemaEntity>();
        return Task.CompletedTask;
    }

    // ───────────────────────── 客户端 ─────────────────────────

    public async Task<OAuthClientRegistration?> GetClientAsync(string clientId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<OAuthClientSchemaEntity>()
            .Where(item => item.ClientId == clientId)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToClient(row);
    }

    public async Task CreateClientAsync(OAuthClientRegistration client, CancellationToken cancellationToken)
    {
        await database.Insertable(new OAuthClientSchemaEntity
        {
            ClientId = client.ClientId,
            ClientName = client.ClientName,
            RedirectUris = Serialize(client.RedirectUris),
            GrantTypes = Serialize(client.GrantTypes),
            Scopes = Serialize(client.Scopes),
            TokenEndpointAuthMethod = client.TokenEndpointAuthMethod,
            Source = client.Source,
            MetadataUri = client.MetadataUri,
            CreatedAt = client.CreatedAt,
            UpdatedAt = client.UpdatedAt
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateClientAsync(OAuthClientRegistration client, CancellationToken cancellationToken)
    {
        await database.Updateable<OAuthClientSchemaEntity>()
            .SetColumns(item => new OAuthClientSchemaEntity
            {
                ClientName = client.ClientName,
                RedirectUris = Serialize(client.RedirectUris),
                GrantTypes = Serialize(client.GrantTypes),
                Scopes = Serialize(client.Scopes),
                TokenEndpointAuthMethod = client.TokenEndpointAuthMethod,
                Source = client.Source,
                MetadataUri = client.MetadataUri,
                UpdatedAt = client.UpdatedAt
            })
            .Where(item => item.ClientId == client.ClientId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public Task<int> CountClientsAsync(CancellationToken cancellationToken) =>
        database.Queryable<OAuthClientSchemaEntity>().CountAsync(cancellationToken);

    // ───────────────────────── 同意 ─────────────────────────

    public async Task<OAuthConsent?> GetConsentAsync(string consentId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<OAuthConsentSchemaEntity>()
            .Where(item => item.Id == consentId)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToConsent(row);
    }

    public async Task<OAuthConsent?> FindActiveConsentAsync(
        string subject,
        string clientId,
        CancellationToken cancellationToken)
    {
        var row = await database.Queryable<OAuthConsentSchemaEntity>()
            .Where(item => item.Subject == subject && item.ClientId == clientId && item.RevokedAt == null)
            .OrderBy(item => item.UpdatedAt, OrderByType.Desc)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToConsent(row);
    }

    public async Task CreateConsentAsync(OAuthConsent consent, CancellationToken cancellationToken)
    {
        await database.Insertable(new OAuthConsentSchemaEntity
        {
            Id = consent.Id,
            Subject = consent.Subject,
            ClientId = consent.ClientId,
            Scopes = Serialize(consent.Scopes),
            KnowledgeBaseIds = SerializeGuids(consent.KnowledgeBaseIds),
            CreatedAt = consent.CreatedAt,
            UpdatedAt = consent.UpdatedAt,
            RevokedAt = consent.RevokedAt
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateConsentAsync(OAuthConsent consent, CancellationToken cancellationToken)
    {
        await database.Updateable<OAuthConsentSchemaEntity>()
            .SetColumns(item => new OAuthConsentSchemaEntity
            {
                Scopes = Serialize(consent.Scopes),
                KnowledgeBaseIds = SerializeGuids(consent.KnowledgeBaseIds),
                UpdatedAt = consent.UpdatedAt,
                RevokedAt = consent.RevokedAt
            })
            .Where(item => item.Id == consent.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task RevokeConsentAsync(string consentId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        await database.Updateable<OAuthConsentSchemaEntity>()
            .SetColumns(item => new OAuthConsentSchemaEntity
            {
                RevokedAt = revokedAt,
                UpdatedAt = revokedAt
            })
            .Where(item => item.Id == consentId && item.RevokedAt == null)
            .ExecuteCommandAsync(cancellationToken);

        // 同一个同意的刷新令牌一并作废：否则撤销之后还能换出新令牌。
        await database.Updateable<OAuthRefreshTokenSchemaEntity>()
            .SetColumns(item => new OAuthRefreshTokenSchemaEntity { RevokedAt = revokedAt })
            .Where(item => item.ConsentId == consentId && item.RevokedAt == null)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OAuthConsent>> ListConsentsAsync(string subject, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<OAuthConsentSchemaEntity>()
            .Where(item => item.Subject == subject)
            .OrderBy(item => item.UpdatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);

        return rows.Select(ToConsent).ToList();
    }

    // ───────────────────────── 授权请求 ─────────────────────────

    public async Task CreateAuthorizationRequestAsync(
        OAuthAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        await database.Insertable(new OAuthAuthorizationRequestSchemaEntity
        {
            Id = request.Id,
            ClientId = request.ClientId,
            ClientName = request.ClientName,
            RedirectUri = request.RedirectUri,
            ResponseType = request.ResponseType,
            Scope = request.Scope,
            State = request.State,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            Resource = request.Resource,
            Subject = request.Subject,
            CreatedAt = request.CreatedAt,
            ExpiresAt = request.ExpiresAt,
            Status = request.Status
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<OAuthAuthorizationRequest?> GetAuthorizationRequestAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var row = await database.Queryable<OAuthAuthorizationRequestSchemaEntity>()
            .Where(item => item.Id == id)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToAuthorizationRequest(row);
    }

    public async Task UpdateAuthorizationRequestStatusAsync(
        string id,
        string status,
        CancellationToken cancellationToken)
    {
        await database.Updateable<OAuthAuthorizationRequestSchemaEntity>()
            .SetColumns(item => new OAuthAuthorizationRequestSchemaEntity { Status = status })
            .Where(item => item.Id == id && item.Status == OAuthAuthorizationRequest.StatusPending)
            .ExecuteCommandAsync(cancellationToken);
    }

    // ───────────────────────── 授权码 ─────────────────────────

    public async Task CreateCodeAsync(OAuthAuthorizationCode code, CancellationToken cancellationToken)
    {
        await database.Insertable(new OAuthAuthorizationCodeSchemaEntity
        {
            CodeHash = code.CodeHash,
            ClientId = code.ClientId,
            Subject = code.Subject,
            RedirectUri = code.RedirectUri,
            Scopes = Serialize(code.Scopes),
            KnowledgeBaseIds = SerializeGuids(code.KnowledgeBaseIds),
            CodeChallenge = code.CodeChallenge,
            CodeChallengeMethod = code.CodeChallengeMethod,
            Resource = code.Resource,
            ConsentId = code.ConsentId,
            CreatedAt = code.CreatedAt,
            ExpiresAt = code.ExpiresAt,
            ConsumedAt = code.ConsumedAt
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<OAuthAuthorizationCode?> ConsumeCodeAsync(
        string codeHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 条件更新保证「只能用一次」：两个并发换码请求里只有一个能把 consumed_at 从 NULL 写进去。
        var affected = await database.Updateable<OAuthAuthorizationCodeSchemaEntity>()
            .SetColumns(item => new OAuthAuthorizationCodeSchemaEntity { ConsumedAt = now })
            .Where(item => item.CodeHash == codeHash && item.ConsumedAt == null && item.ExpiresAt > now)
            .ExecuteCommandAsync(cancellationToken);

        if (affected == 0) return null;

        var row = await database.Queryable<OAuthAuthorizationCodeSchemaEntity>()
            .Where(item => item.CodeHash == codeHash)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToCode(row);
    }

    // ───────────────────────── 刷新令牌 ─────────────────────────

    public async Task CreateRefreshTokenAsync(OAuthRefreshToken token, CancellationToken cancellationToken)
    {
        await database.Insertable(new OAuthRefreshTokenSchemaEntity
        {
            TokenHash = token.TokenHash,
            ClientId = token.ClientId,
            Subject = token.Subject,
            ConsentId = token.ConsentId,
            Scopes = Serialize(token.Scopes),
            Resource = token.Resource,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt,
            RevokedAt = token.RevokedAt,
            ReplacedByHash = token.ReplacedByHash
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<OAuthRefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<OAuthRefreshTokenSchemaEntity>()
            .Where(item => item.TokenHash == tokenHash)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToRefreshToken(row);
    }

    public async Task RevokeRefreshTokenAsync(
        string tokenHash,
        string? replacedByHash,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        await database.Updateable<OAuthRefreshTokenSchemaEntity>()
            .SetColumns(item => new OAuthRefreshTokenSchemaEntity
            {
                RevokedAt = revokedAt,
                ReplacedByHash = replacedByHash
            })
            .Where(item => item.TokenHash == tokenHash && item.RevokedAt == null)
            .ExecuteCommandAsync(cancellationToken);
    }

    // ───────────────────────── jti 黑名单 ─────────────────────────

    public async Task RevokeJtiAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (await database.Queryable<OAuthRevokedJtiSchemaEntity>().AnyAsync(item => item.Jti == jti, cancellationToken))
            return;

        await database.Insertable(new OAuthRevokedJtiSchemaEntity
        {
            Jti = jti,
            ExpiresAt = expiresAt,
            RevokedAt = DateTimeOffset.UtcNow
        }).ExecuteCommandAsync(cancellationToken);
    }

    public Task<bool> IsJtiRevokedAsync(string jti, CancellationToken cancellationToken) =>
        database.Queryable<OAuthRevokedJtiSchemaEntity>().AnyAsync(item => item.Jti == jti, cancellationToken);

    // ───────────────────────── 认证事件 ─────────────────────────

    public async Task RecordAuthEventAsync(OAuthAuthEvent authEvent, CancellationToken cancellationToken)
    {
        await database.Insertable(new OAuthAuthEventSchemaEntity
        {
            OccurredAt = authEvent.OccurredAt,
            EventType = authEvent.EventType,
            ClientId = authEvent.ClientId,
            Subject = authEvent.Subject,
            Outcome = authEvent.Outcome,
            RemoteAddress = authEvent.RemoteAddress,
            Detail = authEvent.Detail,
            Error = authEvent.Error
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OAuthAuthEvent>> QueryAuthEventsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<OAuthAuthEventSchemaEntity>()
            .OrderBy(item => item.OccurredAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(row => new OAuthAuthEvent(
            row.Id,
            row.OccurredAt,
            row.EventType,
            row.ClientId,
            row.Subject,
            row.Outcome,
            row.RemoteAddress,
            row.Detail,
            row.Error)).ToList();
    }

    public async Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await database.Deleteable<OAuthAuthorizationCodeSchemaEntity>()
            .Where(item => item.ExpiresAt < now.AddDays(-1))
            .ExecuteCommandAsync(cancellationToken);

        await database.Deleteable<OAuthRefreshTokenSchemaEntity>()
            .Where(item => item.ExpiresAt < now.AddDays(-1))
            .ExecuteCommandAsync(cancellationToken);

        await database.Deleteable<OAuthRevokedJtiSchemaEntity>()
            .Where(item => item.ExpiresAt < now)
            .ExecuteCommandAsync(cancellationToken);

        await database.Deleteable<OAuthAuthorizationRequestSchemaEntity>()
            .Where(item => item.ExpiresAt < now.AddDays(-1))
            .ExecuteCommandAsync(cancellationToken);
    }

    // ───────────────────────── 映射 ─────────────────────────

    private static OAuthClientRegistration ToClient(OAuthClientSchemaEntity row) => new(
        row.ClientId,
        row.ClientName,
        Deserialize(row.RedirectUris),
        Deserialize(row.GrantTypes),
        Deserialize(row.Scopes),
        row.TokenEndpointAuthMethod,
        row.Source,
        row.MetadataUri,
        row.CreatedAt,
        row.UpdatedAt);

    private static OAuthConsent ToConsent(OAuthConsentSchemaEntity row) => new(
        row.Id,
        row.Subject,
        row.ClientId,
        Deserialize(row.Scopes),
        DeserializeGuids(row.KnowledgeBaseIds),
        row.CreatedAt,
        row.UpdatedAt,
        row.RevokedAt);

    private static OAuthAuthorizationRequest ToAuthorizationRequest(OAuthAuthorizationRequestSchemaEntity row) => new(
        row.Id,
        row.ClientId,
        row.ClientName,
        row.RedirectUri,
        row.ResponseType,
        row.Scope,
        row.State,
        row.CodeChallenge,
        row.CodeChallengeMethod,
        row.Resource,
        row.Subject,
        row.CreatedAt,
        row.ExpiresAt,
        row.Status);

    private static OAuthAuthorizationCode ToCode(OAuthAuthorizationCodeSchemaEntity row) => new(
        row.CodeHash,
        row.ClientId,
        row.Subject,
        row.RedirectUri,
        Deserialize(row.Scopes),
        DeserializeGuids(row.KnowledgeBaseIds),
        row.CodeChallenge,
        row.CodeChallengeMethod,
        row.Resource,
        row.ConsentId,
        row.CreatedAt,
        row.ExpiresAt,
        row.ConsumedAt);

    private static OAuthRefreshToken ToRefreshToken(OAuthRefreshTokenSchemaEntity row) => new(
        row.TokenHash,
        row.ClientId,
        row.Subject,
        row.ConsentId,
        Deserialize(row.Scopes),
        row.Resource,
        row.CreatedAt,
        row.ExpiresAt,
        row.RevokedAt,
        row.ReplacedByHash);

    private static string Serialize(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static string SerializeGuids(IReadOnlyList<Guid> values) =>
        JsonSerializer.Serialize(values.Select(id => id.ToString("D")), JsonOptions);

    private static IReadOnlyList<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 解析失败 = 空集合 = 更严格。
            return [];
        }
    }

    private static IReadOnlyList<Guid> DeserializeGuids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
            return values
                .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToList();
        }
        catch (JsonException)
        {
            // 损坏时给一个不可能的知识库 ID：宁可全部拒绝，也不要退化成「不限制」。
            return [Guid.Empty];
        }
    }
}
