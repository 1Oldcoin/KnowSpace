using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// 外部智能体客户端：谁可以接入本机 MCP 服务端、能做什么。
///
/// <para>
/// 只存密钥摘要与前缀，<b>不存明文</b>。明文只在创建/轮换时返回一次，
/// 因此这张表泄露不等于凭据泄露。
/// </para>
/// </summary>
[SugarTable("agent_clients")]
[SugarIndex("ux_agent_clients_key_hash", nameof(KeyHash), OrderByType.Asc, true)]
public sealed class AgentClientSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "description", ColumnDataType = "text", IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "key_hash", ColumnDataType = "text", IsNullable = false)]
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>密钥前缀，仅用于界面识别（例如 <c>maf_a1b2c3d4</c>）。</summary>
    [SugarColumn(ColumnName = "key_prefix", ColumnDataType = "text", IsNullable = false)]
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>已授予能力的 JSON 数组文本，例如 <c>["knowledge:read"]</c>。</summary>
    [SugarColumn(ColumnName = "capabilities", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Capabilities { get; set; } = "[]";

    /// <summary>允许访问的知识库 ID（JSON 数组）。空数组 = 不限制。</summary>
    [SugarColumn(ColumnName = "allowed_knowledge_base_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string AllowedKnowledgeBaseIds { get; set; } = "[]";

    /// <summary>工具白名单（JSON 数组）。空数组 = 本能力范围内的工具全部放开。</summary>
    [SugarColumn(ColumnName = "allowed_tools", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string AllowedTools { get; set; } = "[]";

    /// <summary>每分钟请求上限；0 表示不限制。</summary>
    [SugarColumn(ColumnName = "requests_per_minute", ColumnDataType = "integer", IsNullable = false, DefaultValue = "120")]
    public int RequestsPerMinute { get; set; }

    [SugarColumn(ColumnName = "enabled", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "true")]
    public bool Enabled { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    [SugarColumn(ColumnName = "last_seen_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? LastSeenAt { get; set; }

    [SugarColumn(ColumnName = "last_seen_address", ColumnDataType = "text", IsNullable = true)]
    public string? LastSeenAddress { get; set; }
}

/// <summary>
/// MCP 调用审计：外部智能体每次接入都留痕。追加写，客户端删除后历史保留。
///
/// <para>
/// 记录的是「谁、调了什么工具、打到哪个知识库、结果如何」，不含请求正文，
/// 避免把用户资料复制进审计表。
/// </para>
/// </summary>
[SugarTable("agent_client_calls")]
[SugarIndex("ix_agent_client_calls_client_occurred_at", nameof(ClientId), OrderByType.Asc, nameof(OccurredAt), OrderByType.Desc, false)]
public sealed class AgentClientCallSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset OccurredAt { get; set; }

    [SugarColumn(ColumnName = "client_id", ColumnDataType = "text", IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "tool", ColumnDataType = "text", IsNullable = true)]
    public string? Tool { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "text", IsNullable = true)]
    public string? KnowledgeBaseId { get; set; }

    /// <summary><c>success</c> / <c>denied</c> / <c>failed</c>。</summary>
    [SugarColumn(ColumnName = "outcome", ColumnDataType = "text", IsNullable = false)]
    public string Outcome { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "status_code", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int StatusCode { get; set; }

    [SugarColumn(ColumnName = "duration_ms", ColumnDataType = "int8", IsNullable = false, DefaultValue = "0")]
    public long DurationMs { get; set; }

    [SugarColumn(ColumnName = "error", ColumnDataType = "text", IsNullable = true)]
    public string? Error { get; set; }
}
