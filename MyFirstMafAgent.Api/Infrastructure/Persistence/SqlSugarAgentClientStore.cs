using System.Text.Json;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 外部智能体客户端与调用审计的 SqlSugar 实现（两张表）。
///
/// <para>
/// 列表接口依赖的 JSON 列（能力、知识库白名单、工具白名单）在这里
/// 统一解析；记录损坏时按「最小权限」处理，而不是按「全部放开」。
/// </para>
/// </summary>
public sealed class SqlSugarAgentClientStore(SqlSugarScope database)
    : IAgentClientStore, IAgentCallLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // 两个接口都声明了 InitializeAsync(CancellationToken)，签名不能加可选参数；
    // 需要"跳过建表检查"时走下面的重载（结构指纹一致时由 DatabaseInitializer 调用）。
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken, ensureSchema: true);

    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<AgentClientSchemaEntity>();
        database.CodeFirst.InitTables<AgentClientCallSchemaEntity>();
        return Task.CompletedTask;
    }

    // ───────────────────────── 客户端 ─────────────────────────

    public async Task<IReadOnlyList<AgentClient>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AgentClientSchemaEntity>()
            .OrderBy(row => row.CreatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);

        return rows.Select(ToClient).ToList();
    }

    public async Task<AgentClient?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<AgentClientSchemaEntity>()
            .Where(item => item.Id == id)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToClient(row);
    }

    public async Task<AgentClient?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<AgentClientSchemaEntity>()
            .Where(item => item.KeyHash == keyHash)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToClient(row);
    }

    public async Task CreateAsync(AgentClient client, string keyHash, CancellationToken cancellationToken)
    {
        await database.Insertable(new AgentClientSchemaEntity
        {
            Id = client.Id,
            Name = client.Name,
            Description = client.Description,
            KeyHash = keyHash,
            KeyPrefix = client.KeyPrefix,
            Capabilities = SerializeStrings(client.Capabilities),
            AllowedKnowledgeBaseIds = SerializeGuids(client.AllowedKnowledgeBaseIds),
            AllowedTools = SerializeStrings(client.AllowedTools),
            RequestsPerMinute = client.RequestsPerMinute,
            Enabled = client.Enabled,
            CreatedAt = client.CreatedAt,
            UpdatedAt = client.UpdatedAt,
            LastSeenAt = client.LastSeenAt,
            LastSeenAddress = client.LastSeenAddress
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateAsync(AgentClient client, string? keyHash, CancellationToken cancellationToken)
    {
        var update = database.Updateable<AgentClientSchemaEntity>()
            .SetColumns(item => new AgentClientSchemaEntity
            {
                Name = client.Name,
                Description = client.Description,
                KeyPrefix = client.KeyPrefix,
                Capabilities = SerializeStrings(client.Capabilities),
                AllowedKnowledgeBaseIds = SerializeGuids(client.AllowedKnowledgeBaseIds),
                AllowedTools = SerializeStrings(client.AllowedTools),
                RequestsPerMinute = client.RequestsPerMinute,
                Enabled = client.Enabled,
                UpdatedAt = client.UpdatedAt
            })
            .Where(item => item.Id == client.Id);

        // 换密钥只在轮换时发生；显式分支避免把 null 参数化进必填列。
        if (keyHash is not null)
            update = update.SetColumns(item => item.KeyHash == keyHash);

        await update.ExecuteCommandAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await database.Deleteable<AgentClientSchemaEntity>()
            .Where(item => item.Id == id)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task TouchAsync(
        string id,
        DateTimeOffset seenAt,
        string? address,
        CancellationToken cancellationToken)
    {
        await database.Updateable<AgentClientSchemaEntity>()
            .SetColumns(item => new AgentClientSchemaEntity
            {
                LastSeenAt = seenAt,
                LastSeenAddress = address
            })
            .Where(item => item.Id == id)
            .ExecuteCommandAsync(cancellationToken);
    }

    // ───────────────────────── 调用审计 ─────────────────────────

    public async Task RecordAsync(AgentCallRecord record, CancellationToken cancellationToken)
    {
        await database.Insertable(new AgentClientCallSchemaEntity
        {
            OccurredAt = record.OccurredAt,
            ClientId = record.ClientId,
            Tool = record.Tool,
            KnowledgeBaseId = record.KnowledgeBaseId,
            Outcome = record.Outcome,
            StatusCode = record.StatusCode,
            DurationMs = record.DurationMs,
            Error = record.Error
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCallRecord>> QueryAsync(
        string? clientId,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<AgentClientCallSchemaEntity>();
        if (!string.IsNullOrWhiteSpace(clientId))
            query = query.Where(item => item.ClientId == clientId);

        var rows = await query
            .OrderBy(item => item.OccurredAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(row => new AgentCallRecord(
            row.Id,
            row.OccurredAt,
            row.ClientId,
            row.Tool,
            row.KnowledgeBaseId,
            row.Outcome,
            row.StatusCode,
            row.DurationMs,
            row.Error)).ToList();
    }

    // ───────────────────────── 映射 ─────────────────────────

    private static AgentClient ToClient(AgentClientSchemaEntity row) => new(
        row.Id,
        row.Name,
        row.Description,
        row.KeyPrefix,
        ParseStrings(row.Capabilities),
        ParseGuids(row.AllowedKnowledgeBaseIds),
        ParseStrings(row.AllowedTools),
        row.RequestsPerMinute,
        row.Enabled,
        row.CreatedAt,
        row.UpdatedAt,
        row.LastSeenAt,
        row.LastSeenAddress);

    private static string SerializeStrings(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static string SerializeGuids(IReadOnlyList<Guid> values) =>
        JsonSerializer.Serialize(values.Select(id => id.ToString("D")), JsonOptions);

    private static IReadOnlyList<string> ParseStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 损坏的能力列表按「无权限」处理；损坏的工具白名单按「无白名单」处理，
            // 两者叠加只会更严格（能力为空 ⇒ 任何工具都无法通过能力校验）。
            return [];
        }
    }

    private static IReadOnlyList<Guid> ParseGuids(string? json)
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
            // 无法解析时给一个不可能的知识库 ID：宁可全部拒绝，也不要退化成"不限制"。
            return [Guid.Empty];
        }
    }
}
