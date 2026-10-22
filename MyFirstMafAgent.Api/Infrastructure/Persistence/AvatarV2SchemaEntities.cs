using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>一次投喂/纠正任务。原文先落 fragment，再由后台 Worker 异步整理。</summary>
[SugarTable("avatar_ingestions")]
[SugarIndex("ix_avatar_ingestions_status", nameof(Status), OrderByType.Asc, nameof(CreatedAt), OrderByType.Asc)]
public sealed class AvatarIngestionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "source_type", ColumnDataType = "text", IsNullable = false, DefaultValue = "'text'")]
    public string SourceType { get; set; } = "text";

    [SugarColumn(ColumnName = "source_ref", ColumnDataType = "text", IsNullable = true)]
    public string? SourceRef { get; set; }

    [SugarColumn(ColumnName = "idempotency_key", ColumnDataType = "text", IsNullable = true)]
    public string? IdempotencyKey { get; set; }

    [SugarColumn(ColumnName = "fragment_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string FragmentIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "supersedes_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string SupersedesIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false, DefaultValue = "'queued'")]
    public string Status { get; set; } = "queued";

    [SugarColumn(ColumnName = "stage", ColumnDataType = "text", IsNullable = false, DefaultValue = "'received'")]
    public string Stage { get; set; } = "received";

    [SugarColumn(ColumnName = "error", ColumnDataType = "text", IsNullable = true)]
    public string? Error { get; set; }

    [SugarColumn(ColumnName = "memory_count", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int MemoryCount { get; set; }

    [SugarColumn(ColumnName = "insight_count", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int InsightCount { get; set; }

    [SugarColumn(ColumnName = "stats_json", ColumnDataType = "text", IsNullable = false, DefaultValue = "'{}'")]
    public string StatsJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    [SugarColumn(ColumnName = "started_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? StartedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>记忆到原文的证据。</summary>
[SugarTable("avatar_memory_evidence")]
[SugarIndex("ix_avatar_memory_evidence_memory", nameof(MemoryId), OrderByType.Asc)]
public sealed class AvatarMemoryEvidenceSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "memory_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid MemoryId { get; set; }

    [SugarColumn(ColumnName = "fragment_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? FragmentId { get; set; }

    [SugarColumn(ColumnName = "evidence_text", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string EvidenceText { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "start_offset", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int StartOffset { get; set; }

    [SugarColumn(ColumnName = "end_offset", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int EndOffset { get; set; }

    [SugarColumn(ColumnName = "page_no", ColumnDataType = "integer", IsNullable = true)]
    public int? PageNo { get; set; }

    [SugarColumn(ColumnName = "message_id", ColumnDataType = "text", IsNullable = true)]
    public string? MessageId { get; set; }

    [SugarColumn(ColumnName = "source_hash", ColumnDataType = "text", IsNullable = true)]
    public string? SourceHash { get; set; }

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? OccurredAt { get; set; }

    [SugarColumn(ColumnName = "recorded_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>记忆修订/合并/被取代的历史快照。</summary>
[SugarTable("avatar_memory_versions")]
[SugarIndex("ix_avatar_memory_versions_memory", nameof(MemoryId), OrderByType.Asc, nameof(Version), OrderByType.Asc)]
public sealed class AvatarMemoryVersionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "memory_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid MemoryId { get; set; }

    [SugarColumn(ColumnName = "version", ColumnDataType = "integer", IsNullable = false, DefaultValue = "1")]
    public int Version { get; set; }

    [SugarColumn(ColumnName = "snapshot_json", ColumnDataType = "text", IsNullable = false, DefaultValue = "'{}'")]
    public string SnapshotJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "change_type", ColumnDataType = "text", IsNullable = false, DefaultValue = "'created'")]
    public string ChangeType { get; set; } = "created";

    [SugarColumn(ColumnName = "supersedes_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? SupersedesId { get; set; }

    [SugarColumn(ColumnName = "reason", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>人格洞察候选版本。</summary>
[SugarTable("avatar_insight_versions")]
[SugarIndex("ix_avatar_insight_versions_status", nameof(Status), OrderByType.Asc, nameof(CreatedAt), OrderByType.Desc)]
public sealed class AvatarInsightVersionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "insight_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? InsightId { get; set; }

    [SugarColumn(ColumnName = "dimension", ColumnDataType = "text", IsNullable = false, DefaultValue = "'summary'")]
    public string Dimension { get; set; } = "summary";

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "confidence", ColumnDataType = "integer", IsNullable = false, DefaultValue = "50")]
    public int Confidence { get; set; } = 50;

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false, DefaultValue = "'candidate'")]
    public string Status { get; set; } = "candidate";

    [SugarColumn(ColumnName = "scope", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Scope { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "evidence_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string EvidenceIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "supersedes_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? SupersedesId { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>回答反馈。</summary>
[SugarTable("avatar_feedback")]
[SugarIndex("ix_avatar_feedback_created", nameof(CreatedAt), OrderByType.Desc)]
public sealed class AvatarFeedbackSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "chat_message_id", ColumnDataType = "int8", IsNullable = true)]
    public long? ChatMessageId { get; set; }

    [SugarColumn(ColumnName = "memory_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string MemoryIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "feedback_type", ColumnDataType = "text", IsNullable = false, DefaultValue = "'like'")]
    public string FeedbackType { get; set; } = "like";

    [SugarColumn(ColumnName = "reason_code", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string ReasonCode { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "comment", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Comment { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "corrected_text", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string CorrectedText { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "ingestion_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? IngestionId { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>实体：人物、组织、项目等。</summary>
[SugarTable("avatar_entities")]
[SugarIndex("ux_avatar_entities_name", nameof(EntityType), OrderByType.Asc, nameof(Name), OrderByType.Asc, true)]
public sealed class AvatarEntitySchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "entity_type", ColumnDataType = "text", IsNullable = false, DefaultValue = "'person'")]
    public string EntityType { get; set; } = "person";

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "aliases", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Aliases { get; set; } = "[]";

    [SugarColumn(ColumnName = "metadata_json", ColumnDataType = "text", IsNullable = false, DefaultValue = "'{}'")]
    public string MetadataJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("avatar_memory_entities")]
[SugarIndex("ux_avatar_memory_entities", nameof(MemoryId), OrderByType.Asc, nameof(EntityId), OrderByType.Asc, true)]
public sealed class AvatarMemoryEntitySchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "memory_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid MemoryId { get; set; }

    [SugarColumn(ColumnName = "entity_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid EntityId { get; set; }

    [SugarColumn(ColumnName = "role", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Role { get; set; } = string.Empty;
}

/// <summary>固定评测题。</summary>
[SugarTable("avatar_eval_cases")]
[SugarIndex("ix_avatar_eval_cases_category", nameof(Category), OrderByType.Asc)]
public sealed class AvatarEvalCaseSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "category", ColumnDataType = "text", IsNullable = false, DefaultValue = "'values'")]
    public string Category { get; set; } = "values";

    [SugarColumn(ColumnName = "question", ColumnDataType = "text", IsNullable = false)]
    public string Question { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "expected_answer", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string ExpectedAnswer { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "must_memory_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string MustMemoryIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "forbidden_memory_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string ForbiddenMemoryIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "rubric", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Rubric { get; set; } = "[]";

    [SugarColumn(ColumnName = "split", ColumnDataType = "text", IsNullable = false, DefaultValue = "'public'")]
    public string Split { get; set; } = "public";

    [SugarColumn(ColumnName = "active", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "true")]
    public bool Active { get; set; } = true;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

[SugarTable("avatar_eval_runs")]
[SugarIndex("ix_avatar_eval_runs_started", nameof(StartedAt), OrderByType.Desc)]
public sealed class AvatarEvalRunSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false, DefaultValue = "'running'")]
    public string Status { get; set; } = "running";

    [SugarColumn(ColumnName = "total", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Total { get; set; }

    [SugarColumn(ColumnName = "passed", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Passed { get; set; }

    [SugarColumn(ColumnName = "fact_error_count", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int FactErrorCount { get; set; }

    [SugarColumn(ColumnName = "citation_accuracy", ColumnDataType = "double precision", IsNullable = false, DefaultValue = "0")]
    public double CitationAccuracy { get; set; }

    [SugarColumn(ColumnName = "style_score", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int StyleScore { get; set; }

    [SugarColumn(ColumnName = "summary", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Summary { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "started_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset StartedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? CompletedAt { get; set; }
}

[SugarTable("avatar_eval_results")]
[SugarIndex("ix_avatar_eval_results_run", nameof(RunId), OrderByType.Asc)]
public sealed class AvatarEvalResultSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "run_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid RunId { get; set; }

    [SugarColumn(ColumnName = "case_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid CaseId { get; set; }

    [SugarColumn(ColumnName = "question", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Question { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "answer", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Answer { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "citation_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string CitationIds { get; set; } = "[]";

    [SugarColumn(ColumnName = "passed", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "false")]
    public bool Passed { get; set; }

    [SugarColumn(ColumnName = "score", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Score { get; set; }

    [SugarColumn(ColumnName = "detail", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string Detail { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}
