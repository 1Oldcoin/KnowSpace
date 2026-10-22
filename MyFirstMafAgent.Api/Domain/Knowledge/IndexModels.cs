namespace MyFirstMafAgent.Api.Domain.Knowledge;

public static class KnowledgeIndexStatuses
{
    /// <summary>阴影索引：正在全量重建或追赶变更日志，线上查询不会读取它。</summary>
    public const string Building = "Building";

    /// <summary>索引已构建并通过一致性检查，等待人工切换。</summary>
    public const string Ready = "Ready";

    /// <summary>当前线上索引。</summary>
    public const string Active = "Active";

    /// <summary>已下线但仍在回滚窗口内的索引。</summary>
    public const string Retired = "Retired";

    public const string Failed = "Failed";
}

public sealed record KnowledgeIndex(
    Guid Id,
    Guid KnowledgeBaseId,
    Guid EmbeddingSpecId,
    string EmbeddingFingerprint,
    int Dimensions,
    string Name,
    string Status,
    bool ActivateWhenReady,
    int BuildProgress,
    string? BuildStage,
    string? FailureReason,
    long LastAppliedEventId,
    DateTimeOffset? LastFullRebuildAt,
    DateTimeOffset? ConsistencyCheckedAt,
    double? QualityScore,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? RetiredAt)
{
    public bool IsShadow => Status is KnowledgeIndexStatuses.Building or KnowledgeIndexStatuses.Ready;
}

/// <summary>线上查询必须原子读取的活动索引快照。</summary>
public sealed record ActiveIndex(
    Guid KnowledgeBaseId,
    Guid IndexId,
    Guid EmbeddingSpecId,
    string EmbeddingFingerprint,
    int Dimensions,
    string IndexStatus,
    long LastAppliedEventId);

/// <summary>索引构建/重放依赖的已发布资料快照。</summary>
public sealed record IndexSourceDocument(
    Guid DocumentId,
    Guid KnowledgeBaseId,
    string FileName,
    string Extension,
    int ContentVersion,
    string? ContentHash,
    string Status);

public sealed record IndexSourceChunk(long ChunkId, Guid DocumentId, int DocumentVersion, int ChunkIndex, string Content, string? ContentHash);

public sealed record IndexVector(long ChunkId, Guid DocumentId, int DocumentVersion, int ChunkIndex, float[] Embedding);

public sealed record IndexVectorStats(long VectorCount, long DocumentCount, long ChunkCount);

public sealed record IndexConsistencyReport(
    Guid IndexId,
    bool IsConsistent,
    long ExpectedChunkCount,
    long IndexedVectorCount,
    long MissingVectorCount,
    long OrphanVectorCount,
    long StaleVersionVectorCount,
    int DimensionMismatchCount,
    IReadOnlyList<string> Issues)
{
    public static IndexConsistencyReport Failed(Guid indexId, string issue) => new(
        indexId, false, 0, 0, 0, 0, 0, 0, [issue]);
}

public sealed record IndexQualityReport(
    Guid IndexId,
    double Score,
    int ProbeCount,
    int HitCount,
    long CoverageNumerator,
    long CoverageDenominator);

public static class OutboxEventTypes
{
    public const string DocumentPublished = "DocumentPublished";
    public const string DocumentDeleted = "DocumentDeleted";
}

public sealed record OutboxEventDraft(
    string EventType,
    Guid KnowledgeBaseId,
    Guid? DocumentId,
    int? DocumentVersion,
    string? ContentHash,
    string IdempotencyKey,
    string? Payload = null);

public sealed record OutboxEvent(
    long Id,
    string EventType,
    Guid KnowledgeBaseId,
    Guid? DocumentId,
    int? DocumentVersion,
    string? ContentHash,
    string IdempotencyKey,
    string? Payload,
    DateTimeOffset OccurredAt);
