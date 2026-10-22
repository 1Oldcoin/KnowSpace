using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

public sealed record KnowledgeRetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks,
    Guid? IndexId,
    string? EmbeddingSpecName,
    string? Warning);

public sealed record RetrievalStatus(
    bool HasActiveIndex,
    Guid? IndexId,
    Guid? EmbeddingSpecId,
    string? EmbeddingSpecName,
    int Dimensions,
    long LastAppliedEventId,
    string? Warning);

/// <summary>
/// 检索路由：原子读取活动索引配置 → 用匹配的向量模型生成查询向量 → 指纹/维度校验 → 检索。
/// 指纹不一致时绝不允许跨模型检索，避免不同向量空间混用导致错误结果。
/// </summary>
public sealed class RetrievalRouter(
    IKnowledgeIndexStore indexStore,
    IEmbeddingSpecStore specStore,
    IModelBindingStore modelBindings,
    IEmbeddingGeneratorProvider generatorProvider,
    IIndexVectorStore vectorStore,
    IDocumentSearchRepository legacyRepository,
    ILogger<RetrievalRouter> logger)
{
    public async Task<KnowledgeRetrievalResult> SearchAsync(
        string query,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken)
    {
        var active = await indexStore.GetActiveAsync(knowledgeBaseId, cancellationToken);
        if (active is null)
            return await SearchLegacyAsync(query, knowledgeBaseId, limit, cancellationToken);

        try
        {
            var spec = await specStore.GetAsync(active.EmbeddingSpecId, cancellationToken);
            if (spec is null)
                return Empty("活动索引引用的向量规格不存在，请重建索引。");

            var validationError = Validate(active, spec);
            if (validationError is not null)
            {
                logger.LogError("Active index {IndexId} is not usable: {Error}", active.IndexId, validationError);
                return Empty(validationError);
            }

            var generator = generatorProvider.GetOrCreate(spec);
            var embedding = await generator.CreateEmbeddingAsync(query, cancellationToken);
            if (embedding.Length != spec.Dimensions)
                return Empty($"查询向量维度 {embedding.Length} 与索引维度 {spec.Dimensions} 不一致。");

            var chunks = await vectorStore.SearchAsync(active.IndexId, embedding, knowledgeBaseId, limit, cancellationToken);
            return new KnowledgeRetrievalResult(chunks, active.IndexId, spec.Name, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 模型服务暂时不可用时不让问答整体失败，返回明确的降级原因。
            logger.LogError(exception, "Retrieval failed for active index {IndexId}", active.IndexId);
            return Empty($"检索暂时不可用：{exception.Message}");
        }
    }

    public async Task<RetrievalStatus> GetStatusAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var active = await indexStore.GetActiveAsync(knowledgeBaseId, cancellationToken);
        if (active is null)
        {
            var specId = await modelBindings.GetEmbeddingSpecIdAsync(knowledgeBaseId, cancellationToken);
            return new RetrievalStatus(false, null, specId, null, 0, 0,
                "知识库尚无活动索引，可能正在构建或使用兼容索引。");
        }

        var spec = await specStore.GetAsync(active.EmbeddingSpecId, cancellationToken);
        var index = await indexStore.GetAsync(active.IndexId, cancellationToken);
        return new RetrievalStatus(true, active.IndexId, active.EmbeddingSpecId, spec?.Name,
            active.Dimensions, index?.LastAppliedEventId ?? 0, null);
    }

    private async Task<KnowledgeRetrievalResult> SearchLegacyAsync(
        string query,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var specs = await specStore.ListAsync(cancellationToken);
            var spec = specs.FirstOrDefault();
            if (spec is null) return Empty("尚未注册任何向量模型规格，无法检索。");

            var generator = generatorProvider.GetOrCreate(spec);
            var embedding = await generator.CreateEmbeddingAsync(query, cancellationToken);
            var chunks = await legacyRepository.SearchAsync(knowledgeBaseId, embedding, limit, cancellationToken);
            return new KnowledgeRetrievalResult(chunks, null, spec.Name,
                chunks.Count == 0 ? "知识库索引正在构建，当前没有可检索的已完成内容。" : "当前使用兼容索引，建议创建并切换新索引。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Legacy retrieval failed for knowledge base {KnowledgeBaseId}", knowledgeBaseId);
            return Empty($"检索暂时不可用：{exception.Message}");
        }
    }

    private static string? Validate(ActiveIndex active, EmbeddingSpec spec)
    {
        if (!string.Equals(active.EmbeddingFingerprint, spec.Fingerprint, StringComparison.Ordinal))
            return $"活动索引指纹 {active.EmbeddingFingerprint} 与向量规格指纹 {spec.Fingerprint} 不一致，已阻止跨模型检索。";
        if (active.Dimensions != spec.Dimensions)
            return $"活动索引维度 {active.Dimensions} 与向量规格维度 {spec.Dimensions} 不一致。";
        return null;
    }

    private static KnowledgeRetrievalResult Empty(string warning) =>
        new([], null, null, warning);
}
