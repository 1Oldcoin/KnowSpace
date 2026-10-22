namespace MyFirstMafAgent.Api.Domain.Knowledge;

public interface IEmbeddingSpecStore
{
    Task<IReadOnlyList<EmbeddingSpec>> ListAsync(CancellationToken cancellationToken);

    Task<EmbeddingSpec?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<EmbeddingSpec?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken);

    Task<EmbeddingSpec> UpsertAsync(EmbeddingSpecDraft draft, CancellationToken cancellationToken);
}

/// <summary>把向量规格实例化为可复用的生成器；相同指纹的生成器只创建一次。</summary>
public interface IEmbeddingGeneratorProvider
{
    IEmbeddingGenerator GetOrCreate(EmbeddingSpec spec);
}

public interface IKnowledgeIndexStore
{
    Task<IReadOnlyList<KnowledgeIndex>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task<KnowledgeIndex?> GetAsync(Guid indexId, CancellationToken cancellationToken);

    Task<KnowledgeIndex> CreateAsync(KnowledgeIndex index, CancellationToken cancellationToken);

    Task UpdateStatusAsync(Guid indexId, string status, string? failureReason, CancellationToken cancellationToken);

    Task ResetForRebuildAsync(Guid indexId, CancellationToken cancellationToken);

    Task SetActivateWhenReadyAsync(Guid indexId, bool activateWhenReady, CancellationToken cancellationToken);

    Task UpdateProgressAsync(Guid indexId, int progress, string stage, CancellationToken cancellationToken);

    Task SetLastAppliedEventAsync(Guid indexId, long eventId, CancellationToken cancellationToken);

    Task MarkBuiltAsync(Guid indexId, long checkpointEventId, CancellationToken cancellationToken);

    Task SetConsistencyAsync(Guid indexId, bool consistent, CancellationToken cancellationToken);

    Task SetQualityAsync(Guid indexId, double score, CancellationToken cancellationToken);

    Task<ActiveIndex?> GetActiveAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task<ActiveIndex> ActivateAsync(Guid indexId, CancellationToken cancellationToken);

    Task<ActiveIndex?> RollbackAsync(Guid knowledgeBaseId, DateTimeOffset retiredAfter, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeIndex>> ListPendingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeIndex>> ListActiveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeIndex>> ListRetiredBeforeAsync(Guid knowledgeBaseId, DateTimeOffset cutoff, CancellationToken cancellationToken);

    Task DeleteAsync(Guid indexId, CancellationToken cancellationToken);
}

public interface IIndexVectorStore
{
    Task DeleteIndexVectorsAsync(Guid indexId, CancellationToken cancellationToken);

    Task DeleteDocumentVectorsAsync(Guid indexId, Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlySet<long>> GetIndexedChunkIdsAsync(Guid indexId, Guid documentId, CancellationToken cancellationToken);

    Task UpsertAsync(Guid indexId, IReadOnlyList<IndexVector> vectors, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid indexId,
        float[] embedding,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken);

    Task<IndexVectorStats> GetStatsAsync(Guid indexId, CancellationToken cancellationToken);

    Task<long> GetChunkVectorCountAsync(Guid indexId, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> GetVectorDimensionsAsync(Guid indexId, CancellationToken cancellationToken);
}

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxEvent>> ReadAfterAsync(long checkpoint, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxEvent>> ReadKnowledgeBaseAfterAsync(Guid knowledgeBaseId, long checkpoint, int limit, CancellationToken cancellationToken);

    Task<long> GetMaxIdAsync(CancellationToken cancellationToken);

    Task<long> GetKnowledgeBaseMaxIdAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task DeleteConsumedBeforeAsync(long maxAppliedEventId, DateTimeOffset olderThan, CancellationToken cancellationToken);
}

/// <summary>索引构建只读取「已发布版本」的资料与切片，绝不读取解析中的临时数据。</summary>
public interface IDocumentIndexSource
{
    /// <summary>把升级前已 Ready 但没有版本号的资料提升为版本 1，并补发 Outbox 事件。</summary>
    Task<int> BackfillLegacyVersionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListKnowledgeBaseIdsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexSourceDocument>> ListPublishedDocumentsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task<IndexSourceDocument?> GetPublishedDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexSourceChunk>> GetPublishedChunksAsync(Guid documentId, CancellationToken cancellationToken);

    Task<int> CountPublishedChunksAsync(Guid documentId, CancellationToken cancellationToken);
}
