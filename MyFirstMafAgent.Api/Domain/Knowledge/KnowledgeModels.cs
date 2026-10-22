namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>检索命中的资料片段（领域只读模型）。</summary>
public sealed record RetrievedChunk(Guid DocumentId, string FileName, string Content, double Similarity);

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
