using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 聊天式投喂的唯一入口。v2 只负责“接收并排队”：
/// 原文先落库，后台 Worker 再整理成记忆，避免长文件阻塞问答。
/// 用户仍然不需要选择类型、写标题、填标签或按任何模板组织内容。
/// </summary>
public sealed class AvatarIntakeService(
    IAvatarRepository repository,
    AvatarIngestionService ingestion,
    AvatarMemoryIndexer indexer,
    AvatarMemoryRetriever retriever)
{
    public const int MaxTextLength = 200_000;
    private const int MaxOrganizedSegments = 12;

    public Task<AvatarFeedResultDto> FeedTextAsync(
        string? text,
        DateTimeOffset? occurredAt,
        string source,
        CancellationToken cancellationToken) =>
        ingestion.EnqueueTextAsync(
            text,
            occurredAt,
            AvatarIngestion.SourceText,
            null,
            null,
            cancellationToken);

    public Task<AvatarFeedResultDto> FeedFileAsync(
        string fileName,
        string? text,
        DateTimeOffset? occurredAt,
        CancellationToken cancellationToken) =>
        ingestion.EnqueueFileAsync(
            fileName,
            text ?? string.Empty,
            occurredAt,
            cancellationToken);

    public async Task<IReadOnlyList<AvatarFeedEntryDto>> ListFeedAsync(
        int limit,
        CancellationToken cancellationToken) =>
        (await repository.ListFeedEntriesAsync(limit, cancellationToken))
        .Select(AvatarFeedEntryDto.FromDomain)
        .ToList();

    public async Task ClearFeedAsync(CancellationToken cancellationToken) =>
        await repository.ClearFeedAsync(cancellationToken);

    /// <summary>列出记忆；带 query 时走「向量 + 词面」混合检索。</summary>
    public async Task<IReadOnlyList<AvatarMemoryDto>> ListMemoriesAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200);
        var normalized = (query ?? string.Empty).Trim();

        IReadOnlyList<AvatarMemory> memories = normalized.Length == 0
            ? await repository.ListMemoriesForRetrievalAsync(limit, false, cancellationToken)
            : (await retriever.RetrieveAsync(normalized, limit, cancellationToken))
                .Select(hit => hit.Memory)
                .ToList();

        return memories.Select(AvatarMemoryDto.FromDomain).ToList();
    }

    public Task<bool> DeleteMemoryAsync(Guid memoryId, CancellationToken cancellationToken) =>
        repository.DeleteMemoryAsync(memoryId, cancellationToken);

    public async Task<AvatarReindexResultDto> ReindexAsync(
        bool all,
        int limit,
        CancellationToken cancellationToken) =>
        await indexer.ReindexAsync(all, Math.Clamp(limit, 1, 500), cancellationToken);

    /// <summary>
    /// 把旧版已经存在、但还没整理成记忆的碎片补排一次整理任务。
    /// 原文不复制，直接复用现有 fragment。
    /// </summary>
    public async Task<AvatarFeedResultDto> OrganizeLegacyFragmentsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var fragments = await repository.ListUndigestedFragmentsAsync(
            Math.Clamp(limit, 1, MaxOrganizedSegments), cancellationToken);
        if (fragments.Count == 0)
            throw new AvatarValidationException("没有待整理的旧素材。");

        var now = DateTimeOffset.UtcNow;
        var task = AvatarIngestion.Create(
            AvatarIngestion.SourceText,
            "legacy-fragments",
            null,
            fragments.Select(fragment => fragment.Id).ToList(),
            null,
            now);

        await repository.AddFeedEntryAsync(
            new AvatarFeedEntry(
                0,
                AvatarFeedEntry.UserRole,
                $"【整理旧素材】共 {fragments.Count} 段，帮我补成记忆。",
                null,
                now),
            cancellationToken);
        await repository.AddIngestionAsync(task, cancellationToken);

        return new AvatarFeedResultDto(
            $"我把 {fragments.Count} 段旧素材排入了整理队列，完成后会告诉我记了什么。",
            [],
            [],
            false,
            null,
            0,
            await repository.CountPendingMemoryVectorsAsync(cancellationToken),
            task.Id,
            task.Status);
    }

    public Task<IReadOnlyList<AvatarMemoryEvidenceDto>> ListEvidenceAsync(
        Guid memoryId,
        CancellationToken cancellationToken) =>
        ingestion.ListEvidenceAsync(memoryId, cancellationToken);

    public Task<IReadOnlyList<AvatarMemoryVersionDto>> ListVersionsAsync(
        Guid memoryId,
        CancellationToken cancellationToken) =>
        ingestion.ListVersionsAsync(memoryId, cancellationToken);
}
