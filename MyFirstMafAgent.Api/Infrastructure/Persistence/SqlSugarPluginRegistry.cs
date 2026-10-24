using System.Text.Json;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 插件安装态与审计的 SqlSugar 实现。
///
/// <para>
/// 只保存用户意图与审计，不保存进程状态；插件目录是"已安装什么"的真实来源，
/// 本表记录"用户希望它处于什么状态"。
/// </para>
/// </summary>
public sealed class SqlSugarPluginRegistry(SqlSugarScope database)
    : IPluginRegistry, IPluginItemMap, IPluginSyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<PluginInstallationSchemaEntity>();
        database.CodeFirst.InitTables<PluginAuditSchemaEntity>();
        database.CodeFirst.InitTables<PluginPlatformItemSchemaEntity>();
        database.CodeFirst.InitTables<PluginSyncStateSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PluginRegistration>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<PluginInstallationSchemaEntity>()
            .OrderBy(row => row.PluginId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToRegistration).ToList();
    }

    public async Task<PluginRegistration?> GetAsync(string pluginId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<PluginInstallationSchemaEntity>()
            .Where(item => item.PluginId == pluginId)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToRegistration(row);
    }

    public async Task SaveAsync(PluginRegistration registration, CancellationToken cancellationToken)
    {
        var granted = JsonSerializer.Serialize(registration.GrantedPermissions, JsonOptions);
        var allowedKnowledgeBases = JsonSerializer.Serialize(
            registration.AllowedKnowledgeBaseIds.Select(id => id.ToString("D")),
            JsonOptions);

        var exists = await database.Queryable<PluginInstallationSchemaEntity>()
            .AnyAsync(item => item.PluginId == registration.PluginId, cancellationToken);

        if (!exists)
        {
            await database.Insertable(new PluginInstallationSchemaEntity
            {
                PluginId = registration.PluginId,
                Name = registration.Name,
                Version = registration.Version,
                Publisher = registration.Publisher,
                Enabled = registration.Enabled,
                ConfigJson = registration.ConfigJson,
                GrantedPermissions = granted,
                AllowedKnowledgeBaseIds = allowedKnowledgeBases,
                MaxDocuments = registration.MaxDocuments,
                InstalledAt = registration.InstalledAt,
                UpdatedAt = registration.UpdatedAt,
                LastState = registration.LastKnownState,
                LastError = registration.LastError
            }).ExecuteCommandAsync(cancellationToken);

            return;
        }

        await database.Updateable<PluginInstallationSchemaEntity>()
            .SetColumns(item => new PluginInstallationSchemaEntity
            {
                Name = registration.Name,
                Version = registration.Version,
                Publisher = registration.Publisher,
                Enabled = registration.Enabled,
                ConfigJson = registration.ConfigJson,
                GrantedPermissions = granted,
                AllowedKnowledgeBaseIds = allowedKnowledgeBases,
                MaxDocuments = registration.MaxDocuments,
                UpdatedAt = SqlFunc.GetDate(),
                LastState = registration.LastKnownState,
                LastError = registration.LastError
            })
            .Where(item => item.PluginId == registration.PluginId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task DeleteAsync(string pluginId, CancellationToken cancellationToken)
    {
        await database.Deleteable<PluginInstallationSchemaEntity>()
            .Where(item => item.PluginId == pluginId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task RecordAuditAsync(PluginAuditRecord record, CancellationToken cancellationToken)
    {
        await database.Insertable(new PluginAuditSchemaEntity
        {
            OccurredAt = record.OccurredAt,
            PluginId = record.PluginId,
            Action = record.Action,
            CapabilityId = record.CapabilityId,
            Actor = record.Actor,
            Outcome = record.Outcome,
            DurationMs = record.DurationMs,
            Detail = record.Detail
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PluginAuditRecord>> QueryAuditAsync(
        string? pluginId,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<PluginAuditSchemaEntity>();

        if (!string.IsNullOrWhiteSpace(pluginId))
            query = query.Where(item => item.PluginId == pluginId);

        var rows = await query
            .OrderBy(item => item.OccurredAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(row => new PluginAuditRecord(
            row.Id,
            row.OccurredAt,
            row.PluginId,
            row.Action,
            row.CapabilityId,
            row.Actor,
            row.Outcome,
            row.DurationMs,
            row.Detail)).ToList();
    }

    private static PluginRegistration ToRegistration(PluginInstallationSchemaEntity row) => new(
        row.PluginId,
        row.Name,
        row.Version,
        row.Publisher,
        row.Enabled,
        row.ConfigJson ?? string.Empty,
        ParsePermissions(row.GrantedPermissions),
        row.InstalledAt,
        row.UpdatedAt,
        row.LastState,
        row.LastError,
        ParseGuidList(row.AllowedKnowledgeBaseIds),
        row.MaxDocuments);

    private static IReadOnlyList<string> ParsePermissions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 权限记录损坏时按"未授予"处理：宁可拒绝启动，也不放大权限。
            return [];
        }
    }

    /// <summary>解析知识库范围；记录损坏时返回空集合，但空集合代表“不限制”——
    /// 因此损坏时返回一个不可能的哨兵会让插件彻底不可同步，反而更安全。</summary>
    private static IReadOnlyList<Guid> ParseGuidList(string? json)
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
            return [Guid.Empty];
        }
    }

    // ───────────────────────── 平台条目映射 ─────────────────────────

    public async Task<PlatformItemLink?> GetAsync(
        string pluginId,
        string source,
        string externalId,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        var row = await database.Queryable<PluginPlatformItemSchemaEntity>()
            .Where(item => item.PluginId == pluginId
                && item.Source == source
                && item.ExternalId == externalId
                && item.KnowledgeBaseId == knowledgeBaseId)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToLink(row);
    }

    public async Task SaveAsync(PlatformItemLink link, CancellationToken cancellationToken)
    {
        var existing = await database.Queryable<PluginPlatformItemSchemaEntity>()
            .Where(item => item.PluginId == link.PluginId
                && item.Source == link.Source
                && item.ExternalId == link.ExternalId
                && item.KnowledgeBaseId == link.KnowledgeBaseId)
            .FirstAsync(cancellationToken);

        if (existing is null)
        {
            await database.Insertable(new PluginPlatformItemSchemaEntity
            {
                PluginId = link.PluginId,
                Source = link.Source,
                ExternalId = link.ExternalId,
                KnowledgeBaseId = link.KnowledgeBaseId,
                DocumentId = link.DocumentId,
                ContentHash = link.ContentHash,
                Title = link.Title,
                State = link.State,
                DocumentVersion = link.DocumentVersion,
                FirstSeenAt = link.FirstSeenAt,
                LastSeenAt = link.LastSeenAt,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ExecuteCommandAsync(cancellationToken);

            return;
        }

        var update = database.Updateable<PluginPlatformItemSchemaEntity>()
            .SetColumns(item => new PluginPlatformItemSchemaEntity
            {
                KnowledgeBaseId = link.KnowledgeBaseId,
                ContentHash = link.ContentHash,
                Title = link.Title,
                State = link.State,
                DocumentVersion = link.DocumentVersion,
                LastSeenAt = link.LastSeenAt,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == existing.Id);

        // document_id 是 uuid 列；SqlSugar 把"可空 Guid 变量为 null"参数化成 text，
        // PostgreSQL 会直接报 42804。撤回（墓碑）必须显式写成常量 null 才安全。
        update = link.DocumentId is { } documentId
            ? update.SetColumns(item => item.DocumentId == documentId)
            : update.SetColumns(item => new PluginPlatformItemSchemaEntity { DocumentId = null });

        await update.ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformItemLink>> ListAsync(
        string pluginId,
        Guid? knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<PluginPlatformItemSchemaEntity>()
            .Where(item => item.PluginId == pluginId);

        if (knowledgeBaseId is { } knowledgeBase)
            query = query.Where(item => item.KnowledgeBaseId == knowledgeBase);

        var rows = await query
            .OrderBy(item => item.LastSeenAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);

        return rows.Select(ToLink).ToList();
    }

    public async Task<int> CountActiveAsync(string pluginId, Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        await database.Queryable<PluginPlatformItemSchemaEntity>()
            .Where(item => item.PluginId == pluginId
                && item.KnowledgeBaseId == knowledgeBaseId
                && item.State == PlatformItemStates.Active)
            .CountAsync(cancellationToken);

    public async Task DeleteByPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        await database.Deleteable<PluginPlatformItemSchemaEntity>()
            .Where(item => item.PluginId == pluginId)
            .ExecuteCommandAsync(cancellationToken);
    }

    private static PlatformItemLink ToLink(PluginPlatformItemSchemaEntity row) => new(
        row.PluginId,
        row.Source,
        row.ExternalId,
        row.KnowledgeBaseId,
        row.DocumentId,
        row.ContentHash,
        row.Title,
        row.State,
        row.DocumentVersion,
        row.FirstSeenAt,
        row.LastSeenAt);

    // ───────────────────────── 同步游标 ─────────────────────────

    public async Task<PluginSyncState?> GetByScopeAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<PluginSyncStateSchemaEntity>()
            .Where(item => item.ScopeKey == scopeKey)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToSyncState(row);
    }

    public async Task SaveAsync(PluginSyncState state, CancellationToken cancellationToken)
    {
        var row = new PluginSyncStateSchemaEntity
        {
            ScopeKey = state.ScopeKey,
            PluginId = state.PluginId,
            CapabilityId = state.CapabilityId,
            KnowledgeBaseId = state.KnowledgeBaseId,
            Cursor = state.Cursor,
            NextSyncAfterSeconds = state.NextSyncAfterSeconds,
            LastSyncedAt = state.LastSyncedAt,
            LastStatus = state.LastStatus,
            LastError = state.LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var exists = await database.Queryable<PluginSyncStateSchemaEntity>()
            .AnyAsync(item => item.ScopeKey == state.ScopeKey, cancellationToken);

        if (!exists)
        {
            await database.Insertable(row).ExecuteCommandAsync(cancellationToken);
            return;
        }

        // 用实体更新而不是 SetColumns 表达式：表达式里的可空 DateTimeOffset? 为 null 时，
        // SqlSugar 会把参数当 text 发过去，PostgreSQL 直接报
        // "column last_synced_at is of type timestamp with time zone but expression is of type text"。
        // 按主键更新整行时，参数类型来自实体属性，null 也能正确写成 NULL。
        await database.Updateable(row)
            .WhereColumns(item => new { item.ScopeKey })
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PluginSyncState>> ListAsync(string pluginId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<PluginSyncStateSchemaEntity>()
            .Where(item => item.PluginId == pluginId)
            .OrderBy(item => item.UpdatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);

        return rows.Select(ToSyncState).ToList();
    }

    /// <summary>
    /// 定时同步的补投来源：只取声明了间隔（&gt; 0）的游标。
    /// 是否“到点”由补投器按 <c>LastSyncedAt + 间隔</c> 判断，SQL 里不做时间运算，
    /// 便于用同一个 DateTimeOffset 时钟做测试。
    /// </summary>
    public async Task<IReadOnlyList<PluginSyncState>> ListScheduledAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<PluginSyncStateSchemaEntity>()
            .Where(item => item.NextSyncAfterSeconds != null && item.NextSyncAfterSeconds > 0)
            .OrderBy(item => item.UpdatedAt, OrderByType.Asc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(ToSyncState).ToList();
    }

    public async Task DeleteAllForPluginAsync(string pluginId, CancellationToken cancellationToken)
    {        await database.Deleteable<PluginSyncStateSchemaEntity>()
            .Where(item => item.PluginId == pluginId)
            .ExecuteCommandAsync(cancellationToken);
    }

    private static PluginSyncState ToSyncState(PluginSyncStateSchemaEntity row) => new(
        row.ScopeKey,
        row.PluginId,
        row.CapabilityId,
        row.KnowledgeBaseId,
        row.Cursor,
        row.NextSyncAfterSeconds,
        row.LastSyncedAt,
        row.LastStatus,
        row.LastError,
        row.UpdatedAt);
}
