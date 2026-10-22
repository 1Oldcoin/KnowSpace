using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Application.Knowledge;

/// <summary>
/// 索引生命周期管理器：
/// 创建 → 全量重建 → 增量重放 → 一致性检查 → 质量评测 → 原子切换 → 回滚窗口 → 清理。
/// 所有切换都只更新活动索引绑定，线上查询不会看到半成品索引。
/// </summary>
public sealed class IndexLifecycleManager(
    IKnowledgeIndexStore indexStore,
    IIndexVectorStore vectorStore,
    IOutboxStore outbox,
    IDocumentIndexSource documentSource,
    IEmbeddingSpecStore specStore,
    IEmbeddingGeneratorProvider generatorProvider,
    IModelBindingStore modelBindings,
    EmbeddingSpecService specService,
    IOptions<IndexingOptions> indexingOptions,
    ILogger<IndexLifecycleManager> logger)
{
    private const int ReplayBatchSize = 50;
    private const int MaxReplayBatches = 100;

    private DateTimeOffset lastMissingIndexScan = DateTimeOffset.MinValue;

    private IndexingOptions Options => indexingOptions.Value;

    /// <summary>
    /// 启动时调用：回填历史版本、注册配置中的向量规格，并为缺少索引的知识库创建默认阴影索引。
    /// <paramref name="preferredSpecId"/> 来自设置页保存的默认向量模型；为空时回退到配置。
    /// </summary>
    public async Task EnsureDefaultIndexesAsync(Guid? preferredSpecId, CancellationToken cancellationToken)
    {
        var migrated = await documentSource.BackfillLegacyVersionsAsync(cancellationToken);
        if (migrated > 0)
            logger.LogInformation("Backfilled {Count} legacy document(s) to content version 1", migrated);

        var defaultSpec = await specService.ResolveDefaultAsync(preferredSpecId, cancellationToken);
        var knowledgeBaseIds = await documentSource.ListKnowledgeBaseIdsAsync(cancellationToken);
        foreach (var knowledgeBaseId in knowledgeBaseIds)
        {
            var indexes = await indexStore.ListAsync(knowledgeBaseId, cancellationToken);
            if (indexes.Any(index => index.Status is KnowledgeIndexStatuses.Active
                    or KnowledgeIndexStatuses.Ready or KnowledgeIndexStatuses.Building))
            {
                continue;
            }

            // 知识库自身绑定的规格优先于全局默认值。
            var spec = defaultSpec;
            if (await modelBindings.GetEmbeddingSpecIdAsync(knowledgeBaseId, cancellationToken) is Guid boundSpecId)
                spec = await specStore.GetAsync(boundSpecId, cancellationToken) ?? defaultSpec;

            await CreateIndexAsync(knowledgeBaseId, spec.Id,
                $"default-{spec.Name}", activateWhenReady: true, cancellationToken);
            logger.LogInformation("Created default shadow index for knowledge base {KnowledgeBaseId} using spec {SpecName}",
                knowledgeBaseId, spec.Name);
        }
    }

    public async Task<KnowledgeIndex> CreateIndexAsync(
        Guid knowledgeBaseId,
        Guid? embeddingSpecId,
        string? name,
        bool activateWhenReady,
        CancellationToken cancellationToken)
    {
        var spec = embeddingSpecId is Guid id
            ? await specStore.GetAsync(id, cancellationToken) ?? throw new EmbeddingSpecNotFoundException(id)
            : await specService.GetDefaultAsync(cancellationToken);

        var existing = (await indexStore.ListAsync(knowledgeBaseId, cancellationToken))
            .FirstOrDefault(index => index.IsShadow && index.EmbeddingSpecId == spec.Id);
        if (existing is not null)
        {
            if (existing.ActivateWhenReady != activateWhenReady)
                await indexStore.SetActivateWhenReadyAsync(existing.Id, activateWhenReady, cancellationToken);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var index = new KnowledgeIndex(
            Guid.NewGuid(), knowledgeBaseId, spec.Id, spec.Fingerprint, spec.Dimensions,
            string.IsNullOrWhiteSpace(name) ? $"shadow-{spec.Name}-{now:yyyyMMddHHmmss}" : name.Trim(),
            KnowledgeIndexStatuses.Building, activateWhenReady, 0, "等待构建", null, 0,
            null, null, null, now, now, null, null);
        var created = await indexStore.CreateAsync(index, cancellationToken);
        await modelBindings.SetEmbeddingSpecIdAsync(knowledgeBaseId, spec.Id, cancellationToken);
        logger.LogInformation("Created index {IndexId} for knowledge base {KnowledgeBaseId} using spec {SpecName} ({Fingerprint})",
            created.Id, knowledgeBaseId, spec.Name, spec.Fingerprint);
        return created;
    }

    /// <summary>
    /// 排队执行全量重建。活动索引绝不在原地重建：会自动创建同规格的阴影索引，
    /// 构建完成后再切换，确保线上查询不中断。
    /// </summary>
    public async Task<KnowledgeIndex> ScheduleRebuildAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        if (index.Status == KnowledgeIndexStatuses.Active)
        {
            var now = DateTimeOffset.UtcNow;
            var shadow = await CreateIndexAsync(index.KnowledgeBaseId, index.EmbeddingSpecId,
                $"rebuild-{index.Name}-{now:yyyyMMddHHmmss}", activateWhenReady: true, cancellationToken);
            await indexStore.ResetForRebuildAsync(shadow.Id, cancellationToken);
            return await GetRequiredAsync(shadow.Id, cancellationToken);
        }

        await indexStore.ResetForRebuildAsync(indexId, cancellationToken);
        return await GetRequiredAsync(indexId, cancellationToken);
    }

    /// <summary>影子索引全量重建。重建期间活动索引继续服务。</summary>
    public async Task<KnowledgeIndex> RebuildAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        if (index.Status == KnowledgeIndexStatuses.Active)
            throw new ConflictException("活动索引不能原地重建，请先创建阴影索引。");
        var spec = await GetSpecAsync(index, cancellationToken);
        var generator = generatorProvider.GetOrCreate(spec);

        await indexStore.UpdateStatusAsync(indexId, KnowledgeIndexStatuses.Building, null, cancellationToken);
        await indexStore.UpdateProgressAsync(indexId, 0, "清理旧向量", cancellationToken);
        await vectorStore.DeleteIndexVectorsAsync(indexId, cancellationToken);

        // 先记录水位线：构建期间产生的新事件在构建结束后通过增量重放补齐。
        // 空知识库没有事件时水位线为 0（由存储层保证，不能依赖空结果集的 FirstAsync）。
        var watermark = await outbox.GetKnowledgeBaseMaxIdAsync(index.KnowledgeBaseId, cancellationToken);
        var documents = await documentSource.ListPublishedDocumentsAsync(index.KnowledgeBaseId, cancellationToken) ?? [];
        var processed = 0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunks = await documentSource.GetPublishedChunksAsync(document.DocumentId, cancellationToken) ?? [];
            if (chunks.Count > 0)
            {
                var vectors = new List<IndexVector>(chunks.Count);
                foreach (var chunk in chunks)
                {
                    var embedding = await generator.CreateEmbeddingAsync(chunk.Content, cancellationToken);
                    if (embedding is null || embedding.Length != spec.Dimensions)
                        throw new InvalidDataException(
                            $"Embedding dimension {embedding?.Length ?? 0} does not match spec dimension {spec.Dimensions}.");
                    vectors.Add(new IndexVector(chunk.ChunkId, chunk.DocumentId, chunk.DocumentVersion,
                        chunk.ChunkIndex, embedding));
                }
                await vectorStore.UpsertAsync(indexId, vectors, cancellationToken);
            }

            processed++;
            var progress = documents.Count == 0 ? 90 : 5 + (int)(85d * processed / documents.Count);
            await indexStore.UpdateProgressAsync(indexId, progress, $"已重建 {processed}/{documents.Count} 份资料", cancellationToken);
        }

        await indexStore.MarkBuiltAsync(indexId, watermark, cancellationToken);
        logger.LogInformation("Full rebuild finished for index {IndexId}: {DocumentCount} document(s)", indexId, processed);

        await ReplayAsync(indexId, cancellationToken);
        var consistency = await CheckConsistencyAsync(indexId, cancellationToken);
        if (!consistency.IsConsistent && Options.RequireConsistencyBeforeActivation)
        {
            await indexStore.UpdateStatusAsync(indexId, KnowledgeIndexStatuses.Failed,
                string.Join("; ", consistency.Issues), cancellationToken);
            return await GetRequiredAsync(indexId, cancellationToken);
        }

        await EvaluateQualityAsync(indexId, cancellationToken);
        return await FinalizeReadyAsync(indexId, cancellationToken);
    }

    /// <summary>增量重放：按检查点顺序消费 Outbox，遇到版本过期的事件直接跳过。</summary>
    public async Task<int> ReplayAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        var spec = await GetSpecAsync(index, cancellationToken);
        var generator = generatorProvider.GetOrCreate(spec);
        var checkpoint = index.LastAppliedEventId;
        var applied = 0;

        for (var batch = 0; batch < MaxReplayBatches; batch++)
        {
            var events = await outbox.ReadKnowledgeBaseAfterAsync(index.KnowledgeBaseId, checkpoint, ReplayBatchSize, cancellationToken);
            if (events.Count == 0) break;

            foreach (var evt in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ApplyEventAsync(index, spec, generator, evt, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Failed to apply outbox event {EventId} (type {EventType}) to index {IndexId}",
                        evt.Id, evt.EventType, indexId);
                    throw;
                }

                checkpoint = evt.Id;
                await indexStore.SetLastAppliedEventAsync(indexId, evt.Id, cancellationToken);
                applied++;
            }

            if (events.Count < ReplayBatchSize) break;
        }

        if (applied > 0)
            logger.LogInformation("Replayed {Count} outbox event(s) into index {IndexId}", applied, indexId);
        return applied;
    }

    public async Task<IndexConsistencyReport> CheckConsistencyAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        var spec = await GetSpecAsync(index, cancellationToken);
        var documents = await documentSource.ListPublishedDocumentsAsync(index.KnowledgeBaseId, cancellationToken);

        long expected = 0;
        foreach (var document in documents)
            expected += await documentSource.CountPublishedChunksAsync(document.DocumentId, cancellationToken);

        var indexed = await vectorStore.GetChunkVectorCountAsync(indexId, cancellationToken);
        var dimensions = await vectorStore.GetVectorDimensionsAsync(indexId, cancellationToken);
        var mismatched = dimensions.Count(dimension => dimension != spec.Dimensions);

        var issues = new List<string>();
        if (expected > indexed) issues.Add($"缺少 {expected - indexed} 个切片向量。");
        if (indexed > expected) issues.Add($"存在 {indexed - expected} 个孤立向量。");
        if (mismatched > 0) issues.Add($"存在 {mismatched} 个维度不匹配的向量。");

        var report = new IndexConsistencyReport(
            indexId, issues.Count == 0, expected, indexed,
            Math.Max(0, expected - indexed), Math.Max(0, indexed - expected),
            0, mismatched, issues);
        await indexStore.SetConsistencyAsync(indexId, report.IsConsistent, cancellationToken);
        return report;
    }

    /// <summary>用切片自检索估算索引覆盖率和近邻命中率，作为切换前的质量门槛。</summary>
    public async Task<IndexQualityReport> EvaluateQualityAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        var spec = await GetSpecAsync(index, cancellationToken);
        var generator = generatorProvider.GetOrCreate(spec);

        var documents = await documentSource.ListPublishedDocumentsAsync(index.KnowledgeBaseId, cancellationToken);
        long expected = 0;
        foreach (var document in documents)
            expected += await documentSource.CountPublishedChunksAsync(document.DocumentId, cancellationToken);
        var indexed = await vectorStore.GetChunkVectorCountAsync(indexId, cancellationToken);

        var probes = new List<IndexSourceChunk>();
        foreach (var document in documents)
        {
            if (probes.Count >= Options.QualityProbeCount) break;
            var chunks = await documentSource.GetPublishedChunksAsync(document.DocumentId, cancellationToken);
            probes.AddRange(chunks.Take(Options.QualityProbeCount - probes.Count));
        }

        var hits = 0;
        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await generator.CreateEmbeddingAsync(probe.Content, cancellationToken);
            var results = await vectorStore.SearchAsync(indexId, embedding, index.KnowledgeBaseId, 3, cancellationToken);
            if (results.Any(result => string.Equals(result.Content, probe.Content, StringComparison.Ordinal)))
                hits++;
        }

        var coverage = expected == 0 ? 1d : Math.Min(1d, (double)indexed / expected);
        var hitRate = probes.Count == 0 ? 1d : (double)hits / probes.Count;
        var score = Math.Round((coverage * 0.5 + hitRate * 0.5) * 100, 2);
        await indexStore.SetQualityAsync(indexId, score, cancellationToken);

        logger.LogInformation("Quality evaluated for index {IndexId}: score {Score}, coverage {Coverage}, self-recall {HitRate}",
            indexId, score, coverage, hitRate);
        return new IndexQualityReport(indexId, score, probes.Count, hits, indexed, expected);
    }

    public async Task<KnowledgeIndex> ActivateAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        if (index.Status == KnowledgeIndexStatuses.Building)
            throw new ConflictException("索引仍在构建，不能切换。");

        if (Options.RequireConsistencyBeforeActivation)
        {
            var consistency = await CheckConsistencyAsync(indexId, cancellationToken);
            if (!consistency.IsConsistent)
                throw new ConflictException($"索引一致性检查未通过：{string.Join("; ", consistency.Issues)}");
        }

        var active = await indexStore.ActivateAsync(indexId, cancellationToken);
        await modelBindings.SetEmbeddingSpecIdAsync(index.KnowledgeBaseId, index.EmbeddingSpecId, cancellationToken);
        logger.LogInformation("Activated index {IndexId} for knowledge base {KnowledgeBaseId} (spec {Fingerprint})",
            indexId, index.KnowledgeBaseId, index.EmbeddingFingerprint);
        _ = active;
        return await GetRequiredAsync(indexId, cancellationToken);
    }

    public async Task<ActiveIndex?> RollbackAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Options.RollbackWindowMinutes);
        var active = await indexStore.RollbackAsync(knowledgeBaseId, cutoff, cancellationToken);
        if (active is null) return null;

        await modelBindings.SetEmbeddingSpecIdAsync(knowledgeBaseId, active.EmbeddingSpecId, cancellationToken);
        logger.LogInformation("Rolled back knowledge base {KnowledgeBaseId} to index {IndexId}", knowledgeBaseId, active.IndexId);
        return active;
    }

    /// <summary>删除回滚窗口之外的旧索引，并清理已被所有索引消费的 Outbox 事件。</summary>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Options.RollbackWindowMinutes);
        var knowledgeBaseIds = await documentSource.ListKnowledgeBaseIdsAsync(cancellationToken);
        var deleted = 0;

        foreach (var knowledgeBaseId in knowledgeBaseIds)
        {
            var retired = await indexStore.ListRetiredBeforeAsync(knowledgeBaseId, cutoff, cancellationToken);
            foreach (var index in retired)
            {
                await indexStore.DeleteAsync(index.Id, cancellationToken);
                deleted++;
                logger.LogInformation("Cleaned up retired index {IndexId} for knowledge base {KnowledgeBaseId}",
                    index.Id, knowledgeBaseId);
            }
        }

        var activeIndexes = await indexStore.ListActiveAsync(cancellationToken);
        var pendingIndexes = await indexStore.ListPendingAsync(cancellationToken);
        var indexes = activeIndexes.Concat(pendingIndexes).ToList();
        if (indexes.Count > 0)
        {
            // 只有所有索引（含正在构建的阴影索引）都消费过的事件才可清理。
            var minCheckpoint = indexes.Min(index => index.LastAppliedEventId);
            var retentionCutoff = DateTimeOffset.UtcNow.AddHours(-Options.OutboxRetentionHours);
            await outbox.DeleteConsumedBeforeAsync(minCheckpoint, retentionCutoff, cancellationToken);
        }

        return deleted;
    }

    /// <summary>后台 Worker 单轮处理：补齐缺失索引、推进阴影索引、追赶活动索引。</summary>
    public async Task ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await EnsureMissingIndexesAsync(cancellationToken);

        var pending = await indexStore.ListPendingAsync(cancellationToken);
        foreach (var index in pending)
        {
            try
            {
                if (index.LastFullRebuildAt is null)
                {
                    await RebuildAsync(index.Id, cancellationToken);
                }
                else
                {
                    var applied = await ReplayAsync(index.Id, cancellationToken);
                    // 没有新事件时不必重复做一致性检查与质量评测（后者会实际调用向量模型）。
                    if (applied == 0 && index.Status == KnowledgeIndexStatuses.Ready) continue;

                    var consistency = await CheckConsistencyAsync(index.Id, cancellationToken);
                    if (!consistency.IsConsistent && Options.RequireConsistencyBeforeActivation)
                    {
                        await indexStore.UpdateStatusAsync(index.Id, KnowledgeIndexStatuses.Failed,
                            string.Join("; ", consistency.Issues), cancellationToken);
                        continue;
                    }
                    await EvaluateQualityAsync(index.Id, cancellationToken);
                    await FinalizeReadyAsync(index.Id, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Index worker failed to process index {IndexId} at stage {Stage}",
                    index.Id, index.BuildStage);
                await indexStore.UpdateStatusAsync(index.Id, KnowledgeIndexStatuses.Failed,
                    $"{index.BuildStage}: {exception.Message}", cancellationToken);
            }
        }

        var active = await indexStore.ListActiveAsync(cancellationToken);
        foreach (var index in active)
        {
            try
            {
                await ReplayAsync(index.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Index worker failed to replay changes into active index {IndexId}", index.Id);
            }
        }
    }

    /// <summary>
    /// 自动为「尚未拥有任何索引」的知识库创建默认索引（新建知识库、启动后新增资料等场景）。
    /// 规格优先取该知识库绑定，其次继承当前最新活动索引的规格，最后回退到配置默认规格。
    /// </summary>
    private async Task EnsureMissingIndexesAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - lastMissingIndexScan < TimeSpan.FromSeconds(15)) return;

        var knowledgeBaseIds = await documentSource.ListKnowledgeBaseIdsAsync(cancellationToken);
        if (knowledgeBaseIds.Count == 0) return;

        var activeIndexes = await indexStore.ListActiveAsync(cancellationToken);
        var inheritedSpecId = activeIndexes
            .OrderByDescending(index => index.ActivatedAt)
            .Select(index => (Guid?)index.EmbeddingSpecId)
            .FirstOrDefault();

        foreach (var knowledgeBaseId in knowledgeBaseIds)
        {
            var indexes = await indexStore.ListAsync(knowledgeBaseId, cancellationToken);
            if (indexes.Any(index => index.Status is KnowledgeIndexStatuses.Active
                    or KnowledgeIndexStatuses.Ready or KnowledgeIndexStatuses.Building))
            {
                continue;
            }

            var spec = await specService.GetDefaultAsync(cancellationToken);
            if (await modelBindings.GetEmbeddingSpecIdAsync(knowledgeBaseId, cancellationToken) is Guid boundSpecId
                && await specStore.GetAsync(boundSpecId, cancellationToken) is { } boundSpec)
            {
                spec = boundSpec;
            }
            else if (inheritedSpecId is Guid inheritedId
                && await specStore.GetAsync(inheritedId, cancellationToken) is { } inheritedSpec)
            {
                spec = inheritedSpec;
            }

            await CreateIndexAsync(knowledgeBaseId, spec.Id,
                $"default-{spec.Name}", activateWhenReady: true, cancellationToken);
            logger.LogInformation("Created missing index for knowledge base {KnowledgeBaseId} using spec {SpecName}",
                knowledgeBaseId, spec.Name);
        }

        lastMissingIndexScan = DateTimeOffset.UtcNow;
    }

    private async Task<KnowledgeIndex> FinalizeReadyAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetRequiredAsync(indexId, cancellationToken);
        if (index.ActivateWhenReady)
            return await ActivateAsync(indexId, cancellationToken);

        if (index.Status == KnowledgeIndexStatuses.Building)
            await indexStore.UpdateStatusAsync(indexId, KnowledgeIndexStatuses.Ready, null, cancellationToken);
        return await GetRequiredAsync(indexId, cancellationToken);
    }

    private async Task ApplyEventAsync(
        KnowledgeIndex index,
        EmbeddingSpec spec,
        IEmbeddingGenerator generator,
        OutboxEvent evt,
        CancellationToken cancellationToken)
    {
        if (evt.DocumentId is not Guid documentId) return;

        if (evt.EventType == OutboxEventTypes.DocumentDeleted)
        {
            await vectorStore.DeleteDocumentVectorsAsync(index.Id, documentId, cancellationToken);
            return;
        }

        if (evt.EventType != OutboxEventTypes.DocumentPublished) return;

        var document = await documentSource.GetPublishedDocumentAsync(documentId, cancellationToken);
        if (document is null)
        {
            await vectorStore.DeleteDocumentVectorsAsync(index.Id, documentId, cancellationToken);
            return;
        }

        // 版本校验：只应用当前已发布版本；过期事件直接跳过，检查点仍会前进。
        if (evt.DocumentVersion is int eventVersion && document.ContentVersion > eventVersion)
            return;
        if (document.ContentVersion != evt.DocumentVersion)
            return;

        var chunks = await documentSource.GetPublishedChunksAsync(documentId, cancellationToken);
        var currentChunkIds = chunks.Select(chunk => chunk.ChunkId).ToHashSet();
        var indexedChunkIds = await vectorStore.GetIndexedChunkIdsAsync(index.Id, documentId, cancellationToken);
        if (indexedChunkIds.SetEquals(currentChunkIds)) return;

        await vectorStore.DeleteDocumentVectorsAsync(index.Id, documentId, cancellationToken);
        if (chunks.Count == 0) return;

        var vectors = new List<IndexVector>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var embedding = await generator.CreateEmbeddingAsync(chunk.Content, cancellationToken);
            if (embedding.Length != spec.Dimensions)
                throw new InvalidDataException(
                    $"Embedding dimension {embedding.Length} does not match spec dimension {spec.Dimensions}.");
            vectors.Add(new IndexVector(chunk.ChunkId, chunk.DocumentId, chunk.DocumentVersion,
                chunk.ChunkIndex, embedding));
        }
        await vectorStore.UpsertAsync(index.Id, vectors, cancellationToken);
    }

    private async Task<KnowledgeIndex> GetRequiredAsync(Guid indexId, CancellationToken cancellationToken) =>
        await indexStore.GetAsync(indexId, cancellationToken)
        ?? throw new IndexNotFoundException(indexId);

    private async Task<EmbeddingSpec> GetSpecAsync(KnowledgeIndex index, CancellationToken cancellationToken) =>
        await specStore.GetAsync(index.EmbeddingSpecId, cancellationToken)
        ?? throw new EmbeddingSpecNotFoundException(index.EmbeddingSpecId);
}

public sealed class IndexNotFoundException(Guid indexId)
    : NotFoundException($"索引 {indexId} 不存在。");
