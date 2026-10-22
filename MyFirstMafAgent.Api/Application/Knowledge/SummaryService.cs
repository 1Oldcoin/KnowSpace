using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

/// <summary>
/// 摘要服务：基于已发布的分块文本生成摘要，结果按「内容哈希 + 摘要模型指纹」缓存。
/// 该服务不读取、也不修改任何向量索引状态，因此切换摘要模型无需重建索引。
/// </summary>
public sealed class SummaryService(
    ISummaryStore store,
    IDocumentIndexSource documentSource,
    ISummaryModelClient modelClient,
    ILogger<SummaryService> logger)
{
    private const int SummaryPartSize = 6_000;
    private const int SummaryOverlap = 200;

    public Task<SummaryRecord?> GetLatestAsync(Guid documentId, CancellationToken cancellationToken) =>
        store.GetLatestAsync(documentId, cancellationToken);

    /// <summary>后台流程使用：失败只记录日志，不影响资料发布与索引构建。</summary>
    public async Task<SummaryRecord?> TryGenerateAsync(Guid documentId, CancellationToken cancellationToken)
    {
        try
        {
            return await GenerateAsync(documentId, force: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Summary generation failed for document {DocumentId}", documentId);
            return null;
        }
    }

    public async Task<SummaryRecord> GenerateAsync(Guid documentId, bool force, CancellationToken cancellationToken)
    {
        try
        {
            return await GenerateCoreAsync(documentId, force, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // 模型端点/密钥等配置问题属于可修正错误，转换为 409 而不是 500。
            throw new ConflictException($"摘要生成失败：{exception.Message}");
        }
    }

    private async Task<SummaryRecord> GenerateCoreAsync(Guid documentId, bool force, CancellationToken cancellationToken)
    {
        var document = await documentSource.GetPublishedDocumentAsync(documentId, cancellationToken)
            ?? throw new NotFoundException($"资料 {documentId} 不存在。");
        if (document.ContentVersion <= 0 || string.IsNullOrWhiteSpace(document.ContentHash))
            throw new ConflictException("资料尚未完成解析，无法生成摘要。");

        var modelFingerprint = await modelClient.GetModelFingerprintAsync(document.KnowledgeBaseId, cancellationToken);
        if (!force)
        {
            var cached = await store.FindAsync(documentId, document.ContentHash, modelFingerprint, cancellationToken);
            if (cached is not null)
            {
                await store.SetDisplaySummaryAsync(documentId, cached.Summary, cancellationToken);
                return cached;
            }
        }

        var chunks = await documentSource.GetPublishedChunksAsync(documentId, cancellationToken);
        if (chunks.Count == 0)
            throw new ConflictException("资料没有已发布的分块文本，无法生成摘要。");

        var parts = SplitText(string.Join("\n\n", chunks.Select(chunk => chunk.Content)), SummaryPartSize, SummaryOverlap);
        var partialSummaries = new List<string>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var partial = await modelClient.CompleteAsync(
                document.KnowledgeBaseId,
                "你是知识库整理助手。根据给定资料用中文准确提炼内容；不要补充原文没有的信息。",
                $"请总结以下资料片段，保留核心主题、关键步骤、结论和重要名词。片段 {index + 1}/{parts.Count}：\n\n{parts[index]}",
                cancellationToken);
            partialSummaries.Add(partial.Trim());
        }

        var summary = partialSummaries.Count == 1
            ? partialSummaries[0]
            : await modelClient.CompleteAsync(
                document.KnowledgeBaseId,
                "你是知识库整理助手。请把同一资料的分段摘要合并为一份完整、去重、结构清楚的中文摘要，不要臆测。",
                string.Join("\n\n", partialSummaries.Select((item, index) => $"分段摘要 {index + 1}：\n{item}")),
                cancellationToken);

        var record = new SummaryRecord(
            Guid.NewGuid(),
            documentId,
            document.ContentHash,
            modelFingerprint,
            summary.Trim(),
            DateTimeOffset.UtcNow);
        await store.SaveAsync(record, cancellationToken);
        await store.SetDisplaySummaryAsync(documentId, record.Summary, cancellationToken);
        return record;
    }

    private static List<string> SplitText(string text, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > 0)
        {
            var length = Math.Min(chunkSize, remaining.Length);
            if (length < remaining.Length)
            {
                var boundary = remaining.LastIndexOfAny(['\n', '。', '！', '？', '.', '!', '?', ' '],
                    length - 1, Math.Min(length, 300));
                if (boundary > chunkSize / 2) length = boundary + 1;
            }

            var chunk = remaining[..length].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            if (length >= remaining.Length) break;
            remaining = remaining[Math.Max(1, length - overlap)..].TrimStart();
        }
        return chunks;
    }
}
