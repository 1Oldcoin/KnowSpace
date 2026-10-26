using System.Text.Json;
using MyFirstMafAgent.Api.Domain.Avatar;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>分身模块的 SqlSugar 仓储实现。</summary>
public sealed class SqlSugarAvatarRepository(SqlSugarScope database) : IAvatarRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="ensureSchema">
    /// false = 结构已由 <see cref="Hosting.SchemaStamp"/> 证明是最新的，跳过全部建表检查。
    /// 这里 20 张表的 catalog 往返实测约 1.4 秒，是启动耗时第二大户。
    /// </param>
    public async Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (ensureSchema)
            await EnsureSchemaAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<AvatarProfileSchemaEntity>();
        database.CodeFirst.InitTables<AvatarFragmentSchemaEntity>();
        database.CodeFirst.InitTables<AvatarInsightSchemaEntity>();
        database.CodeFirst.InitTables<AvatarChatSessionSchemaEntity>();
        database.CodeFirst.InitTables<AvatarChatMessageSchemaEntity>();
        database.CodeFirst.InitTables<AvatarExperienceSchemaEntity>();
        database.CodeFirst.InitTables<AvatarBehaviorSchemaEntity>();
        database.CodeFirst.InitTables<AvatarRelationshipSchemaEntity>();
        database.CodeFirst.InitTables<AvatarSimulationSchemaEntity>();
        database.CodeFirst.InitTables<AvatarMemorySchemaEntity>();
        database.CodeFirst.InitTables<AvatarFeedEntrySchemaEntity>();

        // v2：证据链、版本、投喂任务、反馈、实体与评测。
        database.CodeFirst.InitTables<AvatarIngestionSchemaEntity>();
        database.CodeFirst.InitTables<AvatarMemoryEvidenceSchemaEntity>();
        database.CodeFirst.InitTables<AvatarMemoryVersionSchemaEntity>();
        database.CodeFirst.InitTables<AvatarInsightVersionSchemaEntity>();
        database.CodeFirst.InitTables<AvatarFeedbackSchemaEntity>();
        database.CodeFirst.InitTables<AvatarEntitySchemaEntity>();
        database.CodeFirst.InitTables<AvatarMemoryEntitySchemaEntity>();
        database.CodeFirst.InitTables<AvatarEvalCaseSchemaEntity>();
        database.CodeFirst.InitTables<AvatarEvalRunSchemaEntity>();
        database.CodeFirst.InitTables<AvatarEvalResultSchemaEntity>();

        EnsureMemoryColumns();
        EnsureFragmentColumns();

        // 记忆向量表依赖 pgvector 扩展；扩展已在知识库初始化时创建，这里做幂等兜底。
        await PgVectorSql.EnsureExtensionAsync(database, cancellationToken);
        database.CodeFirst.InitTables<AvatarMemoryVectorSchemaEntity>();
    }

    /// <summary>
    /// CodeFirst 对既有表的加列行为依赖版本，这里对 v2 新列做幂等兜底，
    /// 保证老库升级后不会因缺列直接失败。
    /// </summary>
    private void EnsureMemoryColumns()
    {
        if (!database.DbMaintenance.IsAnyTable("avatar_memories", false)) return;

        EnsureColumn("avatar_memories", "memory_type", "text", false, "'fact'");
        EnsureColumn("avatar_memories", "stability", "text", false, "'situational'");
        EnsureColumn("avatar_memories", "confidence", "integer", false, "60");
        EnsureColumn("avatar_memories", "lifecycle_status", "text", false, "'active'");
        EnsureColumn("avatar_memories", "valid_from", "timestamptz", true, null);
        EnsureColumn("avatar_memories", "valid_to", "timestamptz", true, null);
        EnsureColumn("avatar_memories", "supersedes_id", "uuid", true, null);
        EnsureColumn("avatar_memories", "structured_data", "text", false, "'{}'");
        EnsureColumn("avatar_memories", "privacy_level", "text", false, "'normal'");
        EnsureColumn("avatar_memories", "last_confirmed_at", "timestamptz", true, null);
        EnsureColumn("avatar_memories", "last_used_at", "timestamptz", true, null);
        EnsureColumn("avatar_memories", "evidence_count", "integer", false, "0");
    }

    private void EnsureFragmentColumns()
    {
        if (!database.DbMaintenance.IsAnyTable("avatar_fragments", false)) return;

        EnsureColumn("avatar_fragments", "source_hash", "text", true, null);
        EnsureColumn("avatar_fragments", "segment_index", "integer", false, "0");
        EnsureColumn("avatar_fragments", "extractor_version", "text", false, "''");
        EnsureColumn("avatar_fragments", "ingestion_id", "uuid", true, null);
    }

    private void EnsureColumn(string table, string column, string dataType, bool nullable, string? defaultValue)
    {
        if (database.DbMaintenance.IsAnyColumn(table, column, false)) return;
        database.DbMaintenance.AddColumn(table, new DbColumnInfo
        {
            DbColumnName = column,
            DataType = dataType,
            IsNullable = nullable,
            DefaultValue = defaultValue
        });
    }

    // ---- 碎片素材 ----

    public async Task<IReadOnlyList<AvatarFragment>> ListFragmentsAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarFragmentSchemaEntity>()
            .OrderBy(entity => entity.OccurredAt, OrderByType.Desc)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 1_000))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public Task<int> CountFragmentsAsync(CancellationToken cancellationToken) =>
        database.Queryable<AvatarFragmentSchemaEntity>().CountAsync(cancellationToken);

    public Task<int> CountUndigestedFragmentsAsync(CancellationToken cancellationToken) =>
        database.Queryable<AvatarFragmentSchemaEntity>()
            .Where(entity => !entity.IsDigested)
            .CountAsync(cancellationToken);

    public async Task AddFragmentsAsync(IReadOnlyList<AvatarFragment> fragments, CancellationToken cancellationToken)
    {
        if (fragments.Count == 0) return;
        await database.Insertable(fragments.Select(ToEntity).ToList()).ExecuteCommandAsync(cancellationToken);
    }

    public async Task AddFragmentsAsync(
        IReadOnlyList<AvatarFragment> fragments,
        string? sourceHash,
        int startSegmentIndex,
        string extractorVersion,
        Guid? ingestionId,
        CancellationToken cancellationToken)
    {
        if (fragments.Count == 0) return;

        var rows = new List<AvatarFragmentSchemaEntity>(fragments.Count);
        for (var index = 0; index < fragments.Count; index++)
        {
            var entity = ToEntity(fragments[index]);
            entity.SourceHash = sourceHash;
            entity.SegmentIndex = startSegmentIndex + index;
            entity.ExtractorVersion = extractorVersion ?? string.Empty;
            entity.IngestionId = ingestionId;
            rows.Add(entity);
        }

        await database.Insertable(rows).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvatarFragment>> GetFragmentsAsync(
        IReadOnlyList<Guid> fragmentIds,
        CancellationToken cancellationToken)
    {
        if (fragmentIds.Count == 0) return [];

        var rows = new List<AvatarFragmentSchemaEntity>();
        for (var offset = 0; offset < fragmentIds.Count; offset += 500)
        {
            var batch = fragmentIds.Skip(offset).Take(500).ToList();
            rows.AddRange(await database.Queryable<AvatarFragmentSchemaEntity>()
                .Where(entity => batch.Contains(entity.Id))
                .ToListAsync(cancellationToken));
        }

        var byId = rows.ToDictionary(row => row.Id);
        return fragmentIds
            .Where(id => byId.ContainsKey(id))
            .Select(id => ToDomain(byId[id]))
            .ToList();
    }

    public async Task<bool> FragmentExistsAsync(
        string sourceHash,
        int segmentIndex,
        string extractorVersion,
        CancellationToken cancellationToken) =>
        await database.Queryable<AvatarFragmentSchemaEntity>()
            .Where(entity => entity.SourceHash == sourceHash
                && entity.SegmentIndex == segmentIndex
                && entity.ExtractorVersion == extractorVersion)
            .AnyAsync(cancellationToken);

    public async Task<bool> DeleteFragmentAsync(Guid fragmentId, CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarFragmentSchemaEntity>()
            .Where(entity => entity.Id == fragmentId)
            .ExecuteCommandAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<AvatarFragment>> ListUndigestedFragmentsAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarFragmentSchemaEntity>()
            .Where(entity => !entity.IsDigested)
            .OrderBy(entity => entity.OccurredAt, OrderByType.Asc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task MarkFragmentsDigestedAsync(IReadOnlyList<Guid> fragmentIds, CancellationToken cancellationToken)
    {
        if (fragmentIds.Count == 0) return;
        await database.Updateable<AvatarFragmentSchemaEntity>()
            .SetColumns(entity => new AvatarFragmentSchemaEntity { IsDigested = true })
            .Where(entity => fragmentIds.Contains(entity.Id))
            .ExecuteCommandAsync(cancellationToken);
    }

    // ---- 整理后的记忆 ----

    public async Task AddMemoriesAsync(IReadOnlyList<AvatarMemory> memories, CancellationToken cancellationToken)
    {
        if (memories.Count == 0) return;
        await database.Insertable(memories.Select(ToEntity).ToList()).ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateMemoryAsync(AvatarMemory memory, CancellationToken cancellationToken) =>
        await UpdateMemoriesAsync([memory], cancellationToken);

    public async Task UpdateMemoriesAsync(IReadOnlyList<AvatarMemory> memories, CancellationToken cancellationToken)
    {
        foreach (var memory in memories)
        {
            var entity = ToEntity(memory);

            // 必须用实体驱动的 UpdateColumns：SetColumns 会把「求值为 null 的可空列」
            // （valid_from/valid_to/supersedes_id/last_confirmed_at/last_used_at）推断成
            // AnsiString 参数，PostgreSQL 报 42804，整条记忆更新失败。
            // UpdateColumns 按实体属性类型绑定，空值仍然是 timestamptz / uuid。
            await database.Updateable(entity)
                .UpdateColumns(item => new
                {
                    item.FragmentId,
                    item.Kind,
                    item.Title,
                    item.Summary,
                    item.Content,
                    item.OccurredAt,
                    item.People,
                    item.Tags,
                    item.Emotion,
                    item.Importance,
                    item.Source,
                    item.VectorState,
                    item.MemoryType,
                    item.Stability,
                    item.Confidence,
                    item.LifecycleStatus,
                    item.ValidFrom,
                    item.ValidTo,
                    item.SupersedesId,
                    item.StructuredData,
                    item.PrivacyLevel,
                    item.LastConfirmedAt,
                    item.LastUsedAt,
                    item.EvidenceCount,
                    item.UpdatedAt
                })
                .ExecuteCommandAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AvatarMemory>> ListMemoriesForRetrievalAsync(
        int limit,
        bool includeHistorical,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<AvatarMemorySchemaEntity>();
        if (!includeHistorical)
        {
            query = query.Where(entity =>
                entity.LifecycleStatus == AvatarMemoryLifecycle.Active
                || entity.LifecycleStatus == AvatarMemoryLifecycle.Candidate
                || entity.LifecycleStatus == AvatarMemoryLifecycle.PendingConfirmation);
        }

        var rows = await query
            .OrderBy(entity => entity.OccurredAt, OrderByType.Desc)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 2_000))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task TouchMemoriesUsedAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        for (var offset = 0; offset < memoryIds.Count; offset += 500)
        {
            var batch = memoryIds.Skip(offset).Take(500).ToList();
            await database.Updateable<AvatarMemorySchemaEntity>()
                .SetColumns(entity => new AvatarMemorySchemaEntity { LastUsedAt = now, UpdatedAt = now })
                .Where(entity => batch.Contains(entity.Id))
                .ExecuteCommandAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AvatarMemory>> ListMemoriesAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarMemorySchemaEntity>()
            .OrderBy(entity => entity.OccurredAt, OrderByType.Desc)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 2_000))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<AvatarMemory>> GetMemoriesAsync(
        IReadOnlyList<Guid> memoryIds,
        CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0) return [];
        var rows = await database.Queryable<AvatarMemorySchemaEntity>()
            .Where(entity => memoryIds.Contains(entity.Id))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<AvatarMemory?> GetMemoryAsync(Guid memoryId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarMemorySchemaEntity>()
            .Where(entity => entity.Id == memoryId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<bool> DeleteMemoryAsync(Guid memoryId, CancellationToken cancellationToken)
    {
        await database.Deleteable<AvatarMemoryVectorSchemaEntity>()
            .Where(entity => entity.MemoryId == memoryId)
            .ExecuteCommandAsync(cancellationToken);
        return await database.Deleteable<AvatarMemorySchemaEntity>()
            .Where(entity => entity.Id == memoryId)
            .ExecuteCommandAsync(cancellationToken) > 0;
    }

    public Task<int> CountMemoriesAsync(CancellationToken cancellationToken) =>
        database.Queryable<AvatarMemorySchemaEntity>().CountAsync(cancellationToken);

    public Task<int> CountPendingMemoryVectorsAsync(CancellationToken cancellationToken) =>
        database.Queryable<AvatarMemorySchemaEntity>()
            .Where(entity => entity.VectorState != "ready")
            .CountAsync(cancellationToken);

    public async Task<IReadOnlyList<AvatarMemory>> ListMemoriesPendingVectorAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarMemorySchemaEntity>()
            .Where(entity => entity.VectorState != "ready")
            .OrderBy(entity => entity.CreatedAt, OrderByType.Asc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task MarkMemoryVectorStateAsync(Guid memoryId, string state, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await database.Updateable<AvatarMemorySchemaEntity>()
            .SetColumns(entity => new AvatarMemorySchemaEntity
            {
                VectorState = state,
                UpdatedAt = now
            })
            .Where(entity => entity.Id == memoryId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpsertMemoryVectorsAsync(
        IReadOnlyList<AvatarMemoryVector> vectors,
        CancellationToken cancellationToken)
    {
        if (vectors.Count == 0) return;

        await database.Ado.BeginTranAsync();
        try
        {
            var ids = vectors.Select(vector => vector.MemoryId).ToList();
            for (var offset = 0; offset < ids.Count; offset += 500)
            {
                var batch = ids.Skip(offset).Take(500).ToList();
                await database.Deleteable<AvatarMemoryVectorSchemaEntity>()
                    .Where(entity => batch.Contains(entity.MemoryId))
                    .ExecuteCommandAsync(cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var vector in vectors)
            {
                await database.Insertable(new AvatarMemoryVectorSchemaEntity
                {
                    MemoryId = vector.MemoryId,
                    EmbeddingSpecId = vector.EmbeddingSpecId,
                    EmbeddingFingerprint = vector.SpecFingerprint,
                    Dimensions = vector.Dimensions,
                    Embedding = PgVectorSql.ToVectorLiteral(vector.Embedding),
                    UpdatedAt = now
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

    public async Task<IReadOnlyList<AvatarMemoryVectorMatch>> SearchMemoryVectorsAsync(
        float[] embedding,
        string embeddingFingerprint,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = PgVectorSql.BuildAvatarMemorySearchSql(PgVectorSql.ToVectorLiteral(embedding));
        var rows = await database.Ado.SqlQueryAsync<AvatarMemoryVectorRow>(sql, new
        {
            fingerprint = embeddingFingerprint,
            limit = Math.Clamp(limit, 1, 100)
        }, cancellationToken);
        return rows
            .Select(row => new AvatarMemoryVectorMatch(row.MemoryId, row.Similarity))
            .ToList();
    }

    // ---- 投喂对话 ----

    public async Task AddFeedEntryAsync(AvatarFeedEntry entry, CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarFeedEntrySchemaEntity
        {
            Role = entry.Role,
            Content = entry.Content,
            Payload = entry.Payload,
            CreatedAt = entry.CreatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task<IReadOnlyList<AvatarFeedEntry>> ListFeedEntriesAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarFeedEntrySchemaEntity>()
            .OrderBy(entity => entity.Id, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(entity => entity.Id)
            .Select(entity => new AvatarFeedEntry(entity.Id, entity.Role, entity.Content, entity.Payload, entity.CreatedAt))
            .ToList();
    }

    public async Task ClearFeedAsync(CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarFeedEntrySchemaEntity>()
            .Where(entity => entity.Id > 0)
            .ExecuteCommandAsync(cancellationToken);

    // ---- 人格洞察 ----

    public async Task<IReadOnlyList<AvatarInsight>> ListInsightsAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarInsightSchemaEntity>()
            .OrderBy(entity => entity.Dimension, OrderByType.Asc)
            .OrderBy(entity => entity.Confidence, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveInsightsAsync(IReadOnlyList<AvatarInsight> insights, CancellationToken cancellationToken)
    {
        if (insights.Count == 0) return;

        foreach (var insight in insights)
        {
            var entity = ToEntity(insight);
            var existing = await database.Queryable<AvatarInsightSchemaEntity>()
                .Where(item => item.Dimension == entity.Dimension && item.Title == entity.Title)
                .Take(1)
                .ToListAsync(cancellationToken);

            if (existing.Count == 0)
            {
                await database.Insertable(entity).ExecuteCommandAsync(cancellationToken);
                continue;
            }

            await database.Updateable<AvatarInsightSchemaEntity>()
                .SetColumns(item => new AvatarInsightSchemaEntity
                {
                    Content = entity.Content,
                    Confidence = entity.Confidence,
                    EvidenceCount = entity.EvidenceCount,
                    Evidence = entity.Evidence,
                    UpdatedAt = entity.UpdatedAt
                })
                .Where(item => item.Id == existing[0].Id)
                .ExecuteCommandAsync(cancellationToken);
        }
    }

    public async Task ClearInsightsAsync(CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarInsightSchemaEntity>()
            .Where(entity => entity.Id != Guid.Empty)
            .ExecuteCommandAsync(cancellationToken);

    // ---- 分身对话 ----

    public async Task<IReadOnlyList<AvatarChatSession>> ListChatSessionsAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarChatSessionSchemaEntity>()
            .OrderBy(entity => entity.UpdatedAt, OrderByType.Desc)
            .Take(100)
            .ToListAsync(cancellationToken);
        return rows.Select(entity => AvatarChatSession.Restore(entity.Id, entity.Title, entity.CreatedAt, entity.UpdatedAt)).ToList();
    }

    public async Task<AvatarChatSession?> GetChatSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarChatSessionSchemaEntity>()
            .Where(entity => entity.Id == sessionId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0
            ? null
            : AvatarChatSession.Restore(rows[0].Id, rows[0].Title, rows[0].CreatedAt, rows[0].UpdatedAt);
    }

    public async Task AddChatSessionAsync(AvatarChatSession session, CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarChatSessionSchemaEntity
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task TouchChatSessionAsync(Guid sessionId, string? title, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(title))
        {
            await database.Updateable<AvatarChatSessionSchemaEntity>()
                .SetColumns(entity => new AvatarChatSessionSchemaEntity { UpdatedAt = now })
                .Where(entity => entity.Id == sessionId)
                .ExecuteCommandAsync(cancellationToken);
            return;
        }

        var normalized = title.Trim();
        if (normalized.Length > AvatarChatSession.MaxTitleLength)
            normalized = normalized[..AvatarChatSession.MaxTitleLength];

        await database.Updateable<AvatarChatSessionSchemaEntity>()
            .SetColumns(entity => new AvatarChatSessionSchemaEntity { Title = normalized, UpdatedAt = now })
            .Where(entity => entity.Id == sessionId && entity.Title == "新的对话")
            .ExecuteCommandAsync(cancellationToken);

        await database.Updateable<AvatarChatSessionSchemaEntity>()
            .SetColumns(entity => new AvatarChatSessionSchemaEntity { UpdatedAt = now })
            .Where(entity => entity.Id == sessionId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task AddChatMessageAsync(AvatarChatMessage message, CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarChatMessageSchemaEntity
        {
            SessionId = message.SessionId,
            Role = message.Role,
            Content = message.Content,
            Citations = Serialize(message.Citations),
            CreatedAt = message.CreatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task<long> AddChatMessageReturningIdAsync(
        AvatarChatMessage message,
        CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarChatMessageSchemaEntity
        {
            SessionId = message.SessionId,
            Role = message.Role,
            Content = message.Content,
            Citations = Serialize(message.Citations),
            CreatedAt = message.CreatedAt
        }).ExecuteReturnBigIdentityAsync();

    public async Task<AvatarChatMessage?> GetChatMessageAsync(long messageId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarChatMessageSchemaEntity>()
            .Where(entity => entity.Id == messageId)
            .Take(1)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return null;

        var row = rows[0];
        return new AvatarChatMessage(
            row.Id,
            row.SessionId,
            row.Role,
            row.Content,
            Deserialize<AvatarChatCitation>(row.Citations),
            row.CreatedAt);
    }

    public async Task<IReadOnlyList<AvatarChatMessage>> ListChatMessagesAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarChatMessageSchemaEntity>()
            .Where(entity => entity.SessionId == sessionId)
            .OrderBy(entity => entity.Id, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(entity => entity.Id)
            .Select(entity => new AvatarChatMessage(
                entity.Id,
                entity.SessionId,
                entity.Role,
                entity.Content,
                Deserialize<AvatarChatCitation>(entity.Citations),
                entity.CreatedAt))
            .ToList();
    }

    public async Task<bool> DeleteChatSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await database.Deleteable<AvatarChatMessageSchemaEntity>()
            .Where(entity => entity.SessionId == sessionId)
            .ExecuteCommandAsync(cancellationToken);
        return await database.Deleteable<AvatarChatSessionSchemaEntity>()
            .Where(entity => entity.Id == sessionId)
            .ExecuteCommandAsync(cancellationToken) > 0;
    }

    public async Task<AvatarProfile?> GetProfileAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarProfileSchemaEntity>()
            .Where(entity => entity.Id == AvatarProfile.SingletonId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task SaveProfileAsync(AvatarProfile profile, CancellationToken cancellationToken)
    {
        var entity = ToEntity(profile);
        var existing = await database.Queryable<AvatarProfileSchemaEntity>()
            .Where(item => item.Id == profile.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        if (existing.Count == 0)
        {
            await database.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            return;
        }

        await database.Updateable<AvatarProfileSchemaEntity>()
            .SetColumns(item => new AvatarProfileSchemaEntity
            {
                DisplayName = entity.DisplayName,
                Headline = entity.Headline,
                Personality = entity.Personality,
                Values = entity.Values,
                Strengths = entity.Strengths,
                Weaknesses = entity.Weaknesses,
                Skills = entity.Skills,
                Resources = entity.Resources,
                Constraints = entity.Constraints,
                LongTermGoals = entity.LongTermGoals,
                RiskPreference = entity.RiskPreference,
                UpdatedAt = entity.UpdatedAt
            })
            .Where(item => item.Id == profile.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvatarExperience>> ListExperiencesAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarExperienceSchemaEntity>()
            .OrderBy(entity => entity.OccurredAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task AddExperienceAsync(AvatarExperience experience, CancellationToken cancellationToken) =>
        await database.Insertable(ToEntity(experience)).ExecuteCommandAsync(cancellationToken);

    public async Task<bool> DeleteExperienceAsync(Guid experienceId, CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarExperienceSchemaEntity>()
            .Where(entity => entity.Id == experienceId)
            .ExecuteCommandAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<AvatarBehavior>> ListBehaviorsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarBehaviorSchemaEntity>()
            .Where(entity => entity.OccurredOn >= from && entity.OccurredOn <= to)
            .OrderBy(entity => entity.OccurredOn, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 2_000))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task AddBehaviorsAsync(IReadOnlyList<AvatarBehavior> behaviors, CancellationToken cancellationToken)
    {
        if (behaviors.Count == 0) return;
        await database.Insertable(behaviors.Select(ToEntity).ToList()).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<bool> DeleteBehaviorAsync(Guid behaviorId, CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarBehaviorSchemaEntity>()
            .Where(entity => entity.Id == behaviorId)
            .ExecuteCommandAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<AvatarRelationship>> ListRelationshipsAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarRelationshipSchemaEntity>()
            .OrderBy(entity => entity.Influence, OrderByType.Desc)
            .OrderBy(entity => entity.Closeness, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task AddRelationshipAsync(AvatarRelationship relationship, CancellationToken cancellationToken) =>
        await database.Insertable(ToEntity(relationship)).ExecuteCommandAsync(cancellationToken);

    public async Task<bool> UpdateRelationshipAsync(AvatarRelationship relationship, CancellationToken cancellationToken) =>
        await database.Updateable<AvatarRelationshipSchemaEntity>()
            .SetColumns(item => new AvatarRelationshipSchemaEntity
            {
                Name = relationship.Name,
                Role = relationship.Role,
                Closeness = relationship.Closeness,
                Influence = relationship.Influence,
                Notes = relationship.Notes,
                UpdatedAt = relationship.UpdatedAt
            })
            .Where(item => item.Id == relationship.Id)
            .ExecuteCommandAsync(cancellationToken) > 0;

    public async Task<bool> DeleteRelationshipAsync(Guid relationshipId, CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarRelationshipSchemaEntity>()
            .Where(entity => entity.Id == relationshipId)
            .ExecuteCommandAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<AvatarSimulation>> ListSimulationsAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarSimulationSchemaEntity>()
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<AvatarSimulation?> GetSimulationAsync(Guid simulationId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarSimulationSchemaEntity>()
            .Where(entity => entity.Id == simulationId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task AddSimulationAsync(AvatarSimulation simulation, CancellationToken cancellationToken) =>
        await database.Insertable(ToEntity(simulation)).ExecuteCommandAsync(cancellationToken);

    public async Task UpdateSimulationAsync(AvatarSimulation simulation, CancellationToken cancellationToken)
    {
        // completed_at 可空：SetColumns 会把 null 推断成 AnsiString → 42804，故走实体驱动的 UpdateColumns。
        var entity = new AvatarSimulationSchemaEntity
        {
            Id = simulation.Id,
            Status = simulation.Status,
            SuccessProbability = simulation.SuccessProbability,
            Confidence = simulation.Confidence,
            Summary = simulation.Summary,
            Report = simulation.Report,
            KeyFactors = Serialize(simulation.KeyFactors),
            Milestones = Serialize(simulation.Milestones),
            Risks = Serialize(simulation.Risks),
            NextActions = Serialize(simulation.NextActions),
            Model = simulation.Model,
            ErrorMessage = simulation.ErrorMessage,
            CompletedAt = simulation.CompletedAt
        };

        await database.Updateable(entity)
            .UpdateColumns(item => new
            {
                item.Status,
                item.SuccessProbability,
                item.Confidence,
                item.Summary,
                item.Report,
                item.KeyFactors,
                item.Milestones,
                item.Risks,
                item.NextActions,
                item.Model,
                item.ErrorMessage,
                item.CompletedAt
            })
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<bool> DeleteSimulationAsync(Guid simulationId, CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarSimulationSchemaEntity>()
            .Where(entity => entity.Id == simulationId)
            .ExecuteCommandAsync(cancellationToken) > 0;

    // ---- 证据 / 版本 ----

    public async Task AddMemoryEvidenceAsync(
        IReadOnlyList<AvatarMemoryEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (evidence.Count == 0) return;

        await database.Insertable(evidence.Select(item => new AvatarMemoryEvidenceSchemaEntity
        {
            Id = item.Id,
            MemoryId = item.MemoryId,
            FragmentId = item.FragmentId,
            EvidenceText = item.EvidenceText,
            StartOffset = item.StartOffset,
            EndOffset = item.EndOffset,
            PageNo = item.PageNo,
            MessageId = item.MessageId,
            SourceHash = item.SourceHash,
            OccurredAt = item.OccurredAt,
            RecordedAt = item.RecordedAt
        }).ToList()).ExecuteCommandAsync(cancellationToken);

        var memoryIds = evidence.Select(item => item.MemoryId).Distinct().ToList();
        var currentRows = await database.Queryable<AvatarMemorySchemaEntity>()
            .Where(entity => memoryIds.Contains(entity.Id))
            .Select(entity => new AvatarMemorySchemaEntity
            {
                Id = entity.Id,
                EvidenceCount = entity.EvidenceCount
            })
            .ToListAsync(cancellationToken);
        var currentCounts = currentRows.ToDictionary(row => row.Id, row => row.EvidenceCount);

        var now = DateTimeOffset.UtcNow;
        foreach (var memoryId in memoryIds)
        {
            var increment = evidence.Count(item => item.MemoryId == memoryId);
            await database.Updateable<AvatarMemorySchemaEntity>()
                .SetColumns(entity => new AvatarMemorySchemaEntity
                {
                    EvidenceCount = currentCounts.GetValueOrDefault(memoryId) + increment,
                    UpdatedAt = now
                })
                .Where(entity => entity.Id == memoryId)
                .ExecuteCommandAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AvatarMemoryEvidence>> ListMemoryEvidenceAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarMemoryEvidenceSchemaEntity>()
            .Where(entity => entity.MemoryId == memoryId)
            .OrderBy(entity => entity.RecordedAt, OrderByType.Asc)
            .ToListAsync(cancellationToken);

        return rows.Select(row => AvatarMemoryEvidence.Restore(
            row.Id,
            row.MemoryId,
            row.FragmentId,
            row.EvidenceText,
            row.StartOffset,
            row.EndOffset,
            row.PageNo,
            row.MessageId,
            row.SourceHash,
            row.OccurredAt,
            row.RecordedAt)).ToList();
    }

    public async Task AddMemoryVersionsAsync(
        IReadOnlyList<AvatarMemoryVersion> versions,
        CancellationToken cancellationToken)
    {
        if (versions.Count == 0) return;

        await database.Insertable(versions.Select(version => new AvatarMemoryVersionSchemaEntity
        {
            Id = version.Id,
            MemoryId = version.MemoryId,
            Version = version.Version,
            SnapshotJson = version.SnapshotJson,
            ChangeType = version.ChangeType,
            SupersedesId = version.SupersedesId,
            Reason = version.Reason,
            CreatedAt = version.CreatedAt
        }).ToList()).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvatarMemoryVersion>> ListMemoryVersionsAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarMemoryVersionSchemaEntity>()
            .Where(entity => entity.MemoryId == memoryId)
            .OrderBy(entity => entity.Version, OrderByType.Asc)
            .ToListAsync(cancellationToken);

        return rows.Select(row => AvatarMemoryVersion.Restore(
            row.Id,
            row.MemoryId,
            row.Version,
            row.SnapshotJson,
            row.ChangeType,
            row.SupersedesId,
            row.Reason,
            row.CreatedAt)).ToList();
    }

    public Task<int> CountMemoryVersionsAsync(Guid memoryId, CancellationToken cancellationToken) =>
        database.Queryable<AvatarMemoryVersionSchemaEntity>()
            .Where(entity => entity.MemoryId == memoryId)
            .CountAsync(cancellationToken);

    // ---- 投喂任务 ----

    public async Task AddIngestionAsync(AvatarIngestion ingestion, CancellationToken cancellationToken) =>
        await database.Insertable(ToEntity(ingestion)).ExecuteCommandAsync(cancellationToken);

    public async Task<AvatarIngestion?> GetIngestionAsync(Guid ingestionId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarIngestionSchemaEntity>()
            .Where(entity => entity.Id == ingestionId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<IReadOnlyList<AvatarIngestion>> ListIngestionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarIngestionSchemaEntity>()
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public Task<int> RequeueStaleIngestionsAsync(
        TimeSpan staleAfter,
        CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - staleAfter;
        return database.Ado.ExecuteCommandAsync(
            RequeueStaleIngestionsSql,
            new SugarParameter[]
            {
                new("@status", AvatarIngestion.Running),
                new("@queued", AvatarIngestion.Queued),
                new("@stage", AvatarIngestion.StageReceived),
                new("@now", DateTimeOffset.UtcNow),
                new("@threshold", threshold)
            },
            cancellationToken);
    }

    public async Task<AvatarIngestion?> ClaimNextIngestionAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarIngestionSchemaEntity>()
            .Where(entity => entity.Status == AvatarIngestion.Queued)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Asc)
            .Take(1)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return null;

        var now = DateTimeOffset.UtcNow;
        var affected = await database.Ado.ExecuteCommandAsync(
            ClaimIngestionSql,
            new SugarParameter[]
            {
                new("@id", rows[0].Id),
                new("@queued", AvatarIngestion.Queued),
                new("@running", AvatarIngestion.Running),
                new("@stage", AvatarIngestion.StageExtracting),
                new("@now", now)
            },
            cancellationToken);
        if (affected == 0) return null;

        rows[0].Status = AvatarIngestion.Running;
        rows[0].Stage = AvatarIngestion.StageExtracting;
        rows[0].StartedAt ??= now;
        rows[0].UpdatedAt = now;
        rows[0].Error = null;
        return ToDomain(rows[0]);
    }

    public Task UpdateIngestionAsync(AvatarIngestion ingestion, CancellationToken cancellationToken) =>
        database.Ado.ExecuteCommandAsync(
            UpdateIngestionSql,
            BuildIngestionUpdateParameters(ingestion),
            cancellationToken);

    /// <summary>
    /// 投喂任务的状态推进必须走显式 SQL，不能用 <c>Updateable.SetColumns</c>：
    /// SqlSugar 把「求值为 null 的 DateTimeOffset?」声明成 <c>DbType.AnsiString</c> 参数，
    /// PostgreSQL 不做 text → timestamptz 的隐式赋值，直接报
    /// <c>42804: column "completed_at" is of type timestamp with time zone but expression is of type text</c>。
    /// 这条语句是投喂流程的第一次状态回写，一旦失败任务就永远停在 running/extracting，
    /// 原文永远不进记忆、不建向量。时间列因此统一加 CAST，参数类型不再影响写入。
    /// </summary>
    internal static SugarParameter[] BuildIngestionUpdateParameters(AvatarIngestion ingestion) =>
    [
        new("@id", ingestion.Id),
        new("@sourceRef", (object?)ingestion.SourceRef ?? DBNull.Value),
        new("@status", ingestion.Status),
        new("@stage", ingestion.Stage),
        new("@error", (object?)ingestion.Error ?? DBNull.Value),
        new("@memoryCount", ingestion.MemoryCount),
        new("@insightCount", ingestion.InsightCount),
        new("@statsJson", ingestion.StatsJson),
        new("@updatedAt", ingestion.UpdatedAt),
        new("@startedAt", ingestion.StartedAt.HasValue ? (object)ingestion.StartedAt.Value : DBNull.Value),
        new("@completedAt", ingestion.CompletedAt.HasValue ? (object)ingestion.CompletedAt.Value : DBNull.Value)
    ];

    internal const string UpdateIngestionSql = """
        UPDATE avatar_ingestions SET
            source_ref = @sourceRef,
            status = @status,
            stage = @stage,
            error = @error,
            memory_count = @memoryCount,
            insight_count = @insightCount,
            stats_json = @statsJson,
            updated_at = CAST(@updatedAt AS timestamptz),
            started_at = CAST(@startedAt AS timestamptz),
            completed_at = CAST(@completedAt AS timestamptz)
        WHERE id = @id
        """;

    private const string RequeueStaleIngestionsSql = """
        UPDATE avatar_ingestions SET
            status = @queued,
            stage = @stage,
            error = NULL,
            started_at = NULL,
            updated_at = CAST(@now AS timestamptz)
        WHERE status = @status
            AND (started_at IS NULL OR started_at < CAST(@threshold AS timestamptz))
        """;

    private const string ClaimIngestionSql = """
        UPDATE avatar_ingestions SET
            status = @running,
            stage = @stage,
            started_at = CAST(@now AS timestamptz),
            updated_at = CAST(@now AS timestamptz),
            error = NULL
        WHERE id = @id AND status = @queued
        """;

    public async Task<AvatarIngestion?> FindIngestionByKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;

        var rows = await database.Queryable<AvatarIngestionSchemaEntity>()
            .Where(entity => entity.IdempotencyKey == idempotencyKey)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    // ---- 人格洞察版本 ----

    public async Task AddInsightVersionAsync(
        AvatarInsightVersion version,
        CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarInsightVersionSchemaEntity
        {
            Id = version.Id,
            InsightId = version.InsightId,
            Dimension = version.Dimension,
            Title = version.Title,
            Content = version.Content,
            Confidence = version.Confidence,
            Status = version.Status,
            Scope = version.Scope,
            EvidenceIds = Serialize(version.EvidenceIds),
            SupersedesId = version.SupersedesId,
            CreatedAt = version.CreatedAt,
            UpdatedAt = version.UpdatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task<AvatarInsightVersion?> GetInsightVersionAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarInsightVersionSchemaEntity>()
            .Where(entity => entity.Id == versionId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<IReadOnlyList<AvatarInsightVersion>> ListInsightVersionsAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<AvatarInsightVersionSchemaEntity>();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(entity => entity.Status == normalized);
        }

        var rows = await query
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task UpdateInsightVersionAsync(
        AvatarInsightVersion version,
        CancellationToken cancellationToken) =>
        await database.Updateable<AvatarInsightVersionSchemaEntity>()
            .SetColumns(entity => new AvatarInsightVersionSchemaEntity
            {
                InsightId = version.InsightId,
                Status = version.Status,
                Content = version.Content,
                Confidence = version.Confidence,
                UpdatedAt = version.UpdatedAt
            })
            .Where(entity => entity.Id == version.Id)
            .ExecuteCommandAsync(cancellationToken);

    // ---- 回答反馈 ----

    public async Task AddFeedbackAsync(AvatarFeedback feedback, CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarFeedbackSchemaEntity
        {
            Id = feedback.Id,
            ChatMessageId = feedback.ChatMessageId,
            MemoryIds = Serialize(feedback.MemoryIds),
            FeedbackType = feedback.FeedbackType,
            ReasonCode = feedback.ReasonCode,
            Comment = feedback.Comment,
            CorrectedText = feedback.CorrectedText,
            IngestionId = feedback.IngestionId,
            CreatedAt = feedback.CreatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task<IReadOnlyList<AvatarFeedback>> ListFeedbackAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarFeedbackSchemaEntity>()
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 1_000))
            .ToListAsync(cancellationToken);

        return rows.Select(row => AvatarFeedback.Restore(
            row.Id,
            row.ChatMessageId,
            Deserialize<Guid>(row.MemoryIds),
            row.FeedbackType,
            row.ReasonCode,
            row.Comment,
            row.CorrectedText,
            row.IngestionId,
            row.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, (int Like, int Unlike)>> GetMemoryFeedbackStatsAsync(
        IReadOnlyList<Guid> memoryIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, (int Like, int Unlike)>();
        if (memoryIds.Count == 0) return result;

        var wanted = memoryIds.ToHashSet();
        var rows = await database.Queryable<AvatarFeedbackSchemaEntity>()
            .Where(entity => entity.FeedbackType == AvatarFeedback.Like
                || entity.FeedbackType == AvatarFeedback.Unlike)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Desc)
            .Take(2_000)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            foreach (var memoryId in Deserialize<Guid>(row.MemoryIds))
            {
                if (!wanted.Contains(memoryId)) continue;
                var current = result.GetValueOrDefault(memoryId);
                result[memoryId] = row.FeedbackType == AvatarFeedback.Like
                    ? (current.Like + 1, current.Unlike)
                    : (current.Like, current.Unlike + 1);
            }
        }

        return result;
    }

    // ---- 实体 ----

    public async Task<IReadOnlyList<AvatarEntity>> ListEntitiesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarEntitySchemaEntity>()
            .OrderBy(entity => entity.UpdatedAt, OrderByType.Desc)
            .Take(1_000)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task UpsertEntitiesAsync(
        IReadOnlyList<AvatarEntity> entities,
        CancellationToken cancellationToken)
    {
        foreach (var entity in entities)
        {
            var existing = await database.Queryable<AvatarEntitySchemaEntity>()
                .Where(item => item.EntityType == entity.EntityType && item.Name == entity.Name)
                .Take(1)
                .ToListAsync(cancellationToken);

            if (existing.Count == 0)
            {
                await database.Insertable(ToEntity(entity)).ExecuteCommandAsync(cancellationToken);
                continue;
            }

            await database.Updateable<AvatarEntitySchemaEntity>()
                .SetColumns(item => new AvatarEntitySchemaEntity
                {
                    Aliases = Serialize(entity.Aliases),
                    MetadataJson = entity.MetadataJson,
                    UpdatedAt = entity.UpdatedAt
                })
                .Where(item => item.Id == existing[0].Id)
                .ExecuteCommandAsync(cancellationToken);
        }
    }

    public async Task AddMemoryEntityLinksAsync(
        IReadOnlyList<AvatarMemoryEntityLink> links,
        CancellationToken cancellationToken)
    {
        if (links.Count == 0) return;

        var memoryIds = links.Select(link => link.MemoryId).Distinct().ToList();
        await database.Deleteable<AvatarMemoryEntitySchemaEntity>()
            .Where(entity => memoryIds.Contains(entity.MemoryId))
            .ExecuteCommandAsync(cancellationToken);

        var rows = links
            .GroupBy(link => (link.MemoryId, link.EntityId))
            .Select(group => new AvatarMemoryEntitySchemaEntity
            {
                Id = Guid.NewGuid(),
                MemoryId = group.Key.MemoryId,
                EntityId = group.Key.EntityId,
                Role = group.First().Role
            })
            .ToList();
        await database.Insertable(rows).ExecuteCommandAsync(cancellationToken);
    }

    // ---- 评测 ----

    public async Task AddEvalCaseAsync(AvatarEvalCase evalCase, CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarEvalCaseSchemaEntity
        {
            Id = evalCase.Id,
            Category = evalCase.Category,
            Question = evalCase.Question,
            ExpectedAnswer = evalCase.ExpectedAnswer,
            MustMemoryIds = Serialize(evalCase.MustMemoryIds),
            ForbiddenMemoryIds = Serialize(evalCase.ForbiddenMemoryIds),
            Rubric = Serialize(evalCase.Rubric),
            Split = evalCase.Split,
            Active = evalCase.Active,
            CreatedAt = evalCase.CreatedAt,
            UpdatedAt = evalCase.UpdatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task<AvatarEvalCase?> GetEvalCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarEvalCaseSchemaEntity>()
            .Where(entity => entity.Id == caseId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<IReadOnlyList<AvatarEvalCase>> ListEvalCasesAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<AvatarEvalCaseSchemaEntity>();
        if (!includeInactive) query = query.Where(entity => entity.Active);

        var rows = await query
            .OrderBy(entity => entity.Category, OrderByType.Asc)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Asc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<bool> DeleteEvalCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
        await database.Deleteable<AvatarEvalCaseSchemaEntity>()
            .Where(entity => entity.Id == caseId)
            .ExecuteCommandAsync(cancellationToken) > 0;

    public async Task AddEvalRunAsync(AvatarEvalRun run, CancellationToken cancellationToken) =>
        await database.Insertable(new AvatarEvalRunSchemaEntity
        {
            Id = run.Id,
            Status = run.Status,
            Total = run.Total,
            Passed = run.Passed,
            FactErrorCount = run.FactErrorCount,
            CitationAccuracy = run.CitationAccuracy,
            StyleScore = run.StyleScore,
            Summary = run.Summary,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt
        }).ExecuteCommandAsync(cancellationToken);

    public async Task UpdateEvalRunAsync(AvatarEvalRun run, CancellationToken cancellationToken)
    {
        // completed_at 可空：同 UpdateMemoriesAsync，用实体驱动绑定避免 null 被推断成 AnsiString。
        var entity = new AvatarEvalRunSchemaEntity
        {
            Id = run.Id,
            Status = run.Status,
            Total = run.Total,
            Passed = run.Passed,
            FactErrorCount = run.FactErrorCount,
            CitationAccuracy = run.CitationAccuracy,
            StyleScore = run.StyleScore,
            Summary = run.Summary,
            CompletedAt = run.CompletedAt
        };

        await database.Updateable(entity)
            .UpdateColumns(item => new
            {
                item.Status,
                item.Total,
                item.Passed,
                item.FactErrorCount,
                item.CitationAccuracy,
                item.StyleScore,
                item.Summary,
                item.CompletedAt
            })
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<AvatarEvalRun?> GetEvalRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarEvalRunSchemaEntity>()
            .Where(entity => entity.Id == runId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<IReadOnlyList<AvatarEvalRun>> ListEvalRunsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarEvalRunSchemaEntity>()
            .OrderBy(entity => entity.StartedAt, OrderByType.Desc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task AddEvalResultsAsync(
        IReadOnlyList<AvatarEvalResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0) return;
        await database.Insertable(results.Select(result => new AvatarEvalResultSchemaEntity
        {
            Id = result.Id,
            RunId = result.RunId,
            CaseId = result.CaseId,
            Question = result.Question,
            Answer = result.Answer,
            CitationIds = Serialize(result.CitationIds),
            Passed = result.Passed,
            Score = result.Score,
            Detail = result.Detail,
            CreatedAt = result.CreatedAt
        }).ToList()).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvatarEvalResult>> ListEvalResultsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<AvatarEvalResultSchemaEntity>()
            .Where(entity => entity.RunId == runId)
            .OrderBy(entity => entity.CreatedAt, OrderByType.Asc)
            .ToListAsync(cancellationToken);

        return rows.Select(row => AvatarEvalResult.Restore(
            row.Id,
            row.RunId,
            row.CaseId,
            row.Question,
            row.Answer,
            Deserialize<Guid>(row.CitationIds),
            row.Passed,
            row.Score,
            row.Detail,
            row.CreatedAt)).ToList();
    }

    private static AvatarIngestion ToDomain(AvatarIngestionSchemaEntity entity) =>
        AvatarIngestion.Restore(
            entity.Id,
            entity.SourceType,
            entity.SourceRef,
            entity.IdempotencyKey,
            Deserialize<Guid>(entity.FragmentIds),
            Deserialize<Guid>(entity.SupersedesIds),
            entity.Status,
            entity.Stage,
            entity.Error,
            entity.MemoryCount,
            entity.InsightCount,
            entity.StatsJson,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.StartedAt,
            entity.CompletedAt);

    private static AvatarIngestionSchemaEntity ToEntity(AvatarIngestion ingestion) => new()
    {
        Id = ingestion.Id,
        SourceType = ingestion.SourceType,
        SourceRef = ingestion.SourceRef,
        IdempotencyKey = ingestion.IdempotencyKey,
        FragmentIds = Serialize(ingestion.FragmentIds),
        SupersedesIds = Serialize(ingestion.SupersedesMemoryIds),
        Status = ingestion.Status,
        Stage = ingestion.Stage,
        Error = ingestion.Error,
        MemoryCount = ingestion.MemoryCount,
        InsightCount = ingestion.InsightCount,
        StatsJson = ingestion.StatsJson,
        CreatedAt = ingestion.CreatedAt,
        UpdatedAt = ingestion.UpdatedAt,
        StartedAt = ingestion.StartedAt,
        CompletedAt = ingestion.CompletedAt
    };

    private static AvatarInsightVersion ToDomain(AvatarInsightVersionSchemaEntity entity) =>
        AvatarInsightVersion.Restore(
            entity.Id,
            entity.InsightId,
            entity.Dimension,
            entity.Title,
            entity.Content,
            entity.Confidence,
            entity.Status,
            entity.Scope,
            Deserialize<Guid>(entity.EvidenceIds),
            entity.SupersedesId,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static AvatarEntity ToDomain(AvatarEntitySchemaEntity entity) =>
        AvatarEntity.Restore(
            entity.Id,
            entity.EntityType,
            entity.Name,
            Deserialize<string>(entity.Aliases),
            entity.MetadataJson,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static AvatarEntitySchemaEntity ToEntity(AvatarEntity entity) => new()
    {
        Id = entity.Id,
        EntityType = entity.EntityType,
        Name = entity.Name,
        Aliases = Serialize(entity.Aliases),
        MetadataJson = entity.MetadataJson,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };

    private static AvatarEvalCase ToDomain(AvatarEvalCaseSchemaEntity entity) =>
        AvatarEvalCase.Restore(
            entity.Id,
            entity.Category,
            entity.Question,
            entity.ExpectedAnswer,
            Deserialize<Guid>(entity.MustMemoryIds),
            Deserialize<Guid>(entity.ForbiddenMemoryIds),
            Deserialize<string>(entity.Rubric),
            entity.Split,
            entity.Active,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static AvatarEvalRun ToDomain(AvatarEvalRunSchemaEntity entity) =>
        AvatarEvalRun.Restore(
            entity.Id,
            entity.Status,
            entity.Total,
            entity.Passed,
            entity.FactErrorCount,
            entity.CitationAccuracy,
            entity.StyleScore,
            entity.Summary,
            entity.StartedAt,
            entity.CompletedAt);

    private static AvatarProfile ToDomain(AvatarProfileSchemaEntity entity) =>
        AvatarProfile.Restore(
            entity.Id,
            entity.DisplayName,
            entity.Headline,
            entity.Personality,
            entity.Values,
            entity.Strengths,
            entity.Weaknesses,
            entity.Skills,
            entity.Resources,
            entity.Constraints,
            entity.LongTermGoals,
            entity.RiskPreference,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static AvatarProfileSchemaEntity ToEntity(AvatarProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        Headline = profile.Headline,
        Personality = profile.Personality,
        Values = profile.Values,
        Strengths = profile.Strengths,
        Weaknesses = profile.Weaknesses,
        Skills = profile.Skills,
        Resources = profile.Resources,
        Constraints = profile.Constraints,
        LongTermGoals = profile.LongTermGoals,
        RiskPreference = profile.RiskPreference,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt
    };

    private static AvatarExperience ToDomain(AvatarExperienceSchemaEntity entity) =>        AvatarExperience.Restore(
            entity.Id,
            entity.OccurredAt,
            entity.Category,
            entity.Title,
            entity.Content,
            entity.Outcome,
            entity.Emotion,
            entity.Impact,
            Deserialize<string>(entity.Tags),
            entity.CreatedAt);

    private static AvatarExperienceSchemaEntity ToEntity(AvatarExperience experience) => new()
    {
        Id = experience.Id,
        OccurredAt = experience.OccurredAt,
        Category = experience.Category,
        Title = experience.Title,
        Content = experience.Content,
        Outcome = experience.Outcome,
        Emotion = experience.Emotion,
        Impact = experience.Impact,
        Tags = Serialize(experience.Tags),
        CreatedAt = experience.CreatedAt
    };

    private static AvatarFragment ToDomain(AvatarFragmentSchemaEntity entity) =>
        AvatarFragment.Restore(
            entity.Id,
            entity.Kind,
            entity.Title,
            entity.Content,
            entity.OccurredAt,
            entity.Source,
            Deserialize<string>(entity.Tags),
            entity.IsDigested,
            entity.CreatedAt);

    private static AvatarFragmentSchemaEntity ToEntity(AvatarFragment fragment) => new()
    {
        Id = fragment.Id,
        Kind = fragment.Kind,
        Title = fragment.Title,
        Content = fragment.Content,
        OccurredAt = fragment.OccurredAt,
        Source = fragment.Source,
        Tags = Serialize(fragment.Tags),
        IsDigested = fragment.IsDigested,
        CreatedAt = fragment.CreatedAt
    };

    private static AvatarMemory ToDomain(AvatarMemorySchemaEntity entity) =>
        AvatarMemory.RestoreV2(
            entity.Id,
            entity.FragmentId,
            entity.Kind,
            entity.Title,
            entity.Summary,
            entity.Content,
            entity.OccurredAt,
            Deserialize<string>(entity.People),
            Deserialize<string>(entity.Tags),
            entity.Emotion,
            entity.Importance,
            entity.Source,
            entity.VectorState,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.MemoryType,
            entity.Stability,
            entity.Confidence,
            entity.LifecycleStatus,
            entity.ValidFrom,
            entity.ValidTo,
            entity.SupersedesId,
            entity.StructuredData,
            entity.PrivacyLevel,
            entity.LastConfirmedAt,
            entity.LastUsedAt,
            entity.EvidenceCount);

    private static AvatarMemorySchemaEntity ToEntity(AvatarMemory memory) => new()
    {
        Id = memory.Id,
        FragmentId = memory.FragmentId,
        Kind = memory.Kind,
        Title = memory.Title,
        Summary = memory.Summary,
        Content = memory.Content,
        OccurredAt = memory.OccurredAt,
        People = Serialize(memory.People),
        Tags = Serialize(memory.Tags),
        Emotion = memory.Emotion,
        Importance = memory.Importance,
        Source = memory.Source,
        VectorState = memory.VectorState,
        MemoryType = memory.MemoryType,
        Stability = memory.Stability,
        Confidence = memory.Confidence,
        LifecycleStatus = memory.LifecycleStatus,
        ValidFrom = memory.ValidFrom,
        ValidTo = memory.ValidTo,
        SupersedesId = memory.SupersedesId,
        StructuredData = memory.StructuredData,
        PrivacyLevel = memory.PrivacyLevel,
        LastConfirmedAt = memory.LastConfirmedAt,
        LastUsedAt = memory.LastUsedAt,
        EvidenceCount = memory.EvidenceCount,
        CreatedAt = memory.CreatedAt,
        UpdatedAt = memory.UpdatedAt
    };

    private static AvatarInsight ToDomain(AvatarInsightSchemaEntity entity) =>
        AvatarInsight.Restore(
            entity.Id,
            entity.Dimension,
            entity.Title,
            entity.Content,
            entity.Confidence,
            entity.EvidenceCount,
            Deserialize<string>(entity.Evidence),
            entity.UpdatedAt);

    private static AvatarInsightSchemaEntity ToEntity(AvatarInsight insight) => new()
    {
        Id = insight.Id,
        Dimension = insight.Dimension,
        Title = insight.Title,
        Content = insight.Content,
        Confidence = insight.Confidence,
        EvidenceCount = insight.EvidenceCount,
        Evidence = Serialize(insight.Evidence),
        UpdatedAt = insight.UpdatedAt
    };

    private static AvatarBehavior ToDomain(AvatarBehaviorSchemaEntity entity) =>
        AvatarBehavior.Restore(
            entity.Id,
            entity.OccurredOn,
            entity.Category,
            entity.Description,
            entity.DurationMinutes,
            entity.Mood,
            Deserialize<string>(entity.Tags),
            entity.Source,
            entity.CreatedAt);

    private static AvatarBehaviorSchemaEntity ToEntity(AvatarBehavior behavior) => new()
    {
        Id = behavior.Id,
        OccurredOn = behavior.OccurredOn,
        Category = behavior.Category,
        Description = behavior.Description,
        DurationMinutes = behavior.DurationMinutes,
        Mood = behavior.Mood,
        Tags = Serialize(behavior.Tags),
        Source = behavior.Source,
        CreatedAt = behavior.CreatedAt
    };

    private static AvatarRelationship ToDomain(AvatarRelationshipSchemaEntity entity) =>
        AvatarRelationship.Restore(
            entity.Id,
            entity.Name,
            entity.Role,
            entity.Closeness,
            entity.Influence,
            entity.Notes,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static AvatarRelationshipSchemaEntity ToEntity(AvatarRelationship relationship) => new()
    {
        Id = relationship.Id,
        Name = relationship.Name,
        Role = relationship.Role,
        Closeness = relationship.Closeness,
        Influence = relationship.Influence,
        Notes = relationship.Notes,
        CreatedAt = relationship.CreatedAt,
        UpdatedAt = relationship.UpdatedAt
    };

    private static AvatarSimulation ToDomain(AvatarSimulationSchemaEntity entity) =>
        AvatarSimulation.Restore(
            entity.Id,
            entity.Scenario,
            entity.Goal,
            entity.HorizonMonths,
            entity.Status,
            entity.SuccessProbability,
            entity.Confidence,
            entity.Summary,
            entity.Report,
            Deserialize<AvatarKeyFactor>(entity.KeyFactors),
            Deserialize<AvatarMilestone>(entity.Milestones),
            Deserialize<AvatarRisk>(entity.Risks),
            Deserialize<string>(entity.NextActions),
            entity.Model,
            entity.ErrorMessage,
            entity.CreatedAt,
            entity.CompletedAt);

    private static AvatarSimulationSchemaEntity ToEntity(AvatarSimulation simulation) => new()
    {
        Id = simulation.Id,
        Scenario = simulation.Scenario,
        Goal = simulation.Goal,
        HorizonMonths = simulation.HorizonMonths,
        Status = simulation.Status,
        SuccessProbability = simulation.SuccessProbability,
        Confidence = simulation.Confidence,
        Summary = simulation.Summary,
        Report = simulation.Report,
        KeyFactors = Serialize(simulation.KeyFactors),
        Milestones = Serialize(simulation.Milestones),
        Risks = Serialize(simulation.Risks),
        NextActions = Serialize(simulation.NextActions),
        Model = simulation.Model,
        ErrorMessage = simulation.ErrorMessage,
        CreatedAt = simulation.CreatedAt,
        CompletedAt = simulation.CompletedAt
    };

    private static string Serialize<T>(IReadOnlyList<T> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static IReadOnlyList<T> Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>pgvector 检索投影行。列名在 SQL 中显式引用，避免 Npgsql 读取 vector 类型。</summary>
public sealed class AvatarMemoryVectorRow
{
    public AvatarMemoryVectorRow()
    {
    }

    public Guid MemoryId { get; set; }

    public double Similarity { get; set; }
}
