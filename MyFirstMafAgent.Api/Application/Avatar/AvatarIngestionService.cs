using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 异步投喂入口与处理器。
/// 接收：原文先落 fragment，创建 avatar_ingestions 任务后立即返回 ingestionId。
/// 处理：由 <see cref="AvatarIngestionWorker"/> 在后台完成记忆提取、证据绑定、去重/冲突、
/// 版本记录、人格候选归纳与向量化，不阻塞问答。
/// </summary>
public sealed class AvatarIngestionService(
    IAvatarRepository repository,
    AvatarOrganizer organizer,
    AvatarInsightService insightService,
    AvatarMemoryIndexer indexer,
    ILogger<AvatarIngestionService> logger)
{
    private const int MaxSegments = 200;
    private const int SegmentChars = 3_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- 入队 ----

    public async Task<AvatarFeedResultDto> EnqueueTextAsync(
        string? text,
        DateTimeOffset? occurredAt,
        string sourceType,
        string? sourceRef,
        IReadOnlyList<Guid>? supersedesMemoryIds,
        CancellationToken cancellationToken)
    {
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
            throw new AvatarValidationException("写点什么再喂给我。");
        if (content.Length > 200_000)
            throw new AvatarValidationException("单次投喂不能超过 200,000 个字符。");

        var now = DateTimeOffset.UtcNow;
        var fragment = AvatarFragment.Create(
            new AvatarFragmentDraft(null, null, content, occurredAt, "chat", null), now, "chat");

        var ingestion = AvatarIngestion.Create(
            NormalizeSourceType(sourceType),
            sourceRef,
            null,
            [fragment.Id],
            supersedesMemoryIds,
            now);

        await repository.AddFragmentsAsync(
            [fragment], ComputeHash(content), 0, AvatarOrganizer.ExtractorVersion, ingestion.Id, cancellationToken);
        await repository.AddIngestionAsync(ingestion, cancellationToken);
        await repository.AddFeedEntryAsync(
            new AvatarFeedEntry(0, AvatarFeedEntry.UserRole, TruncateForFeed(content), null, now),
            cancellationToken);

        logger.LogInformation(
            "Avatar ingestion {IngestionId} queued: {Length} chars, source {Source}",
            ingestion.Id, content.Length, ingestion.SourceType);

        return new AvatarFeedResultDto(
            "我先记下了，正在整理成记忆。整理完成后会在上面的记录里告诉我记了什么。",
            [],
            [],
            false,
            null,
            0,
            await repository.CountPendingMemoryVectorsAsync(cancellationToken),
            ingestion.Id,
            ingestion.Status);
    }

    public async Task<AvatarFeedResultDto> EnqueueFileAsync(
        string fileName,
        string text,
        DateTimeOffset? occurredAt,
        CancellationToken cancellationToken)
    {
        var segments = AvatarTextSplitter.Split(text, SegmentChars, MaxSegments);
        if (segments.Count == 0)
            throw new AvatarValidationException("文件内容为空或无法识别为文本。");

        var sourceHash = ComputeHash(text);
        var idempotencyKey = $"{sourceHash}:file:{AvatarOrganizer.ExtractorVersion}";
        var existing = await repository.FindIngestionByKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return new AvatarFeedResultDto(
                existing.Status is AvatarIngestion.Completed or AvatarIngestion.Partial
                    ? $"《{fileName}》之前已经导入过，我没有重复整理。"
                    : $"《{fileName}》正在整理中，稍后就能用。",
                [],
                [],
                false,
                null,
                0,
                await repository.CountPendingMemoryVectorsAsync(cancellationToken),
                existing.Id,
                existing.Status);
        }

        var now = DateTimeOffset.UtcNow;
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "未命名文件" : fileName.Trim();
        var fragments = segments
            .Select(segment => AvatarFragment.Create(
                new AvatarFragmentDraft(null, null, segment, occurredAt, "upload", null), now, "upload"))
            .ToList();

        var ingestion = AvatarIngestion.Create(
            AvatarIngestion.SourceFile,
            safeName,
            idempotencyKey,
            fragments.Select(fragment => fragment.Id).ToList(),
            null,
            now);

        await repository.AddFeedEntryAsync(
            new AvatarFeedEntry(
                0,
                AvatarFeedEntry.UserRole,
                $"【导入文件】{safeName}（{segments.Count} 段），帮我整理成记忆。",
                null,
                now),
            cancellationToken);
        await repository.AddFragmentsAsync(
            fragments, sourceHash, 0, AvatarOrganizer.ExtractorVersion, ingestion.Id, cancellationToken);
        await repository.AddIngestionAsync(ingestion, cancellationToken);

        logger.LogInformation(
            "Avatar file ingestion {IngestionId} queued: {FileName}, {Segments} segments",
            ingestion.Id, safeName, segments.Count);

        return new AvatarFeedResultDto(
            $"我收到了《{safeName}》，正在后台整理成记忆。",
            [],
            [],
            false,
            null,
            0,
            await repository.CountPendingMemoryVectorsAsync(cancellationToken),
            ingestion.Id,
            ingestion.Status);
    }

    public async Task<AvatarIngestionDto?> GetIngestionAsync(Guid ingestionId, CancellationToken cancellationToken)
    {
        var ingestion = await repository.GetIngestionAsync(ingestionId, cancellationToken);
        return ingestion is null ? null : AvatarIngestionDto.FromDomain(ingestion);
    }

    public async Task<IReadOnlyList<AvatarIngestionDto>> ListIngestionsAsync(
        int limit,
        CancellationToken cancellationToken) =>
        (await repository.ListIngestionsAsync(limit, cancellationToken))
        .Select(AvatarIngestionDto.FromDomain)
        .ToList();

    public async Task<bool> RetryAsync(Guid ingestionId, CancellationToken cancellationToken)
    {
        var ingestion = await repository.GetIngestionAsync(ingestionId, cancellationToken);
        if (ingestion is null || !ingestion.IsTerminal) return false;

        ingestion.ResetForRetry(DateTimeOffset.UtcNow);
        await repository.UpdateIngestionAsync(ingestion, cancellationToken);
        return true;
    }

    // ---- 后台处理 ----

    public async Task ProcessAsync(AvatarIngestion ingestion, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        ingestion.MarkRunning(AvatarIngestion.StageExtracting, now);
        await repository.UpdateIngestionAsync(ingestion, cancellationToken);

        try
        {
            var fragments = await repository.GetFragmentsAsync(ingestion.FragmentIds, cancellationToken);
            if (fragments.Count == 0)
            {
                ingestion.Fail("投喂原文不存在，无法整理。", DateTimeOffset.UtcNow);
                await repository.UpdateIngestionAsync(ingestion, cancellationToken);
                return;
            }

            var existingInsights = await repository.ListInsightsAsync(cancellationToken);
            var existingMemories = await repository.ListMemoriesForRetrievalAsync(2_000, true, cancellationToken);

            var results = new List<AvatarOrganizeResult>(fragments.Count);
            foreach (var fragment in fragments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await organizer.OrganizeAsync(
                    fragment.Content,
                    fragment.Id,
                    fragment.OccurredAt,
                    fragment.Source,
                    existingInsights,
                    cancellationToken,
                    existingMemories));
            }

            ingestion.MarkRunning(AvatarIngestion.StageCommitting, DateTimeOffset.UtcNow);
            await repository.UpdateIngestionAsync(ingestion, cancellationToken);

            var commit = await CommitAsync(ingestion, results, cancellationToken);

            ingestion.MarkRunning(AvatarIngestion.StageIndexing, DateTimeOffset.UtcNow);
            await repository.UpdateIngestionAsync(ingestion, cancellationToken);

            var indexOutcome = await indexer.IndexAsync(commit.NewMemories, cancellationToken);

            await repository.MarkFragmentsDigestedAsync(
                fragments.Select(fragment => fragment.Id).ToList(), cancellationToken);

            var usedFallback = results.Any(result => result.UsedFallback);
            var stats = JsonSerializer.Serialize(new
            {
                segments = fragments.Count,
                newMemories = commit.NewMemories.Count,
                merged = commit.MergedCount,
                superseded = commit.SupersededCount,
                insights = commit.InsightCount,
                indexed = indexOutcome.Indexed,
                pending = indexOutcome.Pending,
                usedFallback,
                warning = indexOutcome.Warning
            }, JsonOptions);

            if (usedFallback)
            {
                ingestion.MarkPartial(
                    "部分内容暂时无法整理，已按原文保存，可稍后重试。",
                    stats,
                    commit.NewMemories.Count,
                    commit.InsightCount,
                    DateTimeOffset.UtcNow);
            }
            else
            {
                ingestion.Complete(
                    AvatarIngestion.StageCompleted,
                    stats,
                    commit.NewMemories.Count,
                    commit.InsightCount,
                    DateTimeOffset.UtcNow);
            }

            await repository.UpdateIngestionAsync(ingestion, cancellationToken);

            var reply = commit.NewMemories.Count == 0
                ? "这段内容和已有记忆基本一致，我把新的证据合并进去了。"
                : ComposeReply(commit.NewMemories);

            await repository.AddFeedEntryAsync(
                new AvatarFeedEntry(0, AvatarFeedEntry.AssistantRole, reply, stats, DateTimeOffset.UtcNow),
                cancellationToken);

            logger.LogInformation(
                "Avatar ingestion {IngestionId} completed: {New} new, {Merged} merged, {Superseded} superseded, {Indexed} indexed",
                ingestion.Id, commit.NewMemories.Count, commit.MergedCount, commit.SupersededCount, indexOutcome.Indexed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Avatar ingestion {IngestionId} failed", ingestion.Id);
            ingestion.Fail(exception.Message, DateTimeOffset.UtcNow);
            await repository.UpdateIngestionAsync(ingestion, cancellationToken);

            await repository.AddFeedEntryAsync(
                new AvatarFeedEntry(
                    0,
                    AvatarFeedEntry.AssistantRole,
                    "这次整理没有完成，原文已经保存。可以在投喂记录里重试。",
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }

    // ---- 提交：去重 / 冲突 / 版本 / 证据 ----

    private async Task<CommitResult> CommitAsync(
        AvatarIngestion ingestion,
        IReadOnlyList<AvatarOrganizeResult> results,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = (await repository.ListMemoriesForRetrievalAsync(2_000, true, cancellationToken)).ToList();
        var existingById = existing.ToDictionary(memory => memory.Id);

        var newMemories = new List<AvatarMemory>();
        var updatedMemories = new Dictionary<Guid, AvatarMemory>();
        var evidences = new List<AvatarMemoryEvidence>();
        var versions = new List<AvatarMemoryVersion>();
        var insightCandidates = new List<AvatarInsightCandidate>();
        var entityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedCount = 0;
        var supersededCount = 0;

        var correctionTargets = ingestion.SupersedesMemoryIds.ToHashSet();

        foreach (var result in results)
        {
            var resultEvidences = result.Evidences ?? [];
            foreach (var candidate in result.Memories)
            {
                var candidateEvidences = resultEvidences
                    .Where(item => item.MemoryId == candidate.Id)
                    .ToList();

                // 1. 纠正投喂：明确取代指定旧记忆。
                if (correctionTargets.Count > 0)
                {
                    var targetId = correctionTargets.First();
                    candidate.MarkLifecycle(AvatarMemoryLifecycle.Active, targetId, now);
                }

                // 2. 模型指出的明确冲突：旧记忆转历史/被取代。
                if (candidate.SupersedesId is Guid supersedeId
                    && supersedeId != candidate.Id
                    && existingById.TryGetValue(supersedeId, out var superseded))
                {
                    superseded.MarkLifecycle(AvatarMemoryLifecycle.Superseded, null, now);
                    updatedMemories[superseded.Id] = superseded;
                    versions.Add(AvatarMemoryVersion.Create(
                        superseded.Id,
                        await NextVersionAsync(superseded.Id, cancellationToken),
                        Snapshot(superseded),
                        AvatarMemoryVersion.ChangeSuperseded,
                        null,
                        $"被新记忆《{candidate.Title}》取代",
                        now));
                    supersededCount++;
                }

                // 3. 去重：同类型、几乎相同的表述直接合并证据。
                var duplicate = existing.FirstOrDefault(item =>
                    item.Id != candidate.Id
                    && item.MemoryType == candidate.MemoryType
                    && AvatarTextSimilarity.Jaccard(item.ToRetrievalText(), candidate.ToRetrievalText()) >= 0.82);
                if (duplicate is not null)
                {
                    duplicate.Confirm(now);
                    updatedMemories[duplicate.Id] = duplicate;
                    if (candidateEvidences.Count == 0)
                    {
                        candidateEvidences.Add(AvatarMemoryEvidence.Create(
                            duplicate.Id, candidate.FragmentId, candidate.Summary,
                            0, candidate.Summary.Length, null, null, null, candidate.OccurredAt, now));
                    }
                    else
                    {
                        candidateEvidences = candidateEvidences
                            .Select(item => AvatarMemoryEvidence.Create(
                                duplicate.Id,
                                item.FragmentId,
                                item.EvidenceText,
                                item.StartOffset,
                                item.EndOffset,
                                item.PageNo,
                                item.MessageId,
                                item.SourceHash,
                                item.OccurredAt,
                                item.RecordedAt))
                            .ToList();
                    }

                    evidences.AddRange(candidateEvidences);
                    versions.Add(AvatarMemoryVersion.Create(
                        duplicate.Id,
                        await NextVersionAsync(duplicate.Id, cancellationToken),
                        Snapshot(duplicate),
                        AvatarMemoryVersion.ChangeMerged,
                        null,
                        "合并了重复表述的新证据",
                        now));
                    mergedCount++;
                    continue;
                }

                // 4. 置信度与稳定性决定生命周期：不把一次表达直接写成永久人格。
                if (candidate.LifecycleStatus == AvatarMemoryLifecycle.Active)
                {
                    var status = candidate.Confidence switch
                    {
                        >= 70 => AvatarMemoryLifecycle.Active,
                        >= 50 => AvatarMemoryLifecycle.Candidate,
                        _ => AvatarMemoryLifecycle.PendingConfirmation
                    };
                    if (candidate.Stability == AvatarMemoryStability.Stable
                        && (candidate.MemoryType is AvatarMemoryTypes.Value
                            or AvatarMemoryTypes.Relationship
                            or AvatarMemoryTypes.Procedural)
                        && candidate.Confidence < 70)
                    {
                        status = AvatarMemoryLifecycle.PendingConfirmation;
                    }

                    candidate.MarkLifecycle(status, null, now);
                }

                newMemories.Add(candidate);
                if (candidateEvidences.Count == 0)
                {
                    candidateEvidences.Add(AvatarMemoryEvidence.Create(
                        candidate.Id, candidate.FragmentId, candidate.Summary,
                        0, candidate.Summary.Length, null, null, null, candidate.OccurredAt, now));
                }
                evidences.AddRange(candidateEvidences);

                versions.Add(AvatarMemoryVersion.Create(
                    candidate.Id,
                    1,
                    Snapshot(candidate),
                    AvatarMemoryVersion.ChangeCreated,
                    candidate.SupersedesId,
                    "投喂整理",
                    now));

                existing.Add(candidate);
                existingById[candidate.Id] = candidate;

                foreach (var person in candidate.People)
                    entityNames.Add(person);
            }

            foreach (var candidate in result.InsightCandidates ?? [])
                insightCandidates.Add(candidate);
            foreach (var name in result.Entities ?? [])
                entityNames.Add(name);
        }

        // 纠正旧记忆：未被明确取代但属于本次纠正目标的，也标为已纠正。
        foreach (var targetId in correctionTargets)
        {
            if (updatedMemories.ContainsKey(targetId)) continue;
            if (!existingById.TryGetValue(targetId, out var target)) continue;

            target.MarkLifecycle(AvatarMemoryLifecycle.Corrected, null, now);
            updatedMemories[target.Id] = target;
            versions.Add(AvatarMemoryVersion.Create(
                target.Id,
                await NextVersionAsync(target.Id, cancellationToken),
                Snapshot(target),
                AvatarMemoryVersion.ChangeCorrected,
                null,
                "用户纠正",
                now));
        }

        if (newMemories.Count > 0)
            await repository.AddMemoriesAsync(newMemories, cancellationToken);
        if (updatedMemories.Count > 0)
            await repository.UpdateMemoriesAsync(updatedMemories.Values.ToList(), cancellationToken);
        if (evidences.Count > 0)
            await repository.AddMemoryEvidenceAsync(evidences, cancellationToken);
        if (versions.Count > 0)
            await repository.AddMemoryVersionsAsync(versions, cancellationToken);

        var insightCount = await ProposeInsightsAsync(
            insightCandidates, newMemories, entityNames, cancellationToken);

        return new CommitResult(newMemories, insightCount, mergedCount, supersededCount);
    }

    private async Task<int> ProposeInsightsAsync(
        IReadOnlyList<AvatarInsightCandidate> candidates,
        IReadOnlyList<AvatarMemory> memories,
        IReadOnlyCollection<string> entityNames,
        CancellationToken cancellationToken)
    {
        if (entityNames.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var entities = entityNames
                .Select(name => AvatarEntity.Create("person", name, null, null, now))
                .ToList();
            await repository.UpsertEntitiesAsync(entities, cancellationToken);

            var known = await repository.ListEntitiesAsync(cancellationToken);
            var byName = known.ToDictionary(
                entity => entity.Name, entity => entity.Id, StringComparer.OrdinalIgnoreCase);
            var links = new List<AvatarMemoryEntityLink>();
            foreach (var memory in memories)
            {
                foreach (var person in memory.People)
                {
                    if (byName.TryGetValue(person, out var entityId))
                        links.Add(new AvatarMemoryEntityLink(memory.Id, entityId, "mentioned"));
                }
            }

            if (links.Count > 0)
                await repository.AddMemoryEntityLinksAsync(links, cancellationToken);
        }

        if (candidates.Count == 0) return 0;

        var memoryIds = memories.Select(memory => memory.Id).ToList();
        var count = 0;
        foreach (var candidate in candidates)
        {
            await insightService.ProposeAsync(
                candidate.Insight,
                candidate.EvidenceMemoryIds.Count > 0 ? candidate.EvidenceMemoryIds : memoryIds,
                candidate.Scope,
                cancellationToken);
            count++;
        }

        return count;
    }

    private async Task<int> NextVersionAsync(Guid memoryId, CancellationToken cancellationToken) =>
        await repository.CountMemoryVersionsAsync(memoryId, cancellationToken) + 1;

    private static string Snapshot(AvatarMemory memory) =>
        JsonSerializer.Serialize(new
        {
            memory.Id,
            memory.Kind,
            memory.MemoryType,
            memory.Stability,
            memory.Title,
            memory.Summary,
            memory.Content,
            memory.OccurredAt,
            memory.People,
            memory.Tags,
            memory.Emotion,
            memory.Importance,
            memory.Confidence,
            memory.LifecycleStatus,
            memory.ValidFrom,
            memory.ValidTo,
            memory.SupersedesId,
            memory.PrivacyLevel
        }, JsonOptions);

    // ---- 查询 ----

    public Task<IReadOnlyList<AvatarMemoryEvidenceDto>> ListEvidenceAsync(
        Guid memoryId,
        CancellationToken cancellationToken) =>
        ListEvidenceCoreAsync(memoryId, cancellationToken);

    private async Task<IReadOnlyList<AvatarMemoryEvidenceDto>> ListEvidenceCoreAsync(
        Guid memoryId,
        CancellationToken cancellationToken) =>
        (await repository.ListMemoryEvidenceAsync(memoryId, cancellationToken))
        .Select(AvatarMemoryEvidenceDto.FromDomain)
        .ToList();

    public async Task<IReadOnlyList<AvatarMemoryVersionDto>> ListVersionsAsync(
        Guid memoryId,
        CancellationToken cancellationToken) =>
        (await repository.ListMemoryVersionsAsync(memoryId, cancellationToken))
        .Select(AvatarMemoryVersionDto.FromDomain)
        .ToList();

    private static string ComposeReply(IReadOnlyList<AvatarMemory> memories)
    {
        var titles = memories.Take(4).Select(memory => $"「{memory.Title}」").ToList();
        var suffix = memories.Count > 4 ? $" 等 {memories.Count} 条" : string.Empty;
        return $"我整理好了：{string.Join('、', titles)}{suffix}。每条都保留了原文证据，以后可以直接问我。";
    }

    private static string TruncateForFeed(string content) =>
        content.Length <= 2_000 ? content : content[..2_000] + "…";

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string NormalizeSourceType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            AvatarIngestion.SourceCorrection => AvatarIngestion.SourceCorrection,
            AvatarIngestion.SourceSupplement => AvatarIngestion.SourceSupplement,
            _ => AvatarIngestion.SourceText
        };

    private sealed record CommitResult(
        IReadOnlyList<AvatarMemory> NewMemories,
        int InsightCount,
        int MergedCount,
        int SupersededCount);
}
