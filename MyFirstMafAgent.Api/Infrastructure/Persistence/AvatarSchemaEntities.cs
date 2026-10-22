using MyFirstMafAgent.Api.Domain.Avatar;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

[SugarTable("avatar_profile")]
public sealed class AvatarProfileSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "display_name", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "headline", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Headline { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "personality", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Personality { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "values_text", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Values { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "strengths", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Strengths { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "weaknesses", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Weaknesses { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "skills", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Skills { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "resources", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Resources { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "constraints", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Constraints { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "long_term_goals", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string LongTermGoals { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "risk_preference", ColumnDataType = "text", IsNullable = false, DefaultValue = "'balanced'")]
    public string RiskPreference { get; set; } = AvatarDefaults.RiskPreference;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("avatar_experiences")]
[SugarIndex("ix_avatar_experiences_occurred", nameof(OccurredAt), OrderByType.Desc)]
public sealed class AvatarExperienceSchemaEntity{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset OccurredAt { get; set; }

    [SugarColumn(ColumnName = "category", ColumnDataType = "text", IsNullable = false, DefaultValue = "'其它'")]
    public string Category { get; set; } = "其它";

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "outcome", ColumnDataType = "text", IsNullable = false, DefaultValue = "'ongoing'")]
    public string Outcome { get; set; } = "ongoing";

    [SugarColumn(ColumnName = "emotion", ColumnDataType = "text", IsNullable = false, DefaultValue = "'neutral'")]
    public string Emotion { get; set; } = "neutral";

    [SugarColumn(ColumnName = "impact", ColumnDataType = "integer", IsNullable = false, DefaultValue = "3")]
    public int Impact { get; set; } = 3;

    [SugarColumn(ColumnName = "tags", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Tags { get; set; } = "[]";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("avatar_behaviors")]
[SugarIndex("ix_avatar_behaviors_occurred", nameof(OccurredOn), OrderByType.Desc)]
public sealed class AvatarBehaviorSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "occurred_on", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset OccurredOn { get; set; }

    [SugarColumn(ColumnName = "category", ColumnDataType = "text", IsNullable = false, DefaultValue = "'其它'")]
    public string Category { get; set; } = "其它";

    [SugarColumn(ColumnName = "description", ColumnDataType = "text", IsNullable = false)]
    public string Description { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "duration_minutes", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int DurationMinutes { get; set; }

    [SugarColumn(ColumnName = "mood", ColumnDataType = "integer", IsNullable = true)]
    public int? Mood { get; set; }

    [SugarColumn(ColumnName = "tags", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Tags { get; set; } = "[]";

    [SugarColumn(ColumnName = "source", ColumnDataType = "text", IsNullable = false, DefaultValue = "'manual'")]
    public string Source { get; set; } = "manual";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("avatar_relationships")]
public sealed class AvatarRelationshipSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "role", ColumnDataType = "text", IsNullable = false, DefaultValue = "'其它'")]
    public string Role { get; set; } = "其它";

    [SugarColumn(ColumnName = "closeness", ColumnDataType = "integer", IsNullable = false, DefaultValue = "3")]
    public int Closeness { get; set; } = 3;

    [SugarColumn(ColumnName = "influence", ColumnDataType = "integer", IsNullable = false, DefaultValue = "3")]
    public int Influence { get; set; } = 3;

    [SugarColumn(ColumnName = "notes", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Notes { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("avatar_simulations")]
[SugarIndex("ix_avatar_simulations_created", nameof(CreatedAt), OrderByType.Desc)]
public sealed class AvatarSimulationSchemaEntity{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "scenario", ColumnDataType = "text", IsNullable = false)]
    public string Scenario { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "goal", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Goal { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "horizon_months", ColumnDataType = "integer", IsNullable = false, DefaultValue = "6")]
    public int HorizonMonths { get; set; } = 6;

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false, DefaultValue = "'running'")]
    public string Status { get; set; } = "running";

    [SugarColumn(ColumnName = "success_probability", ColumnDataType = "integer", IsNullable = true)]
    public int? SuccessProbability { get; set; }

    [SugarColumn(ColumnName = "confidence", ColumnDataType = "integer", IsNullable = true)]
    public int? Confidence { get; set; }

    [SugarColumn(ColumnName = "summary", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Summary { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "report", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Report { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "key_factors", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string KeyFactors { get; set; } = "[]";

    [SugarColumn(ColumnName = "milestones", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Milestones { get; set; } = "[]";

    [SugarColumn(ColumnName = "risks", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Risks { get; set; } = "[]";

    [SugarColumn(ColumnName = "next_actions", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string NextActions { get; set; } = "[]";

    [SugarColumn(ColumnName = "model", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Model { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "error_message", ColumnDataType = "text", IsNullable = true)]
    public string? ErrorMessage { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>碎片投喂素材：日记、聊天记录、性格自述、形象描述等。</summary>
[SugarTable("avatar_fragments")]
[SugarIndex("ix_avatar_fragments_occurred", nameof(OccurredAt), OrderByType.Desc)]
[SugarIndex("ix_avatar_fragments_digested", nameof(IsDigested), OrderByType.Asc)]
public sealed class AvatarFragmentSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "kind", ColumnDataType = "text", IsNullable = false, DefaultValue = "'other'")]
    public string Kind { get; set; } = "other";

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset OccurredAt { get; set; }

    [SugarColumn(ColumnName = "source", ColumnDataType = "text", IsNullable = false, DefaultValue = "'manual'")]
    public string Source { get; set; } = "manual";

    [SugarColumn(ColumnName = "tags", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Tags { get; set; } = "[]";

    [SugarColumn(ColumnName = "is_digested", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "false")]
    public bool IsDigested { get; set; }

    /// <summary>原文哈希，用于 source_hash + segment_index + extractor_version 幂等。</summary>
    [SugarColumn(ColumnName = "source_hash", ColumnDataType = "text", IsNullable = true)]
    public string? SourceHash { get; set; }

    [SugarColumn(ColumnName = "segment_index", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int SegmentIndex { get; set; }

    [SugarColumn(ColumnName = "extractor_version", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string ExtractorVersion { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "ingestion_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? IngestionId { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>从碎片中归纳出的人格洞察。</summary>
[SugarTable("avatar_insights")]
[SugarIndex("ux_avatar_insights_dimension_title", nameof(Dimension), OrderByType.Asc, nameof(Title), OrderByType.Asc, true)]
public sealed class AvatarInsightSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "dimension", ColumnDataType = "text", IsNullable = false)]
    public string Dimension { get; set; } = "summary";

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "confidence", ColumnDataType = "integer", IsNullable = false, DefaultValue = "50")]
    public int Confidence { get; set; } = 50;

    [SugarColumn(ColumnName = "evidence_count", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int EvidenceCount { get; set; }

    [SugarColumn(ColumnName = "evidence", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Evidence { get; set; } = "[]";

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>分身对话会话（与知识库问答完全隔离）。</summary>
[SugarTable("avatar_chat_sessions")]
public sealed class AvatarChatSessionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false, DefaultValue = "'新的对话'")]
    public string Title { get; set; } = "新的对话";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("avatar_chat_messages")]
[SugarIndex("ix_avatar_chat_messages_session", nameof(SessionId), OrderByType.Asc, nameof(Id), OrderByType.Asc)]
public sealed class AvatarChatMessageSchemaEntity
{
    // 注意：PostgreSQL 下自增列必须用 int8；写成 bigint 时 SqlSugar 会生成非法类型 serialt。
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "session_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid SessionId { get; set; }

    [SugarColumn(ColumnName = "role", ColumnDataType = "text", IsNullable = false)]
    public string Role { get; set; } = "user";

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "citations", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Citations { get; set; } = "[]";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// AI 整理后的记忆卡。用户投喂自由文本，模型把它拆成可检索、可复用的记忆条目；
/// 记忆与原始碎片通过 fragment_id 关联，删除记忆不会删除原始底稿。
/// </summary>
[SugarTable("avatar_memories")]
[SugarIndex("ix_avatar_memories_occurred", nameof(OccurredAt), OrderByType.Desc)]
[SugarIndex("ix_avatar_memories_kind", nameof(Kind), OrderByType.Asc)]
[SugarIndex("ix_avatar_memories_vector_state", nameof(VectorState), OrderByType.Asc)]
[SugarIndex("ix_avatar_memories_lifecycle", nameof(LifecycleStatus), OrderByType.Asc)]
[SugarIndex("ix_avatar_memories_type", nameof(MemoryType), OrderByType.Asc)]
public sealed class AvatarMemorySchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "fragment_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? FragmentId { get; set; }

    [SugarColumn(ColumnName = "kind", ColumnDataType = "text", IsNullable = false, DefaultValue = "'other'")]
    public string Kind { get; set; } = "other";

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "summary", ColumnDataType = "text", IsNullable = false)]
    public string Summary { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false)]
    public DateTimeOffset OccurredAt { get; set; }

    [SugarColumn(ColumnName = "people", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string People { get; set; } = "[]";

    [SugarColumn(ColumnName = "tags", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Tags { get; set; } = "[]";

    [SugarColumn(ColumnName = "emotion", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Emotion { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "importance", ColumnDataType = "integer", IsNullable = false, DefaultValue = "3")]
    public int Importance { get; set; } = 3;

    [SugarColumn(ColumnName = "source", ColumnDataType = "text", IsNullable = false, DefaultValue = "'chat'")]
    public string Source { get; set; } = "chat";

    [SugarColumn(ColumnName = "vector_state", ColumnDataType = "text", IsNullable = false, DefaultValue = "'pending'")]
    public string VectorState { get; set; } = "pending";

    // ---- v2：证据链记忆 ----

    [SugarColumn(ColumnName = "memory_type", ColumnDataType = "text", IsNullable = false, DefaultValue = "'fact'")]
    public string MemoryType { get; set; } = "fact";

    [SugarColumn(ColumnName = "stability", ColumnDataType = "text", IsNullable = false, DefaultValue = "'situational'")]
    public string Stability { get; set; } = "situational";

    [SugarColumn(ColumnName = "confidence", ColumnDataType = "integer", IsNullable = false, DefaultValue = "60")]
    public int Confidence { get; set; } = 60;

    [SugarColumn(ColumnName = "lifecycle_status", ColumnDataType = "text", IsNullable = false, DefaultValue = "'active'")]
    public string LifecycleStatus { get; set; } = "active";

    [SugarColumn(ColumnName = "valid_from", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? ValidFrom { get; set; }

    [SugarColumn(ColumnName = "valid_to", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? ValidTo { get; set; }

    [SugarColumn(ColumnName = "supersedes_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? SupersedesId { get; set; }

    [SugarColumn(ColumnName = "structured_data", ColumnDataType = "text", IsNullable = false, DefaultValue = "'{}'")]
    public string StructuredData { get; set; } = "{}";

    [SugarColumn(ColumnName = "privacy_level", ColumnDataType = "text", IsNullable = false, DefaultValue = "'normal'")]
    public string PrivacyLevel { get; set; } = "normal";

    [SugarColumn(ColumnName = "last_confirmed_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? LastConfirmedAt { get; set; }

    [SugarColumn(ColumnName = "last_used_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? LastUsedAt { get; set; }

    [SugarColumn(ColumnName = "evidence_count", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int EvidenceCount { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 记忆向量。与知识库索引完全隔离：不写入任何 kb_* 表，也不会被知识库检索命中。
/// 注意：embedding 是 pgvector vector 类型，读取时必须投影所需列，禁止整行读取。
/// </summary>
[SugarTable("avatar_memory_vectors")]
public sealed class AvatarMemoryVectorSchemaEntity
{
    [SugarColumn(ColumnName = "memory_id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid MemoryId { get; set; }

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

/// <summary>聊天式投喂的对话记录：用户原文 + 分身整理回执。</summary>
[SugarTable("avatar_feed_entries")]
public sealed class AvatarFeedEntrySchemaEntity
{
    // 同上：PostgreSQL 自增列使用 int8，避免生成 serialt。
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "role", ColumnDataType = "text", IsNullable = false)]
    public string Role { get; set; } = "user";

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "payload", ColumnDataType = "text", IsNullable = true)]
    public string? Payload { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}
