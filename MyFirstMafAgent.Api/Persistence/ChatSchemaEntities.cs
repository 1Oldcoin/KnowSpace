using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

[SugarTable("knowledge_chat_sessions")]
[SugarIndex("ix_knowledge_chat_sessions_base_updated", nameof(KnowledgeBaseId), OrderByType.Asc, nameof(UpdatedAt), OrderByType.Desc, false)]
public sealed class KnowledgeChatSessionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("knowledge_chat_messages")]
[SugarIndex("ix_knowledge_chat_messages_session", nameof(SessionId), OrderByType.Asc, nameof(Id), OrderByType.Asc, false)]
public sealed class KnowledgeChatMessageSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "session_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid SessionId { get; set; }

    [SugarColumn(ColumnName = "role", ColumnDataType = "text", IsNullable = false)]
    public string Role { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 助手消息的引用元数据（JSON 数组，见 <see cref="KnowledgeChatCitation"/>）。
    /// 用户消息与历史数据为 null。
    /// </summary>
    [SugarColumn(ColumnName = "citations", ColumnDataType = "text", IsNullable = true)]
    public string? Citations { get; set; }

    /// <summary>
    /// 助手消息的答案元数据（检索上下文 / 工具调用 / 网页来源，见 <see cref="KnowledgeChatAnswerMeta"/>）。
    /// </summary>
    [SugarColumn(ColumnName = "answer_meta", ColumnDataType = "text", IsNullable = true)]
    public string? AnswerMeta { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("companion_profiles")]
public sealed class CompanionProfileSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "target_sender", ColumnDataType = "text", IsNullable = false)]
    public string TargetSender { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "platform", ColumnDataType = "text", IsNullable = false)]
    public string Platform { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "persona_prompt", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string PersonaPrompt { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("companion_messages")]
[SugarIndex("ix_companion_messages_profile_time", nameof(ProfileId), OrderByType.Asc, nameof(SentAt), OrderByType.Desc, nameof(Id), OrderByType.Desc, false)]
public sealed class CompanionMessageSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "profile_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid ProfileId { get; set; }

    [SugarColumn(ColumnName = "sender", ColumnDataType = "text", IsNullable = false)]
    public string Sender { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "sent_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? SentAt { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 伴侣聊天历史的向量。**与知识库索引完全隔离**：不写任何 kb_* 表，也不会被资料检索命中。
/// <para>
/// 为什么不复用知识库：伴侣记录是私人对话，混进资料索引会让"问资料"时把私聊当证据引用出来。
/// 同一条消息在不同向量模型下可以各存一行？不行——主键是 message_id，
/// 换模型靠 <c>embedding_fingerprint</c> 区分，旧模型的向量在重建时被覆盖。
/// </para>
/// </summary>
[SugarTable("companion_message_vectors")]
public sealed class CompanionMessageVectorSchemaEntity
{
    [SugarColumn(ColumnName = "message_id", ColumnDataType = "int8", IsPrimaryKey = true, IsNullable = false)]
    public long MessageId { get; set; }

    [SugarColumn(ColumnName = "embedding_spec_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid EmbeddingSpecId { get; set; }

    [SugarColumn(ColumnName = "embedding_fingerprint", ColumnDataType = "text", IsNullable = false)]
    public string EmbeddingFingerprint { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dimensions", ColumnDataType = "integer", IsNullable = false)]
    public int Dimensions { get; set; }

    [SugarColumn(ColumnName = "embedding", ColumnDataType = "vector", IsNullable = true, InsertSql = PgVectorSql.EmbeddingInsertCast)]
    public string? Embedding { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("companion_sessions")]
public sealed class CompanionSessionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "profile_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid ProfileId { get; set; }

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("companion_session_messages")]
[SugarIndex("ix_companion_session_messages_session", nameof(SessionId), OrderByType.Asc, nameof(Id), OrderByType.Asc, false)]
public sealed class CompanionSessionMessageSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "session_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid SessionId { get; set; }

    [SugarColumn(ColumnName = "role", ColumnDataType = "text", IsNullable = false)]
    public string Role { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}
