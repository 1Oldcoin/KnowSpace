using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// 插件安装态：用户意图的唯一真实来源（是否启用、配置、已授予权限）。
/// 进程、健康等运行时状态不入库，重启后按 manifest 重新发现。
/// </summary>
[SugarTable("plugin_installations")]
public sealed class PluginInstallationSchemaEntity
{
    [SugarColumn(ColumnName = "plugin_id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string PluginId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "version", ColumnDataType = "text", IsNullable = false)]
    public string Version { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "publisher", ColumnDataType = "text", IsNullable = false)]
    public string Publisher { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "enabled", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "true")]
    public bool Enabled { get; set; }

    /// <summary>用户配置（JSON 对象文本）；空串表示未配置。</summary>
    [SugarColumn(ColumnName = "config_json", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string ConfigJson { get; set; } = string.Empty;

    /// <summary>已授予权限的 JSON 数组文本。默认不授予任何权限。</summary>
    [SugarColumn(ColumnName = "granted_permissions", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string GrantedPermissions { get; set; } = "[]";

    [SugarColumn(ColumnName = "installed_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset InstalledAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    [SugarColumn(ColumnName = "last_state", ColumnDataType = "text", IsNullable = true)]
    public string? LastState { get; set; }

    [SugarColumn(ColumnName = "last_error", ColumnDataType = "text", IsNullable = true)]
    public string? LastError { get; set; }

    /// <summary>
    /// 允许写入的知识库 ID（JSON 数组）。空数组 = 不限制；
    /// 是对“插件只能把内容写进用户授权的知识库”的落实。
    /// </summary>
    [SugarColumn(ColumnName = "allowed_knowledge_base_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string AllowedKnowledgeBaseIds { get; set; } = "[]";

    /// <summary>该插件单知识库最多保留多少条活跃平台条目；0 表示使用全局默认值。</summary>
    [SugarColumn(ColumnName = "max_documents", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int MaxDocuments { get; set; }
}

/// <summary>
/// 插件调用审计。写入路径不做事务、失败只记日志——
/// 审计缺失不能反过来阻断业务调用，但成功调用必须有记录。
/// </summary>
[SugarTable("plugin_audit_events")]
[SugarIndex("ix_plugin_audit_plugin_id_occurred_at", nameof(PluginId), OrderByType.Asc, nameof(OccurredAt), OrderByType.Desc, false)]
public sealed class PluginAuditSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset OccurredAt { get; set; }

    [SugarColumn(ColumnName = "plugin_id", ColumnDataType = "text", IsNullable = false)]
    public string PluginId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "action", ColumnDataType = "text", IsNullable = false)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "capability_id", ColumnDataType = "text", IsNullable = true)]
    public string? CapabilityId { get; set; }

    [SugarColumn(ColumnName = "actor", ColumnDataType = "text", IsNullable = false)]
    public string Actor { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "outcome", ColumnDataType = "text", IsNullable = false)]
    public string Outcome { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "duration_ms", ColumnDataType = "int8", IsNullable = false, DefaultValue = "0")]
    public long DurationMs { get; set; }

    [SugarColumn(ColumnName = "detail", ColumnDataType = "text", IsNullable = true)]
    public string? Detail { get; set; }
}
