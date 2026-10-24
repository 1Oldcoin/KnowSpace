using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

/// <summary>
/// 候选重排端口。抽成接口是为了将来接真正的 cross-encoder（本地 ONNX 或远端 rerank API）
/// 时不动检索路由：路由只负责"取候选 → 重排 → 判拒答"，算法是可替换的。
/// </summary>
public interface IChunkReranker
{
    /// <summary>返回重排后的前 <paramref name="limit"/> 条；每条命中的 <c>Similarity</c> 已被改写为重排得分。</summary>
    IReadOnlyList<RetrievedChunk> Rerank(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int limit,
        double lexicalWeight = RerankScoring.DefaultLexicalWeight);
}

/// <summary>
/// 默认重排器：**基于查询词覆盖率的确定性重排**（<see cref="RerankScoring"/>）。
///
/// <para>
/// 为什么默认实现不是 cross-encoder：cross-encoder 需要一个专门的 rerank 模型
/// （bge-reranker 一类，数百 MB 到 1 GB+），既不在包体预算里，也没有可复现的下载来源；
/// 而"接了但没验证过"的推理路径比没有更糟——它会在用户机器上以未知方式失败。
/// 覆盖率重排不需要任何模型，效果可测量（D-4 有前后对比），并且能直接命中已知的失败形状。
/// 需要接神经重排时实现 <see cref="IChunkReranker"/> 换掉注册即可。
/// </para>
/// </summary>
public sealed class CoverageReranker : IChunkReranker
{
    public IReadOnlyList<RetrievedChunk> Rerank(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int limit,
        double lexicalWeight = RerankScoring.DefaultLexicalWeight) =>
        RerankScoring.Rank(query, candidates, limit, lexicalWeight).Select(entry => entry.Chunk).ToList();
}
