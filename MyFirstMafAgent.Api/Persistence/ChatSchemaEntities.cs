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
