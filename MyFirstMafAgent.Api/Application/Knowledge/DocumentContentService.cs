using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

public sealed record DocumentContentPage(
    Guid DocumentId,
    string FileName,
    string Extension,
    string Content,
    int TotalLength,
    int Offset,
    bool HasMore,
    int ChunkCount);

/// <summary>
/// 资料原文阅读服务：把已解析的切片按顺序拼回连续文本，并按字符偏移分页返回，
/// 避免一次性把长篇资料全部推送到前端。
/// </summary>
public sealed class DocumentContentService(IDocumentTextRepository repository)
{
    private const string ChunkSeparator = "\n\n";
    public const int DefaultPageSize = 8_000;
    public const int MaxPageSize = 40_000;

    public async Task<DocumentContentPage> GetPageAsync(
        Guid documentId,
        int offset,
        int length,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetTextAsync(documentId, cancellationToken)
            ?? throw new DocumentNotFoundException(documentId);

        var fullText = string.Join(ChunkSeparator, document.Chunks.Select(chunk => chunk.Content));
        var safeOffset = Math.Clamp(offset, 0, fullText.Length);
        var safeLength = Math.Clamp(length <= 0 ? DefaultPageSize : length, 1, MaxPageSize);
        var remaining = fullText.Length - safeOffset;
        var take = Math.Min(safeLength, remaining);
        var content = fullText.Substring(safeOffset, take);

        return new DocumentContentPage(
            document.DocumentId,
            document.FileName,
            document.Extension,
            content,
            fullText.Length,
            safeOffset,
            safeOffset + take < fullText.Length,
            document.Chunks.Count);
    }
}

public sealed class DocumentNotFoundException(Guid documentId) : NotFoundException($"资料 {documentId} 不存在。")
{
    public Guid DocumentId { get; } = documentId;
}
