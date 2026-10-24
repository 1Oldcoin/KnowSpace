namespace MyFirstMafAgent.Api.Domain.Notes;

/// <summary>
/// 笔记引用的一条资料依据：定位到“哪份资料的哪一段”，并带上当时的相似度与片段。
///
/// <para>
/// 存下来而不是只留 <c>SourceDocumentId</c>，是因为笔记常常是“从问答里带出来的结论”，
/// 半年后重读时需要的不是“这篇笔记关于某个文件”，而是“这句话出自那一段”。
/// </para>
/// </summary>
/// <param name="DocumentId">资料 ID。</param>
/// <param name="FileName">资料文件名（展示用）。</param>
/// <param name="ChunkIndex">切片在资料内的序号；-1 表示未知（例如手工指定文件）。</param>
/// <param name="Similarity">检索相似度，0 表示不适用（手工引用）。</param>
/// <param name="Snippet">切片片段（已截断），用于在笔记里直接展示依据。</param>
public sealed record NoteSource(
    Guid DocumentId,
    string FileName,
    int ChunkIndex = -1,
    double Similarity = 0,
    string? Snippet = null);
