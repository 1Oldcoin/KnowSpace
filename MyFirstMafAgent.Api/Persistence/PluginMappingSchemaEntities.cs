using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// 平台条目映射：<c>source + externalId → documentId</c>。
///
/// <para>
/// 这张表是"幂等"与"撤回"的唯一真实来源：
/// 没有它，每次同步都会重复上传同一篇内容；有了它才能判断
/// "第一次见 / 内容没变 / 内容变了 / 平台已删除"。
/// </para>
///
/// <para>
/// 撤回不会删除映射行，而是把 <see cref="State"/> 置为 <c>retracted</c>：
/// 这样迟到的旧批次无法把已删除的内容复活。
/// </para>
/// </summary>
[SugarTable("plugin_platform_items")]
public sealed class PluginPlatformItemSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "plugin_id", ColumnDataType = "text", IsNullable = false)]
    public string PluginId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "source", ColumnDataType = "text", IsNullable = false)]
    public string Source { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "external_id", ColumnDataType = "text", IsNullable = false)]
    public string ExternalId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    /// <summary>对应资料 ID；撤回后置空，但映射行保留为墓碑。</summary>
    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? DocumentId { get; set; }

    /// <summary>最近一次成功摄取的内容哈希，用于判断"内容是否真的变了"。</summary>
    [SugarColumn(ColumnName = "content_hash", ColumnDataType = "text", IsNullable = true)]
    public string? ContentHash { get; set; }

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = true)]
    public string? Title { get; set; }

    /// <summary><c>active</c> 或 <c>retracted</c>。</summary>
    [SugarColumn(ColumnName = "state", ColumnDataType = "text", IsNullable = false)]
    public string State { get; set; } = "active";

    /// <summary>最后一次触发摄取时的资料内容版本，便于对照索引与诊断。</summary>
    [SugarColumn(ColumnName = "document_version", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int DocumentVersion { get; set; }

    [SugarColumn(ColumnName = "first_seen_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset FirstSeenAt { get; set; }

    [SugarColumn(ColumnName = "last_seen_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset LastSeenAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 同步游标：让"增量同步"真的增量。
///
/// <para>
/// 作用域键是 <c>pluginId|capabilityId|knowledgeBaseId</c>：
/// 同一个连接器同步到不同知识库时游标不能共用，否则会丢页。
/// </para>
/// </summary>
[SugarTable("plugin_sync_states")]
public sealed class PluginSyncStateSchemaEntity
{
    [SugarColumn(ColumnName = "scope_key", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string ScopeKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "plugin_id", ColumnDataType = "text", IsNullable = false)]
    public string PluginId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "capability_id", ColumnDataType = "text", IsNullable = false)]
    public string CapabilityId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    /// <summary>下次同步的起始游标；为空表示从头开始。</summary>
    [SugarColumn(ColumnName = "cursor", ColumnDataType = "text", IsNullable = true)]
    public string? Cursor { get; set; }

    [SugarColumn(ColumnName = "next_sync_after_seconds", ColumnDataType = "integer", IsNullable = true)]
    public int? NextSyncAfterSeconds { get; set; }

    [SugarColumn(ColumnName = "last_synced_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary><c>success</c> / <c>partial</c> / <c>failed</c>。</summary>
    [SugarColumn(ColumnName = "last_status", ColumnDataType = "text", IsNullable = true)]
    public string? LastStatus { get; set; }

    [SugarColumn(ColumnName = "last_error", ColumnDataType = "text", IsNullable = true)]
    public string? LastError { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}
