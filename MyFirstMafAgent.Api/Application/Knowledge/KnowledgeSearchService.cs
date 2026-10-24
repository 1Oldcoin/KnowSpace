using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

/// <summary>知识库语义检索应用服务；实际向量化与索引选择由 <see cref="RetrievalRouter"/> 负责。</summary>
public sealed class KnowledgeSearchService(
    RetrievalRouter router,
    IKnowledgeBaseDirectory knowledgeBases)
{
    public const int MaxLimit = 20;

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken) =>
        (await SearchWithDiagnosticsAsync(query, knowledgeBaseId, limit, cancellationToken)).Chunks;

    /// <summary>
    /// 带诊断的检索。<paramref name="mode"/> 只影响本次检索：默认（<c>null</c>）按配置走
    /// “混合 + 重排”，显式 <c>hybrid</c> 只做 RRF 融合，评估集用它跑四条基线做对比。
    /// </summary>
    public async Task<KnowledgeRetrievalResult> SearchWithDiagnosticsAsync(
        string query,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken,
        RetrievalMode? mode = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("检索关键词不能为空。", nameof(query));
        if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken))
            throw new KnowledgeBaseNotFoundException(knowledgeBaseId);

        return await router.SearchAsync(query.Trim(), knowledgeBaseId, Math.Clamp(limit, 1, MaxLimit), cancellationToken, mode);
    }
}
