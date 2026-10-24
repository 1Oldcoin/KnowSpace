namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>
/// 检索命中的资料片段（领域只读模型）。
/// <para>
/// <c>ChunkId</c>/<c>ChunkIndex</c> 是引用元数据的基础：只有能唯一定位到“哪一份资料的哪一段”，
/// 引用才能回溯原文。旧版向量检索（kb_chunks 直查）同样会回填这两个字段。
/// </para>
/// <para>
/// <c>MatchSource</c> 记录这条命中来自哪一路召回（向量 / 词法 / 两路都有），
/// 混合检索融合后仍然可以解释“为什么这条排在这里”。
/// </para>
/// </summary>
public sealed record RetrievedChunk(
    Guid DocumentId,
    string FileName,
    string Content,
    double Similarity,
    long ChunkId = 0,
    int ChunkIndex = -1,
    string? MatchSource = null);

/// <summary>命中来源常量：随引用一起透出，便于排查融合效果。</summary>
public static class ChunkMatchSources
{
    public const string Vector = "vector";
    public const string Lexical = "lexical";
    public const string Hybrid = "hybrid";

    /// <summary>融合后经过重排。单条命中的 <c>MatchSource</c> 仍记录它来自哪一路召回。</summary>
    public const string Rerank = "rerank";
}

/// <summary>资料全文中的一个切片，用于重建原文阅读视图。</summary>
public sealed record DocumentChunk(int Index, string Content);

/// <summary>资料的原文文本与元信息。</summary>
public sealed record DocumentText(Guid DocumentId, string FileName, string Extension, IReadOnlyList<DocumentChunk> Chunks);

/// <summary>知识库向量检索端口。</summary>
public interface IDocumentSearchRepository
{
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid knowledgeBaseId,
        float[] embedding,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>资料原文读取端口。</summary>
public interface IDocumentTextRepository
{
    Task<DocumentText?> GetTextAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// 词法（全文）检索端口：输入 <c>to_tsquery</c> 表达式，按 <c>ts_rank</c> 返回命中切片。
/// <para>
/// 与向量检索互补——向量擅长“意思相近”，词法擅长“字面必须出现”
/// （人名、编号、报错码、罕见术语）。两路结果由 <c>HybridRetriever</c> 用 RRF 融合。
/// </para>
/// </summary>
public interface ILexicalSearchRepository
{
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid knowledgeBaseId,
        string tsQuery,
        int limit,
        CancellationToken cancellationToken);
}
