using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Application.Knowledge;

public sealed record KnowledgeRetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks,
    Guid? IndexId,
    string? EmbeddingSpecName,
    string? Warning,
    string Strategy = ChunkMatchSources.Vector,
    /// <summary>是否因为"最高相关度不足"而拒答；为 true 时 <see cref="Chunks"/> 为空。</summary>
    bool Abstained = false,
    /// <summary>本次最高重排得分（未重排时为 0），用于把判定结果写进日志与响应。</summary>
    double TopScore = 0,
    /// <summary>本次实际使用的拒答阈值（未启用拒答时为 0）。</summary>
    double AbstainThreshold = 0);

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
///
/// <para>
/// 启用 <see cref="RetrievalOptions.HybridEnabled"/> 时，向量召回会与词法（全文）召回
/// 用 RRF 融合：向量擅长"意思相近"，词法兜住"字面必须出现"的专有名词、编号与报错码。
/// 两路<b>串行</b>执行——同一个 <c>SqlSugarScope</c> 在一个请求上下文里共用一条连接，
/// 并发查询会抛 <c>NpgsqlOperationInProgressException</c>。
/// </para>
/// </summary>
public sealed class RetrievalRouter(
    IKnowledgeIndexStore indexStore,
    IEmbeddingSpecStore specStore,
    IModelBindingStore modelBindings,
    IEmbeddingGeneratorProvider generatorProvider,
    IIndexVectorStore vectorStore,
    IDocumentSearchRepository legacyRepository,
    ILexicalSearchRepository lexicalRepository,
    IChunkReranker reranker,
    IOptions<RetrievalOptions> retrievalOptions,
    ILogger<RetrievalRouter> logger)
{
    /// <summary>
    /// 检索入口。<paramref name="mode"/> 为 <c>null</c> 表示"调用方没指定"，
    /// 由配置决定默认策略（<see cref="RetrievalOptions.RerankEnabled"/>）；
    /// 显式传 <see cref="RetrievalMode.Hybrid"/> 时严格只做 RRF，不会被配置偷偷升级成重排——
    /// 否则评估脚本跑出来的"混合"基线会随线上配置漂移，前后对比失去意义。
    /// </summary>
    public async Task<KnowledgeRetrievalResult> SearchAsync(
        string query,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken,
        RetrievalMode? mode = null)
    {
        var options = retrievalOptions.Value;
        var effective = mode ?? (options.RerankEnabled ? RetrievalMode.Rerank : RetrievalMode.Hybrid);
        var candidates = CandidateCount(limit, options);

        // 重排要看到比 limit 更深的候选池，否则“第 6 名其实更好”永远没机会被救回来。
        // 注意：候选池是**每一路**的召回条数，所以它必须在这里先放大；
        // 只在 fuseTake 上放大没有用——融合只能从已经召回的那几条里挑。
        if (effective == RetrievalMode.Rerank)
            candidates = Math.Max(candidates, Math.Min(options.RerankCandidateLimit, options.MaxCandidates));

        // 纯词法：连向量模型都不需要（模型不可用时也能检索），只跑全文这一路。
        if (effective == RetrievalMode.Lexical)
            return await SearchLexicalAsync(query, knowledgeBaseId, Math.Min(limit, candidates), cancellationToken);

        var vectorResult = await SearchVectorAsync(query, knowledgeBaseId, candidates, cancellationToken);

        // 纯向量模式（或混合被关掉）：向量结果就是最终结果，但仍要按调用方要的条数截断。
        if (effective == RetrievalMode.Vector || !options.HybridEnabled)
        {
            var capped = vectorResult.Chunks.Count > limit
                ? vectorResult.Chunks.Take(limit).ToList()
                : vectorResult.Chunks;
            return vectorResult with { Chunks = capped };
        }

        var lexical = await SearchLexicalAsync(query, knowledgeBaseId, candidates, cancellationToken);

        // 重排需要比 limit 更多的候选来挑，否则"第 6 名其实更好"永远没机会被救回来。
        // 候选池已经在上面按 RerankCandidateLimit 放大过，融合时全部保留。
        var fuseTake = effective == RetrievalMode.Rerank
            ? Math.Max(limit, candidates)
            : limit;

        var fused = ReciprocalRankFusion.Fuse(vectorResult.Chunks, lexical.Chunks, fuseTake, options.RrfK);
        var warning = CombineWarnings(vectorResult.Warning, lexical.Warning);

        if (effective != RetrievalMode.Rerank)
        {
            // 检索方式记为 hybrid；每条命中具体来自哪一路看 RetrievedChunk.MatchSource。
            return new KnowledgeRetrievalResult(
                fused, vectorResult.IndexId, vectorResult.EmbeddingSpecName, warning, ChunkMatchSources.Hybrid);
        }

        var reranked = reranker.Rerank(query, fused, limit, options.RerankLexicalWeight);
        var decision = options.AbstainEnabled
            ? RerankScoring.Decide(options.AbstainMinScore, reranked.Select(entry => entry.Similarity).ToArray())
            : new AbstainDecision(false, reranked.Count > 0 ? reranked.Max(entry => entry.Similarity) : 0, 0);

        if (decision.Abstained)
        {
            logger.LogInformation(
                "Retrieval abstained for query (top score {TopScore:F3} < threshold {Threshold:F3}, {Candidates} candidate(s)).",
                decision.TopScore,
                decision.Threshold,
                fused.Count);

            return new KnowledgeRetrievalResult(
                Array.Empty<RetrievedChunk>(),
                vectorResult.IndexId,
                vectorResult.EmbeddingSpecName,
                CombineWarnings(
                    warning,
                    $"资料里没有找到与该问题足够相关的内容（最高相关度 {RerankScoring.FormatScore(decision.TopScore)}，"
                    + $"低于阈值 {RerankScoring.FormatScore(decision.Threshold)}）。"),
                ChunkMatchSources.Rerank,
                Abstained: true,
                TopScore: decision.TopScore,
                AbstainThreshold: decision.Threshold);
        }

        return new KnowledgeRetrievalResult(
            reranked,
            vectorResult.IndexId,
            vectorResult.EmbeddingSpecName,
            warning,
            ChunkMatchSources.Rerank,
            Abstained: false,
            TopScore: decision.TopScore,
            AbstainThreshold: decision.Threshold);
    }

    /// <summary>向量召回：优先活动索引，没有活动索引时回退旧版直查。</summary>
    private async Task<KnowledgeRetrievalResult> SearchVectorAsync(
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
            return new KnowledgeRetrievalResult(
                chunks.Select(chunk => chunk with { MatchSource = ChunkMatchSources.Vector }).ToList(),
                active.IndexId,
                spec.Name,
                null);
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

    /// <summary>
    /// 词法召回。任何异常都降级为空结果：全文索引缺失（旧库还没补分词）不应该让问答失败，
    /// 但会写进 <c>Warning</c>，方便在界面上看出"这一轮只有向量在起作用"。
    /// </summary>
    private async Task<KnowledgeRetrievalResult> SearchLexicalAsync(
        string query,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken)
    {
        var tsQuery = LexicalTokens.BuildTsQuery(query);
        if (tsQuery is null)
            return new KnowledgeRetrievalResult(Array.Empty<RetrievedChunk>(), null, null, null, ChunkMatchSources.Lexical);

        try
        {
            var chunks = await lexicalRepository.SearchAsync(knowledgeBaseId, tsQuery, limit, cancellationToken);
            return new KnowledgeRetrievalResult(chunks, null, null, null, ChunkMatchSources.Lexical);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Lexical retrieval failed for knowledge base {KnowledgeBaseId}", knowledgeBaseId);
            return new KnowledgeRetrievalResult(Array.Empty<RetrievedChunk>(), null, null,
                "词法检索暂不可用，本轮仅使用向量检索结果。", ChunkMatchSources.Lexical);
        }
    }

    private static int CandidateCount(int limit, RetrievalOptions options)
    {
        var multiplier = Math.Clamp(options.CandidateMultiplier, 1, 10);
        var ceiling = Math.Clamp(options.MaxCandidates, Math.Clamp(limit, 1, 100), 200);
        return Math.Clamp(limit * multiplier, Math.Clamp(limit, 1, 100), ceiling);
    }

    private static string? CombineWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first} {second}";
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
            return new KnowledgeRetrievalResult(
                chunks.Select(chunk => chunk with { MatchSource = ChunkMatchSources.Vector }).ToList(),
                null,
                spec.Name,
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
