using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

/// <summary>检索模式：混合（RRF）/ 纯向量 / 纯词法 / 混合后重排。</summary>
public enum RetrievalMode
{
    /// <summary>向量 + 词法 RRF 融合，**不重排**。保留为对照基线。</summary>
    Hybrid = 0,
    Vector = 1,
    Lexical = 2,

    /// <summary>先 RRF 融合，再用 <see cref="IChunkReranker"/> 重排并对低分结果拒答。</summary>
    Rerank = 3
}

public static class RetrievalModes
{
    /// <summary>
    /// 解析查询串里的 <c>mode</c>。返回 <c>null</c> 表示“调用方未指定”，
    /// 由服务端按配置决定默认策略（
    /// <list type="bullet">
    ///   <item><c>vector</c> 纯向量</item>
    ///   <item><c>lexical</c> 纯词法</item>
    ///   <item><c>hybrid</c> 纯 RRF 融合（对照用，不重排）</item>
    ///   <item><c>rerank</c> / <c>hybrid+rerank</c> 融合 + 重排 + 拒答</item>
    /// </list>
    /// **显式**传入 <c>hybrid</c> 不会被配置默默升级成 rerank，否则评估基线会被配置污染。
    /// </summary>
    public static RetrievalMode? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "vector" => RetrievalMode.Vector,
            "lexical" => RetrievalMode.Lexical,
            "hybrid" or "rrf" => RetrievalMode.Hybrid,
            "rerank" or "hybrid+rerank" or "hybrid-rerank" or "hybrid_rerank" => RetrievalMode.Rerank,
            _ => null
        };
    }

    public static string ToWire(this RetrievalMode mode) => mode switch
    {
        RetrievalMode.Vector => ChunkMatchSources.Vector,
        RetrievalMode.Lexical => ChunkMatchSources.Lexical,
        RetrievalMode.Rerank => ChunkMatchSources.Rerank,
        _ => ChunkMatchSources.Hybrid
    };
}

/// <summary>
/// Reciprocal Rank Fusion（RRF）：<c>score(d) = Σ 1 / (k + rank_i(d))</c>，<c>rank</c> 从 1 开始。
///
/// <para>
/// 之所以用 RRF 而不是把余弦相似度和 <c>ts_rank</c> 加权相加：两个分数的量纲完全不同
/// （余弦在 0–1，ts_rank 通常只有 0.0x），任何线性权重都要按语料重新调参；
/// RRF 只看排名，天然免疫量纲问题，也是工业界默认的混合检索融合方式。
/// </para>
///
/// <para>
/// 去重键用 <c>chunkId</c>；老数据没有 chunkId 时退化为"文档 + 切片序号 + 内容长度"，
/// 保证同一段被两路同时召回时只出现一次（并标记为 <see cref="ChunkMatchSources.Hybrid"/>）。
/// </para>
/// </summary>
public static class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    public static IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<RetrievedChunk> vectorChunks,
        IReadOnlyList<RetrievedChunk> lexicalChunks,
        int limit,
        int k = DefaultK)
    {
        if (limit <= 0) return Array.Empty<RetrievedChunk>();
        if (vectorChunks.Count == 0 && lexicalChunks.Count == 0) return Array.Empty<RetrievedChunk>();
        if (lexicalChunks.Count == 0) return Take(vectorChunks, limit, ChunkMatchSources.Vector);
        if (vectorChunks.Count == 0) return Take(lexicalChunks, limit, ChunkMatchSources.Lexical);

        k = Math.Max(1, k);
        var accumulator = new Dictionary<string, FusionEntry>(StringComparer.Ordinal);

        void Accumulate(IReadOnlyList<RetrievedChunk> chunks, bool fromVector)
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var key = KeyOf(chunk);
                var contribution = 1d / (k + index + 1);
                if (accumulator.TryGetValue(key, out var entry))
                {
                    entry.Score += contribution;
                    entry.FromVector |= fromVector;
                    entry.FromLexical |= !fromVector;
                }
                else
                {
                    accumulator[key] = new FusionEntry(chunk, contribution, fromVector, !fromVector);
                }
            }
        }

        Accumulate(vectorChunks, fromVector: true);
        Accumulate(lexicalChunks, fromVector: false);

        return accumulator.Values
            .OrderByDescending(entry => entry.Score)
            // 同分时保留向量链路（语义检索对中文长句更稳），再按切片主键保证顺序稳定。
            .ThenByDescending(entry => entry.FromVector)
            .ThenBy(entry => entry.Chunk.ChunkId)
            .Take(limit)
            .Select(entry => entry.Chunk with
            {
                MatchSource = entry.FromVector && entry.FromLexical
                    ? ChunkMatchSources.Hybrid
                    : entry.FromVector ? ChunkMatchSources.Vector : ChunkMatchSources.Lexical
            })
            .ToList();
    }

    private static string KeyOf(RetrievedChunk chunk) =>
        chunk.ChunkId > 0
            ? $"c:{chunk.ChunkId}"
            : $"d:{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.Content.Length}";

    private static IReadOnlyList<RetrievedChunk> Take(IReadOnlyList<RetrievedChunk> chunks, int limit, string source)
    {
        if (chunks.Count == 0) return Array.Empty<RetrievedChunk>();
        return chunks.Take(limit).Select(chunk => chunk with { MatchSource = source }).ToList();
    }

    private sealed class FusionEntry(RetrievedChunk chunk, double score, bool fromVector, bool fromLexical)
    {
        public RetrievedChunk Chunk { get; } = chunk;
        public double Score { get; set; } = score;
        public bool FromVector { get; set; } = fromVector;
        public bool FromLexical { get; set; } = fromLexical;
    }
}
