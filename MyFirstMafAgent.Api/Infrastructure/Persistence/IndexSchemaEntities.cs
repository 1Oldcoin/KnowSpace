using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>向量模型规格表。指纹唯一，保证同一模型参数不会被重复注册。</summary>
[SugarTable("kb_embedding_specs")]
[SugarIndex("ux_kb_embedding_specs_fingerprint", nameof(Fingerprint), OrderByType.Asc, true)]
public sealed class EmbeddingSpecSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "provider", ColumnDataType = "text", IsNullable = false)]
    public string Provider { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "model", ColumnDataType = "text", IsNullable = false)]
    public string Model { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dimensions", ColumnDataType = "integer", IsNullable = false)]
    public int Dimensions { get; set; }

    [SugarColumn(ColumnName = "max_sequence_length", ColumnDataType = "integer", IsNullable = false)]
    public int MaxSequenceLength { get; set; }

    [SugarColumn(ColumnName = "revision", ColumnDataType = "text", IsNullable = false)]
    public string Revision { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "fingerprint", ColumnDataType = "text", IsNullable = false)]
    public string Fingerprint { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "config_json", ColumnDataType = "text", IsNullable = true)]
    public string? ConfigJson { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>知识库与模型规格的绑定。没有绑定时使用系统默认规格。</summary>
[SugarTable("kb_model_bindings")]
public sealed class ModelBindingSchemaEntity
{
    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "embedding_spec_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? EmbeddingSpecId { get; set; }

    [SugarColumn(ColumnName = "summary_settings", ColumnDataType = "text", IsNullable = true)]
    public string? SummarySettings { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>资料的分块版本。每次内容哈希变化产生新版本，旧版本保留用于回滚与审计。</summary>
[SugarTable("kb_document_versions")]
[SugarIndex("ux_kb_document_versions_document_version", nameof(DocumentId), OrderByType.Asc, nameof(Version), OrderByType.Asc, true)]
public sealed class DocumentVersionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid DocumentId { get; set; }

    [SugarColumn(ColumnName = "version", ColumnDataType = "integer", IsNullable = false)]
    public int Version { get; set; }

    [SugarColumn(ColumnName = "content_hash", ColumnDataType = "text", IsNullable = false)]
    public string ContentHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "chunk_count", ColumnDataType = "integer", IsNullable = false)]
    public int ChunkCount { get; set; }

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false)]
    public string Status { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>索引实例：一个知识库可以同时存在活动索引与多个阴影/历史索引。</summary>
[SugarTable("kb_indexes")]
[SugarIndex("ix_kb_indexes_knowledge_base_status", nameof(KnowledgeBaseId), OrderByType.Asc, nameof(Status), OrderByType.Asc, false)]
[SugarIndex("ix_kb_indexes_status_updated_at", nameof(Status), OrderByType.Asc, nameof(UpdatedAt), OrderByType.Asc, false)]
public sealed class KnowledgeIndexSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "embedding_spec_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid EmbeddingSpecId { get; set; }

    [SugarColumn(ColumnName = "embedding_fingerprint", ColumnDataType = "text", IsNullable = false)]
    public string EmbeddingFingerprint { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dimensions", ColumnDataType = "integer", IsNullable = false)]
    public int Dimensions { get; set; }

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false)]
    public string Status { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "activate_when_ready", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "false")]
    public bool ActivateWhenReady { get; set; }

    [SugarColumn(ColumnName = "build_progress", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int BuildProgress { get; set; }

    [SugarColumn(ColumnName = "build_stage", ColumnDataType = "text", IsNullable = true)]
    public string? BuildStage { get; set; }

    [SugarColumn(ColumnName = "failure_reason", ColumnDataType = "text", IsNullable = true)]
    public string? FailureReason { get; set; }

    [SugarColumn(ColumnName = "last_applied_event_id", ColumnDataType = "int8", IsNullable = false, DefaultValue = "0")]
    public long LastAppliedEventId { get; set; }

    [SugarColumn(ColumnName = "last_full_rebuild_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? LastFullRebuildAt { get; set; }

    [SugarColumn(ColumnName = "consistency_checked_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? ConsistencyCheckedAt { get; set; }

    [SugarColumn(ColumnName = "quality_score", ColumnDataType = "float8", IsNullable = true)]
    public double? QualityScore { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    [SugarColumn(ColumnName = "activated_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? ActivatedAt { get; set; }

    [SugarColumn(ColumnName = "retired_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>活动索引绑定。切换索引只需要在事务中更新这一行，读路径永远原子读取。</summary>
[SugarTable("kb_active_indexes")]
public sealed class ActiveIndexSchemaEntity
{
    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "index_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? IndexId { get; set; }

    // 检索路由只读这一行即可获得完整检索参数，切换索引在单个事务内完成。
    [SugarColumn(ColumnName = "embedding_spec_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? EmbeddingSpecId { get; set; }

    [SugarColumn(ColumnName = "embedding_fingerprint", ColumnDataType = "text", IsNullable = true)]
    public string? EmbeddingFingerprint { get; set; }

    [SugarColumn(ColumnName = "dimensions", ColumnDataType = "integer", IsNullable = true)]
    public int? Dimensions { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>每个索引独立的向量存储。embedding 使用无维度 vector，索引之间互不影响。</summary>
[SugarTable("kb_index_vectors")]
[SugarIndex("ux_kb_index_vectors_index_chunk", nameof(IndexId), OrderByType.Asc, nameof(ChunkId), OrderByType.Asc, true)]
[SugarIndex("ix_kb_index_vectors_index_document", nameof(IndexId), OrderByType.Asc, nameof(DocumentId), OrderByType.Asc, false)]
public sealed class IndexVectorSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "index_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid IndexId { get; set; }

    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid DocumentId { get; set; }

    [SugarColumn(ColumnName = "document_version", ColumnDataType = "integer", IsNullable = false)]
    public int DocumentVersion { get; set; }

    [SugarColumn(ColumnName = "chunk_id", ColumnDataType = "int8", IsNullable = false)]
    public long ChunkId { get; set; }

    [SugarColumn(ColumnName = "chunk_index", ColumnDataType = "integer", IsNullable = false)]
    public int ChunkIndex { get; set; }

    // 无维度 vector 列允许不同模型规格的索引共存；查询始终按 index_id 隔离。
    // 注意：该列为 pgvector vector 类型，Npgsql 无法映射为 string/Object，
    // 读取时必须显式 Select 需要的列（投影），禁止整行 ToListAsync/FirstAsync。
    [SugarColumn(ColumnName = "embedding", ColumnDataType = "vector", IsNullable = true, InsertSql = PgVectorSql.EmbeddingInsertCast)]
    public string? Embedding { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>事务性 Outbox。资料发布/删除与事件写入在同一个数据库事务内完成。</summary>
[SugarTable("kb_outbox_events")]
[SugarIndex("ux_kb_outbox_events_idempotency", nameof(IdempotencyKey), OrderByType.Asc, true)]
[SugarIndex("ix_kb_outbox_events_knowledge_base_id", nameof(KnowledgeBaseId), OrderByType.Asc, nameof(Id), OrderByType.Asc, false)]
public sealed class OutboxEventSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "event_type", ColumnDataType = "text", IsNullable = false)]
    public string EventType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? DocumentId { get; set; }

    [SugarColumn(ColumnName = "document_version", ColumnDataType = "integer", IsNullable = true)]
    public int? DocumentVersion { get; set; }

    [SugarColumn(ColumnName = "content_hash", ColumnDataType = "text", IsNullable = true)]
    public string? ContentHash { get; set; }

    [SugarColumn(ColumnName = "idempotency_key", ColumnDataType = "text", IsNullable = false)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "payload", ColumnDataType = "text", IsNullable = true)]
    public string? Payload { get; set; }

    [SugarColumn(ColumnName = "occurred_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>按「资料 + 内容哈希 + 摘要模型指纹」缓存的摘要，与向量索引完全解耦。</summary>
[SugarTable("kb_document_summaries")]
[SugarIndex("ux_kb_document_summaries_document_model", nameof(DocumentId), OrderByType.Asc, nameof(ContentHash), OrderByType.Asc, nameof(ModelFingerprint), OrderByType.Asc, true)]
public sealed class DocumentSummarySchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid DocumentId { get; set; }

    [SugarColumn(ColumnName = "content_hash", ColumnDataType = "text", IsNullable = false)]
    public string ContentHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "model_fingerprint", ColumnDataType = "text", IsNullable = false)]
    public string ModelFingerprint { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "summary", ColumnDataType = "text", IsNullable = false)]
    public string Summary { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}
