using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

[SugarTable("kb_knowledge_bases")]
public sealed class KnowledgeBaseSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("kb_documents")]
[SugarIndex("ix_kb_documents_knowledge_base_id", nameof(KnowledgeBaseId), OrderByType.Asc, false)]
[SugarIndex("ix_kb_documents_status_created_at", nameof(Status), OrderByType.Asc, nameof(CreatedAt), OrderByType.Asc, false)]
public sealed class KnowledgeDocumentSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "topic_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? TopicId { get; set; }

    [SugarColumn(ColumnName = "file_name", ColumnDataType = "text", IsNullable = false)]
    public string FileName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "extension", ColumnDataType = "text", IsNullable = false)]
    public string Extension { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content_type", ColumnDataType = "text", IsNullable = false)]
    public string ContentType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "size_bytes", ColumnDataType = "bigint", IsNullable = false)]
    public long SizeBytes { get; set; }

    [SugarColumn(ColumnName = "storage_path", ColumnDataType = "text", IsNullable = false)]
    public string StoragePath { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false)]
    public string Status { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "error", ColumnDataType = "text", IsNullable = true)]
    public string? Error { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "summary", ColumnDataType = "text", IsNullable = true)]
    public string? Summary { get; set; }

    [SugarColumn(ColumnName = "progress", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Progress { get; set; }

    [SugarColumn(ColumnName = "processing_stage", ColumnDataType = "text", IsNullable = true)]
    public string? ProcessingStage { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>原始文件 SHA-256，用于重复上传检测与审计。</summary>
    [SugarColumn(ColumnName = "file_hash", ColumnDataType = "text", IsNullable = true)]
    public string? FileHash { get; set; }

    /// <summary>当前已发布解析文本的 SHA-256；内容不变时不会产生新分块版本。</summary>
    [SugarColumn(ColumnName = "content_hash", ColumnDataType = "text", IsNullable = true)]
    public string? ContentHash { get; set; }

    /// <summary>当前已发布分块版本号，从 0 开始；0 表示尚未发布。</summary>
    [SugarColumn(ColumnName = "content_version", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int ContentVersion { get; set; }
}

[SugarTable("kb_chunks")]
[SugarIndex("ix_kb_chunks_document_id", nameof(DocumentId), OrderByType.Asc, false)]
[SugarIndex("ux_kb_chunks_document_version_chunk_index", nameof(DocumentId), OrderByType.Asc, nameof(DocumentVersion), OrderByType.Asc, nameof(ChunkIndex), OrderByType.Asc, true)]
public sealed class KnowledgeChunkSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid DocumentId { get; set; }

    /// <summary>所属分块版本；不同版本的切片可以并存，线上只读取当前版本。</summary>
    [SugarColumn(ColumnName = "document_version", ColumnDataType = "integer", IsNullable = false, DefaultValue = "1")]
    public int DocumentVersion { get; set; }

    [SugarColumn(ColumnName = "chunk_index", ColumnDataType = "integer", IsNullable = false)]
    public int ChunkIndex { get; set; }

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content_hash", ColumnDataType = "text", IsNullable = true)]
    public string? ContentHash { get; set; }

    // 分块与向量化解耦：切片写入时不再携带向量，向量由索引生命周期单独生成（kb_index_vectors）。
    // 仅历史兼容列保留；该列是 pgvector 的 vector 类型，Npgsql 无法直接映射为 string，
    // 因此任何查询都必须显式投影（禁止 Queryable<KnowledgeChunkSchemaEntity>().ToListAsync() 整行读取）。
    // 注意：不要给该列设置 SugarColumn.InsertSql。SqlSugar 只会对单行插入做值替换，
    // 批量插入（Insertable(List<T>)）会把 InsertSql 原样拼进 SQL，导致
    // 42703: 字段 "embedding" 不存在。切片写入必须忽略该列（见 KnowledgeBaseStore.PublishChunksAsync）。
    [SugarColumn(ColumnName = "embedding", ColumnDataType = "vector", IsNullable = true)]
    public string? Embedding { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }
}

[SugarTable("kb_upload_sessions")]
[SugarIndex("ix_kb_upload_sessions_knowledge_base_id", nameof(KnowledgeBaseId), OrderByType.Asc, nameof(CreatedAt), OrderByType.Desc, false)]
public sealed class KnowledgeUploadSessionSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "file_name", ColumnDataType = "text", IsNullable = false)]
    public string FileName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "extension", ColumnDataType = "text", IsNullable = false)]
    public string Extension { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content_type", ColumnDataType = "text", IsNullable = false)]
    public string ContentType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "size_bytes", ColumnDataType = "bigint", IsNullable = false)]
    public long SizeBytes { get; set; }

    [SugarColumn(ColumnName = "uploaded_bytes", ColumnDataType = "bigint", IsNullable = false, DefaultValue = "0")]
    public long UploadedBytes { get; set; }

    [SugarColumn(ColumnName = "staging_path", ColumnDataType = "text", IsNullable = false)]
    public string StagingPath { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false)]
    public string Status { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "document_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? DocumentId { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}
