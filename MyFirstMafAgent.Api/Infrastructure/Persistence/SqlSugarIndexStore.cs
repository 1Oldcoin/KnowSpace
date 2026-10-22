using MyFirstMafAgent.Api.Domain.Knowledge;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>索引生命周期、索引向量与 Outbox 的 SqlSugar 实现。</summary>
public sealed class SqlSugarIndexStore(SqlSugarScope database) : IKnowledgeIndexStore, IIndexVectorStore, IOutboxStore
{
    public async Task<IReadOnlyList<KnowledgeIndex>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeIndexSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToIndex).ToList();
    }

    public async Task<KnowledgeIndex?> GetAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var row = await GetEntityAsync(indexId, cancellationToken);
        return row is null ? null : ToIndex(row);
    }

    public async Task<KnowledgeIndex> CreateAsync(KnowledgeIndex index, CancellationToken cancellationToken)
    {
        var entity = new KnowledgeIndexSchemaEntity
        {
            Id = index.Id,
            KnowledgeBaseId = index.KnowledgeBaseId,
            EmbeddingSpecId = index.EmbeddingSpecId,
            EmbeddingFingerprint = index.EmbeddingFingerprint,
            Dimensions = index.Dimensions,
            Name = index.Name,
            Status = index.Status,
            ActivateWhenReady = index.ActivateWhenReady,
            BuildProgress = index.BuildProgress,
            BuildStage = index.BuildStage,
            LastAppliedEventId = index.LastAppliedEventId,
            CreatedAt = index.CreatedAt,
            UpdatedAt = index.CreatedAt
        };
        await database.Insertable(entity).ExecuteCommandAsync(cancellationToken);
        return ToIndex(entity);
    }

    public async Task UpdateStatusAsync(Guid indexId, string status, string? failureReason, CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(indexId, cancellationToken)
            ?? throw new KeyNotFoundException($"Index {indexId} does not exist.");
        var now = DateTimeOffset.UtcNow;
        entity.Status = status;
        entity.FailureReason = failureReason;
        entity.UpdatedAt = now;
        if (status == KnowledgeIndexStatuses.Active) entity.ActivatedAt ??= now;
        if (status == KnowledgeIndexStatuses.Retired) entity.RetiredAt = now;
        await database.Updateable(entity).ExecuteCommandAsync(cancellationToken);
    }

    public async Task ResetForRebuildAsync(Guid indexId, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeIndexSchemaEntity>()
            .SetColumns(item => new KnowledgeIndexSchemaEntity
            {
                Status = KnowledgeIndexStatuses.Building,
                BuildProgress = 0,
                BuildStage = "等待全量重建",
                FailureReason = null,
                LastFullRebuildAt = null,
                QualityScore = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == indexId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetActivateWhenReadyAsync(Guid indexId, bool activateWhenReady, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeIndexSchemaEntity>()
            .SetColumns(item => new KnowledgeIndexSchemaEntity
            {
                ActivateWhenReady = activateWhenReady,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == indexId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateProgressAsync(Guid indexId, int progress, string stage, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(progress, 0, 99);
        await database.Updateable<KnowledgeIndexSchemaEntity>()
            .SetColumns(item => new KnowledgeIndexSchemaEntity
            {
                BuildProgress = clamped,
                BuildStage = stage,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == indexId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetLastAppliedEventAsync(Guid indexId, long eventId, CancellationToken cancellationToken)
    {
        // 检查点只允许前进，重复/乱序事件不会把进度回退。
        await database.Updateable<KnowledgeIndexSchemaEntity>()
            .SetColumns(item => new KnowledgeIndexSchemaEntity
            {
                LastAppliedEventId = eventId,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == indexId && item.LastAppliedEventId < eventId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task MarkBuiltAsync(Guid indexId, long checkpointEventId, CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(indexId, cancellationToken)
            ?? throw new KeyNotFoundException($"Index {indexId} does not exist.");
        entity.LastAppliedEventId = Math.Max(entity.LastAppliedEventId, checkpointEventId);
        entity.LastFullRebuildAt = DateTimeOffset.UtcNow;
        entity.BuildProgress = 100;
        entity.BuildStage = "构建完成，等待一致性检查";
        entity.FailureReason = null;
        entity.Status = entity.Status == KnowledgeIndexStatuses.Building
            ? KnowledgeIndexStatuses.Ready
            : entity.Status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await database.Updateable(entity).ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetConsistencyAsync(Guid indexId, bool consistent, CancellationToken cancellationToken)
    {
        var stage = consistent ? "一致性检查通过" : "一致性检查失败";
        await database.Updateable<KnowledgeIndexSchemaEntity>()
            .SetColumns(item => new KnowledgeIndexSchemaEntity
            {
                ConsistencyCheckedAt = SqlFunc.GetDate(),
                BuildStage = stage,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == indexId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetQualityAsync(Guid indexId, double score, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeIndexSchemaEntity>()
            .SetColumns(item => new KnowledgeIndexSchemaEntity
            {
                QualityScore = score,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == indexId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<ActiveIndex?> GetActiveAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<ActiveIndexSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId && item.IndexId != null)
            .FirstAsync(cancellationToken);
        if (row?.IndexId is not Guid indexId || row.EmbeddingSpecId is not Guid specId) return null;

        return new ActiveIndex(row.KnowledgeBaseId, indexId, specId,
            row.EmbeddingFingerprint ?? string.Empty, row.Dimensions ?? 0,
            KnowledgeIndexStatuses.Active, 0);
    }

    public async Task<ActiveIndex> ActivateAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var index = await GetEntityAsync(indexId, cancellationToken)
            ?? throw new KeyNotFoundException($"Index {indexId} does not exist.");
        if (index.Status is KnowledgeIndexStatuses.Building)
            throw new InvalidOperationException("A building index cannot be activated before its consistency check passes.");

        await database.Ado.BeginTranAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var binding = await database.Queryable<ActiveIndexSchemaEntity>()
                .Where(item => item.KnowledgeBaseId == index.KnowledgeBaseId)
                .FirstAsync(cancellationToken);

            if (binding?.IndexId is Guid previousId && previousId != indexId)
            {
                var previous = await GetEntityAsync(previousId, cancellationToken);
                if (previous is not null && previous.Status == KnowledgeIndexStatuses.Active)
                {
                    previous.Status = KnowledgeIndexStatuses.Retired;
                    previous.RetiredAt = now;
                    previous.UpdatedAt = now;
                    await database.Updateable(previous).ExecuteCommandAsync(cancellationToken);
                }
            }

            index.Status = KnowledgeIndexStatuses.Active;
            index.ActivatedAt = now;
            index.RetiredAt = null;
            index.FailureReason = null;
            index.UpdatedAt = now;
            await database.Updateable(index).ExecuteCommandAsync(cancellationToken);

            if (binding is null)
            {
                await database.Insertable(new ActiveIndexSchemaEntity
                {
                    KnowledgeBaseId = index.KnowledgeBaseId,
                    IndexId = index.Id,
                    EmbeddingSpecId = index.EmbeddingSpecId,
                    EmbeddingFingerprint = index.EmbeddingFingerprint,
                    Dimensions = index.Dimensions,
                    UpdatedAt = now
                }).ExecuteCommandAsync(cancellationToken);
            }
            else
            {
                binding.IndexId = index.Id;
                binding.EmbeddingSpecId = index.EmbeddingSpecId;
                binding.EmbeddingFingerprint = index.EmbeddingFingerprint;
                binding.Dimensions = index.Dimensions;
                binding.UpdatedAt = now;
                await database.Updateable(binding).ExecuteCommandAsync(cancellationToken);
            }

            await database.Ado.CommitTranAsync();
            return new ActiveIndex(index.KnowledgeBaseId, index.Id, index.EmbeddingSpecId,
                index.EmbeddingFingerprint, index.Dimensions, KnowledgeIndexStatuses.Active,
                index.LastAppliedEventId);
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<ActiveIndex?> RollbackAsync(Guid knowledgeBaseId, DateTimeOffset retiredAfter, CancellationToken cancellationToken)
    {
        var candidate = await database.Queryable<KnowledgeIndexSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId
                && item.Status == KnowledgeIndexStatuses.Retired
                && item.RetiredAt != null
                && item.RetiredAt >= retiredAfter)
            .OrderBy(item => item.RetiredAt, OrderByType.Desc)
            .FirstAsync(cancellationToken);
        return candidate is null ? null : await ActivateAsync(candidate.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeIndex>> ListPendingAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeIndexSchemaEntity>()
            .Where(item => item.Status == KnowledgeIndexStatuses.Building
                || (item.Status == KnowledgeIndexStatuses.Ready && item.ActivateWhenReady))
            .OrderBy(item => item.CreatedAt, OrderByType.Asc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToIndex).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeIndex>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeIndexSchemaEntity>()
            .Where(item => item.Status == KnowledgeIndexStatuses.Active)
            .ToListAsync(cancellationToken);
        return rows.Select(ToIndex).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeIndex>> ListRetiredBeforeAsync(Guid knowledgeBaseId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeIndexSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId
                && item.Status == KnowledgeIndexStatuses.Retired
                && item.RetiredAt != null
                && item.RetiredAt < cutoff)
            .ToListAsync(cancellationToken);
        return rows.Select(ToIndex).ToList();
    }

    public async Task DeleteAsync(Guid indexId, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            await database.Deleteable<IndexVectorSchemaEntity>()
                .Where(item => item.IndexId == indexId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<ActiveIndexSchemaEntity>()
                .SetColumns(item => new ActiveIndexSchemaEntity
                {
                    IndexId = null,
                    EmbeddingSpecId = null,
                    EmbeddingFingerprint = null,
                    Dimensions = null,
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(item => item.IndexId == indexId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<KnowledgeIndexSchemaEntity>()
                .Where(item => item.Id == indexId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task DeleteIndexVectorsAsync(Guid indexId, CancellationToken cancellationToken)
    {
        await database.Deleteable<IndexVectorSchemaEntity>()
            .Where(item => item.IndexId == indexId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task DeleteDocumentVectorsAsync(Guid indexId, Guid documentId, CancellationToken cancellationToken)
    {
        await database.Deleteable<IndexVectorSchemaEntity>()
            .Where(item => item.IndexId == indexId && item.DocumentId == documentId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<long>> GetIndexedChunkIdsAsync(Guid indexId, Guid documentId, CancellationToken cancellationToken)
    {
        var ids = await database.Queryable<IndexVectorSchemaEntity>()
            .Where(item => item.IndexId == indexId && item.DocumentId == documentId)
            .Select(item => item.ChunkId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public async Task UpsertAsync(Guid indexId, IReadOnlyList<IndexVector> vectors, CancellationToken cancellationToken)
    {
        if (vectors.Count == 0) return;

        await database.Ado.BeginTranAsync();
        try
        {
            var chunkIds = vectors.Select(item => item.ChunkId).ToList();
            for (var offset = 0; offset < chunkIds.Count; offset += 500)
            {
                var batch = chunkIds.Skip(offset).Take(500).ToList();
                await database.Deleteable<IndexVectorSchemaEntity>()
                    .Where(item => item.IndexId == indexId && batch.Contains(item.ChunkId))
                    .ExecuteCommandAsync(cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var vector in vectors)
            {
                await database.Insertable(new IndexVectorSchemaEntity
                {
                    IndexId = indexId,
                    DocumentId = vector.DocumentId,
                    DocumentVersion = vector.DocumentVersion,
                    ChunkId = vector.ChunkId,
                    ChunkIndex = vector.ChunkIndex,
                    Embedding = PgVectorSql.ToVectorLiteral(vector.Embedding),
                    CreatedAt = now
                }).ExecuteCommandAsync(cancellationToken);
            }

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid indexId,
        float[] embedding,
        Guid knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = PgVectorSql.BuildIndexSearchSql(PgVectorSql.ToVectorLiteral(embedding));
        var rows = await database.Ado.SqlQueryAsync<RetrievedChunkRow>(sql, new
        {
            indexId,
            knowledgeBaseId,
            limit = Math.Clamp(limit, 1, 50)
        }, cancellationToken);
        return rows.Select(row => new RetrievedChunk(row.DocumentId, row.FileName, row.Content, row.Similarity)).ToList();
    }

    public async Task<IndexVectorStats> GetStatsAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var vectorCount = await database.Queryable<IndexVectorSchemaEntity>()
            .Where(item => item.IndexId == indexId)
            .CountAsync(cancellationToken);
        var documentCount = await database.Queryable<IndexVectorSchemaEntity>()
            .Where(item => item.IndexId == indexId)
            .Select(item => item.DocumentId)
            .Distinct()
            .CountAsync(cancellationToken);
        return new IndexVectorStats(vectorCount, documentCount, vectorCount);
    }

    public async Task<long> GetChunkVectorCountAsync(Guid indexId, CancellationToken cancellationToken) =>
        await database.Queryable<IndexVectorSchemaEntity>()
            .Where(item => item.IndexId == indexId)
            .CountAsync(cancellationToken);

    public async Task<IReadOnlyList<int>> GetVectorDimensionsAsync(Guid indexId, CancellationToken cancellationToken)
    {
        var rows = await database.Ado.SqlQueryAsync<IntValueRow>(
            PgVectorSql.DimensionSampleSql, new { indexId }, cancellationToken);
        return rows.Select(row => row.Value).ToList();
    }

    public async Task<IReadOnlyList<OutboxEvent>> ReadAfterAsync(long checkpoint, int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<OutboxEventSchemaEntity>()
            .Where(item => item.Id > checkpoint)
            .OrderBy(item => item.Id, OrderByType.Asc)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(ToEvent).ToList();
    }

    public async Task<IReadOnlyList<OutboxEvent>> ReadKnowledgeBaseAfterAsync(Guid knowledgeBaseId, long checkpoint, int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<OutboxEventSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId && item.Id > checkpoint)
            .OrderBy(item => item.Id, OrderByType.Asc)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(ToEvent).ToList();
    }

    public async Task<long> GetMaxIdAsync(CancellationToken cancellationToken)
    {
        // 不能用 .Select(...).FirstAsync()：Outbox 为空时 SqlSugar 会对 long 做空值转换并抛 NullReferenceException。
        var ids = await database.Queryable<OutboxEventSchemaEntity>()
            .OrderBy(item => item.Id, OrderByType.Desc)
            .Select(item => item.Id)
            .Take(1)
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? 0 : ids[0];
    }

    public async Task<long> GetKnowledgeBaseMaxIdAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        // 知识库没有事件（空库、新建库或事件已清理）时水位线为 0，不能依赖 FirstAsync。
        var ids = await database.Queryable<OutboxEventSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .OrderBy(item => item.Id, OrderByType.Desc)
            .Select(item => item.Id)
            .Take(1)
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? 0 : ids[0];
    }

    public async Task DeleteConsumedBeforeAsync(long maxAppliedEventId, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await database.Deleteable<OutboxEventSchemaEntity>()
            .Where(item => item.Id <= maxAppliedEventId && item.OccurredAt < olderThan)
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task<KnowledgeIndexSchemaEntity?> GetEntityAsync(Guid indexId, CancellationToken cancellationToken) =>
        await database.Queryable<KnowledgeIndexSchemaEntity>()
            .Where(item => item.Id == indexId)
            .FirstAsync(cancellationToken);

    private static KnowledgeIndex ToIndex(KnowledgeIndexSchemaEntity entity) => new(
        entity.Id, entity.KnowledgeBaseId, entity.EmbeddingSpecId, entity.EmbeddingFingerprint,
        entity.Dimensions, entity.Name, entity.Status, entity.ActivateWhenReady, entity.BuildProgress,
        entity.BuildStage, entity.FailureReason, entity.LastAppliedEventId, entity.LastFullRebuildAt,
        entity.ConsistencyCheckedAt, entity.QualityScore, entity.CreatedAt, entity.UpdatedAt,
        entity.ActivatedAt, entity.RetiredAt);

    private static OutboxEvent ToEvent(OutboxEventSchemaEntity entity) => new(
        entity.Id, entity.EventType, entity.KnowledgeBaseId, entity.DocumentId, entity.DocumentVersion,
        entity.ContentHash, entity.IdempotencyKey, entity.Payload, entity.OccurredAt);
}

public sealed class IntValueRow
{
    public IntValueRow() { }

    public int Value { get; set; }
}
