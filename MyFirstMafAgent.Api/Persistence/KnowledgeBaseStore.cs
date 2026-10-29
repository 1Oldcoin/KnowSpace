using System.Security.Cryptography;
using System.Text;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

public sealed class KnowledgeBaseStore(SqlSugarScope database) : IDocumentIndexSource, IKnowledgeBaseMaintenanceStore
{
    // 取值来自 Domain/Knowledge/KnowledgeBaseDefaults：应用层判断「是不是默认库」时不能反向依赖持久化层。
    public static readonly Guid DefaultKnowledgeBaseId = KnowledgeBaseDefaults.DefaultKnowledgeBaseId;
    public const string DefaultKnowledgeBaseName = KnowledgeBaseDefaults.DefaultName;

    private const string QueuedStatus = "Queued";
    private const string ProcessingStatus = "Processing";
    private const string ReadyStatus = "Ready";
    private const string FailedStatus = "Failed";
    private const string QueuedStage = "排队中";
    private const string WaitingStage = "等待解析";
    private const string StartingStage = "开始解析";
    private const string UploadingStatus = "Uploading";
    private const string CompletedStatus = "Completed";

    /// <param name="ensureSchema">
    /// false = 结构已由 <see cref="Hosting.SchemaStamp"/> 证明是最新的，跳过建表/补列/建索引。
    /// 这一段是本项目启动耗时第一大户（实测 1.0–1.5 秒）：15 张表的 catalog 往返加上
    /// <c>kb_chunks</c> 的兼容性检查，结果在两次启动之间完全一致。
    /// </param>
    public async Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (ensureSchema)
            await EnsureSchemaAsync(cancellationToken);

        // 默认知识库的"没有就插一条"是数据步骤，与建表无关：跳过了建表也必须有这个库。
        await EnsureDefaultKnowledgeBaseAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        // pgvector 扩展是数据库级对象，SqlSugar 没有对应的 DbMaintenance API，DDL 统一封装在 PgVectorSql。
        await PgVectorSql.EnsureExtensionAsync(database, cancellationToken);

        database.CodeFirst.InitTables<KnowledgeBaseSchemaEntity>();

        // 历史库需要先补齐 kb_documents 的列，再让 CodeFirst 同步表结构。
        EnsureDocumentColumns();
        await PgVectorSql.EnsureChunkCompatibilityAsync(database, cancellationToken);
        EnsureChunkColumns();

        database.CodeFirst.InitTables<KnowledgeDocumentSchemaEntity>();
        database.CodeFirst.InitTables<KnowledgeChunkSchemaEntity>();
        database.CodeFirst.InitTables<KnowledgeUploadSessionSchemaEntity>();

        // 词法检索依赖 kb_chunks.lexical_tokens 的 GIN 表达式索引；按列建索引，必须在补列之后。
        await database.Ado.ExecuteCommandAsync(LexicalSearchSql.EnsureIndexSql, null, cancellationToken);

        // 索引生命周期、事务性 Outbox、摘要缓存与模型规格表。
        database.CodeFirst.InitTables<EmbeddingSpecSchemaEntity>();
        database.CodeFirst.InitTables<ModelBindingSchemaEntity>();
        database.CodeFirst.InitTables<DocumentVersionSchemaEntity>();
        database.CodeFirst.InitTables<KnowledgeIndexSchemaEntity>();
        database.CodeFirst.InitTables<ActiveIndexSchemaEntity>();
        database.CodeFirst.InitTables<IndexVectorSchemaEntity>();
        database.CodeFirst.InitTables<OutboxEventSchemaEntity>();
        database.CodeFirst.InitTables<DocumentSummarySchemaEntity>();
    }

    private async Task EnsureDefaultKnowledgeBaseAsync(CancellationToken cancellationToken)
    {
        var storage = await database.Storageable(new KnowledgeBaseSchemaEntity
        {
            Id = DefaultKnowledgeBaseId,
            Name = DefaultKnowledgeBaseName,
            CreatedAt = DateTimeOffset.UtcNow
        }).WhereColumns(entity => entity.Id).ToStorageAsync();

        if (storage.InsertList.Count > 0)
            await storage.AsInsertable.ExecuteCommandAsync(cancellationToken);
    }

    private void EnsureDocumentColumns()
    {
        if (!database.DbMaintenance.IsAnyTable("kb_documents", false)) return;

        // knowledge_base_id 带上默认值，旧的文档行会随新增列一起归属默认知识库。
        EnsureDocumentColumn("knowledge_base_id", "uuid", false, DefaultKnowledgeBaseId.ToString());
        EnsureDocumentColumn("topic_id", "uuid", true, null);
        EnsureDocumentColumn("summary", "text", true, null);
        EnsureDocumentColumn("progress", "integer", false, "0");
        EnsureDocumentColumn("processing_stage", "text", true, null);
        EnsureDocumentColumn("updated_at", "timestamptz", false, "now()");
        EnsureDocumentColumn("file_hash", "text", true, null);
        EnsureDocumentColumn("content_hash", "text", true, null);
        EnsureDocumentColumn("content_version", "integer", false, "0");
    }

    private void EnsureDocumentColumn(string columnName, string dataType, bool isNullable, string? defaultValue)
    {
        if (database.DbMaintenance.IsAnyColumn("kb_documents", columnName, false)) return;

        database.DbMaintenance.AddColumn("kb_documents", new DbColumnInfo
        {
            DbColumnName = columnName,
            DataType = dataType,
            IsNullable = isNullable,
            DefaultValue = defaultValue
        });
    }

    private void EnsureChunkColumns()
    {
        if (!database.DbMaintenance.IsAnyTable("kb_chunks", false)) return;

        EnsureChunkColumn("document_version", "integer", false, "1");
        EnsureChunkColumn("content_hash", "text", true, null);

        // 词法检索的预分词列：旧库必须显式补列，否则 GIN 索引建不出来。
        EnsureChunkColumn("lexical_tokens", "text", true, null);
    }

    private void EnsureChunkColumn(string columnName, string dataType, bool isNullable, string? defaultValue)
    {
        if (database.DbMaintenance.IsAnyColumn("kb_chunks", columnName, false)) return;

        database.DbMaintenance.AddColumn("kb_chunks", new DbColumnInfo
        {
            DbColumnName = columnName,
            DataType = dataType,
            IsNullable = isNullable,
            DefaultValue = defaultValue
        });
    }

    /// <summary>
    /// 为缺少预分词的历史切片补齐 <c>lexical_tokens</c>（一次一批，返回处理条数）。
    /// <para>
    /// 分词必须在应用侧完成（PostgreSQL 默认解析器不认中文），所以无法用一条 UPDATE 补齐；
    /// 由 <c>LexicalIndexWorker</c> 在后台分批推进，新切片在发布时已经带分词。
    /// 空内容会写成空字符串而不是 NULL，避免同一行被反复拾取。
    /// </para>
    /// </summary>
    public async Task<int> BackfillLexicalTokensAsync(int batchSize, CancellationToken cancellationToken)
    {
        var rows = await database.Ado.SqlQueryAsync<LexicalBackfillRow>(
            LexicalSearchSql.PendingTokensSql,
            new { limit = Math.Clamp(batchSize, 1, 2_000) },
            cancellationToken);

        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            await database.Updateable<KnowledgeChunkSchemaEntity>()
                .SetColumns(chunk => new KnowledgeChunkSchemaEntity
                {
                    LexicalTokens = LexicalTokens.Build(row.Content)
                })
                .Where(chunk => chunk.Id == row.Id)
                .ExecuteCommandAsync(cancellationToken);
        }

        return rows.Count;
    }

    public async Task<bool> RetryDocumentAsync(Guid id, CancellationToken cancellationToken)
    {
        var updated = await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Status = QueuedStatus,
                Progress = 0,
                ProcessingStage = QueuedStage,
                Error = null,
                Summary = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == id && (document.Status == FailedStatus || document.Status == ReadyStatus))
            .ExecuteCommandAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<string?> GetDocumentStoragePathAsync(Guid id, CancellationToken cancellationToken)
    {
        var paths = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Id == id)
            .Select(document => document.StoragePath)
            .Take(1)
            .ToListAsync(cancellationToken);
        return paths.Count == 0 ? null : paths[0];
    }

    /// <summary>读取资料所属知识库；不存在返回 null。用于"按知识库范围授权"的判定。</summary>
    public async Task<Guid?> GetDocumentKnowledgeBaseIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Id == id)
            .Select(document => document.KnowledgeBaseId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// 替换资料的源文件并重新排队解析。
    ///
    /// <para>
    /// 只接受 Ready/Failed 状态的资料：正在 Queued/Processing 时替换源文件，
    /// 会让 Worker 手上的旧路径失效（文件已被删除），导致处理失败。
    /// 返回 false 表示"稍后再试"，由调用方保留旧哈希，下次同步重新判定。
    /// </para>
    /// </summary>
    public async Task<bool> ReplaceDocumentSourceAsync(
        Guid id,
        string storagePath,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var updated = await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                StoragePath = storagePath,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                Status = QueuedStatus,
                Progress = 0,
                ProcessingStage = QueuedStage,
                Error = null,
                // 旧摘要已过期；Worker 重新解析后会重新生成。
                Summary = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == id
                && document.Status != QueuedStatus
                && document.Status != ProcessingStatus)
            .ExecuteCommandAsync(cancellationToken);

        return updated > 0;
    }

    public async Task<string?> DeleteDocumentAsync(Guid id, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            var document = await database.Queryable<KnowledgeDocumentSchemaEntity>()
                .Where(item => item.Id == id)
                .FirstAsync(cancellationToken);
            if (document is null)
            {
                await database.Ado.RollbackTranAsync();
                return null;
            }

            // 删除事件与资料删除在同一事务内写入，索引 Worker 可据此清理向量。
            var idempotencyKey = $"document:{id}:deleted";
            var eventExists = await database.Queryable<OutboxEventSchemaEntity>()
                .AnyAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (!eventExists)
            {
                await database.Insertable(new OutboxEventSchemaEntity
                {
                    EventType = "DocumentDeleted",
                    KnowledgeBaseId = document.KnowledgeBaseId,
                    DocumentId = document.Id,
                    DocumentVersion = document.ContentVersion,
                    IdempotencyKey = idempotencyKey,
                    OccurredAt = DateTimeOffset.UtcNow
                }).ExecuteCommandAsync(cancellationToken);
            }

            // 上传会话对文档的外键没有级联删除，需要先解除引用，否则删除文档会违反外键约束。
            await database.Updateable<KnowledgeUploadSessionSchemaEntity>()
                .SetColumns(session => new KnowledgeUploadSessionSchemaEntity { DocumentId = null })
                .Where(session => session.DocumentId == id)
                .ExecuteCommandAsync(cancellationToken);

            await database.Deleteable<KnowledgeChunkSchemaEntity>()
                .Where(chunk => chunk.DocumentId == id)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<DocumentVersionSchemaEntity>()
                .Where(version => version.DocumentId == id)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<DocumentSummarySchemaEntity>()
                .Where(summary => summary.DocumentId == id)
                .ExecuteCommandAsync(cancellationToken);

            var deleted = await database.Deleteable<KnowledgeDocumentSchemaEntity>()
                .Where(item => item.Id == id)
                .ExecuteCommandAsync(cancellationToken);

            if (deleted == 0)
            {
                await database.Ado.RollbackTranAsync();
                return null;
            }

            await database.Ado.CommitTranAsync();
            return document.StoragePath;
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task CreateUploadSessionAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await database.Insertable(new KnowledgeUploadSessionSchemaEntity
        {
            Id = session.Id,
            KnowledgeBaseId = session.KnowledgeBaseId,
            FileName = session.FileName,
            Extension = session.Extension,
            ContentType = session.ContentType,
            SizeBytes = session.SizeBytes,
            UploadedBytes = session.UploadedBytes,
            StagingPath = session.StagingPath,
            Status = session.Status,
            DocumentId = session.DocumentId,
            CreatedAt = now,
            UpdatedAt = now
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<UploadSession?> GetUploadSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await database.Queryable<KnowledgeUploadSessionSchemaEntity>()
            .Where(entity => entity.Id == id)
            .FirstAsync(cancellationToken);
        return session is null ? null : ToUploadSession(session);
    }

    public async Task UpdateUploadSessionProgressAsync(Guid id, long uploadedBytes, CancellationToken cancellationToken)
    {
        var updated = await database.Updateable<KnowledgeUploadSessionSchemaEntity>()
            .SetColumns(session => new KnowledgeUploadSessionSchemaEntity
            {
                UploadedBytes = uploadedBytes,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(session => session.Id == id && session.Status == UploadingStatus && session.UploadedBytes <= uploadedBytes)
            .ExecuteCommandAsync(cancellationToken);
        if (updated == 0)
            throw new InvalidOperationException("The upload session changed while receiving a chunk.");
    }

    public async Task CompleteUploadSessionAsync(Guid sessionId, KnowledgeDocument document, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            if (!await database.Queryable<KnowledgeDocumentSchemaEntity>()
                    .AnyAsync(entity => entity.Id == document.Id, cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                await database.Insertable(new KnowledgeDocumentSchemaEntity
                {
                    Id = document.Id,
                    KnowledgeBaseId = document.KnowledgeBaseId,
                    FileName = document.FileName,
                    Extension = document.Extension,
                    ContentType = document.ContentType,
                    SizeBytes = document.SizeBytes,
                    StoragePath = document.StoragePath,
                    Status = document.Status,
                    ProcessingStage = QueuedStage,
                    CreatedAt = now,
                    UpdatedAt = now
                }).ExecuteCommandAsync(cancellationToken);
            }

            var updated = await database.Updateable<KnowledgeUploadSessionSchemaEntity>()
                .SetColumns(session => new KnowledgeUploadSessionSchemaEntity
                {
                    Status = CompletedStatus,
                    DocumentId = document.Id,
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(session => session.Id == sessionId
                    && (session.Status == UploadingStatus || session.DocumentId == document.Id))
                .ExecuteCommandAsync(cancellationToken);
            if (updated == 0)
                throw new InvalidOperationException("The upload session could not be marked as completed.");

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task RecoverProcessingDocumentsAsync(CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Status = QueuedStatus,
                Progress = 0,
                ProcessingStage = WaitingStage,
                Error = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Status == ProcessingStatus)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<QueuedDocument?> TryClaimQueuedDocumentAsync(CancellationToken cancellationToken)
    {
        var candidate = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Status == QueuedStatus)
            .OrderBy(document => document.CreatedAt, OrderByType.Asc)
            .FirstAsync(cancellationToken);
        if (candidate is null) return null;

        // 条件更新保证只有一个处理者能抢到该文档；抢不到时下一个轮询周期会重试。
        var claimed = await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Status = ProcessingStatus,
                Progress = 1,
                ProcessingStage = StartingStage,
                Error = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == candidate.Id && document.Status == QueuedStatus)
            .ExecuteCommandAsync(cancellationToken);
        if (claimed == 0) return null;

        return new QueuedDocument(candidate.Id, candidate.KnowledgeBaseId, candidate.FileName, candidate.Extension,
            candidate.ContentType, candidate.SizeBytes, candidate.StoragePath);
    }

    /// <summary>
    /// 按 ID 抢占指定资料（<c>Queued → Processing</c>）。
    ///
    /// <para>
    /// 与 <see cref="TryClaimQueuedDocumentAsync"/> 的区别是"指定目标"而不是"随便一篇"：
    /// 任务系统是按资料 ID 派活的，抢占失败必须明确返回 null，让任务可以判定为
    /// 「已在别处处理」而不是「随便捡了另一篇」。
    /// </para>
    /// </summary>
    public async Task<QueuedDocument?> TryClaimDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var candidate = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Id == documentId && document.Status == QueuedStatus)
            .FirstAsync(cancellationToken);
        if (candidate is null) return null;

        var claimed = await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Status = ProcessingStatus,
                Progress = 1,
                ProcessingStage = StartingStage,
                Error = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == documentId && document.Status == QueuedStatus)
            .ExecuteCommandAsync(cancellationToken);
        if (claimed == 0) return null;

        return new QueuedDocument(candidate.Id, candidate.KnowledgeBaseId, candidate.FileName, candidate.Extension,
            candidate.ContentType, candidate.SizeBytes, candidate.StoragePath);
    }

    /// <summary>
    /// 把资料放回排队状态，供失败重试使用。
    /// 不清空内容版本：重新解析成功后会发布新版本，旧版本按 Superseded 清理。
    /// </summary>
    public async Task ResetDocumentToQueuedAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Status = QueuedStatus,
                Progress = 0,
                ProcessingStage = WaitingStage,
                Error = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == documentId)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>列出仍处于排队状态的资料，供任务系统补投（带上 UpdatedAt，用于判断“是用户重新排的队”）。</summary>
    public async Task<IReadOnlyList<QueuedDocumentEntry>> ListQueuedDocumentIdsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Status == QueuedStatus)
            .OrderBy(document => document.CreatedAt, OrderByType.Asc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(row => new QueuedDocumentEntry(row.Id, row.KnowledgeBaseId, row.UpdatedAt)).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeBaseInfo>> ListKnowledgeBasesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeBaseSchemaEntity>()
            .OrderBy(entity => entity.CreatedAt, OrderByType.Asc)
            .OrderBy(entity => entity.Name, OrderByType.Asc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToKnowledgeBase).ToList();
    }

    public async Task<KnowledgeBaseInfo?> GetKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<KnowledgeBaseSchemaEntity>()
            .Where(entity => entity.Id == id)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToKnowledgeBase(row);
    }

    public async Task<KnowledgeBaseInfo?> CreateKnowledgeBaseAsync(string name, CancellationToken cancellationToken)
    {
        var exists = await database.Queryable<KnowledgeBaseSchemaEntity>()
            .AnyAsync(entity => entity.Name.ToLower() == name.ToLower(), cancellationToken);
        if (exists) return null;

        var now = DateTimeOffset.UtcNow;
        var knowledgeBase = new KnowledgeBaseSchemaEntity { Id = Guid.NewGuid(), Name = name, CreatedAt = now };
        await database.Insertable(knowledgeBase).ExecuteCommandAsync(cancellationToken);
        return ToKnowledgeBase(knowledgeBase);
    }

    /// <summary>
    /// 删除知识库及其全部从属数据：资料、切片、分块版本、摘要、索引、向量、笔记、专题、
    /// 问答会话与消息、上传会话、Outbox 事件、插件映射。
    ///
    /// <para>
    /// 顺序固定为「子表 → 父表」。运行时的表结构由 SqlSugar CodeFirst 建立、不带外键，
    /// 但早期按 <c>database/schema.sql</c> 手工建过的库是有外键的，而且
    /// <c>kb_documents.knowledge_base_id</c> 那一条没有 <c>ON DELETE CASCADE</c>——
    /// 先删父行会直接抛 23503。按子表优先的顺序删，两种库都能过。
    /// </para>
    ///
    /// <para>
    /// 只负责数据库，磁盘文件由调用方在事务提交后再删：文件系统不在事务里，
    /// 反过来做会留下「资料还在库里、文件已经没了」的坏状态。
    /// </para>
    /// </summary>
    public async Task<KnowledgeBaseDeletionReport?> DeleteKnowledgeBaseAsync(
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            var knowledgeBase = await database.Queryable<KnowledgeBaseSchemaEntity>()
                .Where(entity => entity.Id == knowledgeBaseId)
                .FirstAsync(cancellationToken);
            if (knowledgeBase is null)
            {
                await database.Ado.RollbackTranAsync();
                return null;
            }

            // 先把要删的主键收集齐：删完父表就查不出子表该删哪些了。
            var documentIds = await database.Queryable<KnowledgeDocumentSchemaEntity>()
                .Where(document => document.KnowledgeBaseId == knowledgeBaseId)
                .Select(document => document.Id)
                .ToListAsync(cancellationToken);
            var indexIds = await database.Queryable<KnowledgeIndexSchemaEntity>()
                .Where(index => index.KnowledgeBaseId == knowledgeBaseId)
                .Select(index => index.Id)
                .ToListAsync(cancellationToken);
            var sessionIds = await database.Queryable<KnowledgeChatSessionSchemaEntity>()
                .Where(session => session.KnowledgeBaseId == knowledgeBaseId)
                .Select(session => session.Id)
                .ToListAsync(cancellationToken);

            var chunkCount = documentIds.Count == 0
                ? 0
                : await database.Queryable<KnowledgeChunkSchemaEntity>()
                    .Where(chunk => documentIds.Contains(chunk.DocumentId))
                    .CountAsync(cancellationToken);
            var noteCount = await database.Queryable<NoteSchemaEntity>()
                .Where(note => note.KnowledgeBaseId == knowledgeBaseId)
                .CountAsync(cancellationToken);
            var topicCount = await database.Queryable<TopicSchemaEntity>()
                .Where(topic => topic.KnowledgeBaseId == knowledgeBaseId)
                .CountAsync(cancellationToken);

            // ── 向量：既按索引删，也按资料删 ──
            // 正常情况下两者等价；但历史数据里可能存在「索引已换、向量行还挂在旧 index_id」的情况，
            // 只按其中一种删会留下孤儿行。
            if (indexIds.Count > 0)
            {
                await database.Deleteable<IndexVectorSchemaEntity>()
                    .Where(vector => indexIds.Contains(vector.IndexId))
                    .ExecuteCommandAsync(cancellationToken);
            }

            if (documentIds.Count > 0)
            {
                await database.Deleteable<IndexVectorSchemaEntity>()
                    .Where(vector => documentIds.Contains(vector.DocumentId))
                    .ExecuteCommandAsync(cancellationToken);
                await database.Deleteable<KnowledgeChunkSchemaEntity>()
                    .Where(chunk => documentIds.Contains(chunk.DocumentId))
                    .ExecuteCommandAsync(cancellationToken);
                await database.Deleteable<DocumentVersionSchemaEntity>()
                    .Where(version => documentIds.Contains(version.DocumentId))
                    .ExecuteCommandAsync(cancellationToken);
                await database.Deleteable<DocumentSummarySchemaEntity>()
                    .Where(summary => documentIds.Contains(summary.DocumentId))
                    .ExecuteCommandAsync(cancellationToken);
            }

            // ── 问答会话：消息先删，否则历史库的级联外键会把会话删除挡下来 ──
            if (sessionIds.Count > 0)
            {
                await database.Deleteable<KnowledgeChatMessageSchemaEntity>()
                    .Where(message => sessionIds.Contains(message.SessionId))
                    .ExecuteCommandAsync(cancellationToken);
            }

            await database.Deleteable<KnowledgeChatSessionSchemaEntity>()
                .Where(session => session.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            // ── 索引生命周期 ──
            await database.Deleteable<KnowledgeIndexSchemaEntity>()
                .Where(index => index.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<ActiveIndexSchemaEntity>()
                .Where(active => active.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<ModelBindingSchemaEntity>()
                .Where(binding => binding.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            // ── 未消费的 Outbox 事件：知识库都没了，再让索引 Worker 去处理只会刷错误日志 ──
            await database.Deleteable<OutboxEventSchemaEntity>()
                .Where(outbox => outbox.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            // ── 上传会话（含未完成的断点续传） ──
            await database.Deleteable<KnowledgeUploadSessionSchemaEntity>()
                .Where(session => session.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            // ── 笔记 / 专题 / 插件映射 ──
            await database.Deleteable<NoteSchemaEntity>()
                .Where(note => note.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<TopicSchemaEntity>()
                .Where(topic => topic.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<PluginPlatformItemSchemaEntity>()
                .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            // ── 资料本体与知识库本体 ──
            await database.Deleteable<KnowledgeDocumentSchemaEntity>()
                .Where(document => document.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<KnowledgeBaseSchemaEntity>()
                .Where(entity => entity.Id == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            await database.Ado.CommitTranAsync();

            return new KnowledgeBaseDeletionReport(
                knowledgeBaseId,
                knowledgeBase.Name,
                documentIds.Count,
                chunkCount,
                noteCount,
                topicCount,
                sessionIds.Count);
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task AddDocumentAsync(KnowledgeDocument document, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await database.Insertable(new KnowledgeDocumentSchemaEntity
        {
            Id = document.Id,
            KnowledgeBaseId = document.KnowledgeBaseId,
            FileName = document.FileName,
            Extension = document.Extension,
            ContentType = document.ContentType,
            SizeBytes = document.SizeBytes,
            StoragePath = document.StoragePath,
            Status = document.Status,
            ProcessingStage = QueuedStage,
            CreatedAt = now,
            UpdatedAt = now
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetDocumentStatusAsync(Guid id, string status, string? error, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Status = status,
                Error = error,
                ProcessingStage = status,
                Progress = SqlFunc.IIF(status == ReadyStatus, 100, document.Progress),
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == id)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateDocumentProgressAsync(Guid id, int progress, string stage, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(progress, 0, 99);
        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Progress = clamped,
                ProcessingStage = stage,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == id && document.Status == ProcessingStatus)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetDocumentSummaryAsync(Guid id, string summary, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                Summary = summary,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == id)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 兼容旧调用：把切片视为一个内容版本发布。分组与向量化已解耦，向量由索引 Worker 生成。
    /// </summary>
    public async Task ReplaceChunksAsync(Guid documentId, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken)
    {
        var document = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.Id == documentId)
            .FirstAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Document {documentId} does not exist.");

        var parts = chunks.Select(chunk => chunk.Content).ToList();
        var contentHash = ComputeSha256(string.Join("\n", parts));
        await PublishChunksAsync(documentId, document.KnowledgeBaseId, null, contentHash, parts, cancellationToken);
    }

    /// <summary>
    /// 在单个事务内发布新的分块版本并写入 Outbox 事件。内容哈希未变化时保持既有版本，避免无意义重建。
    /// </summary>
    public async Task<int> PublishChunksAsync(
        Guid documentId,
        Guid knowledgeBaseId,
        string? fileHash,
        string contentHash,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 0)
            throw new InvalidOperationException("No searchable text could be extracted from the file.");

        await database.Ado.BeginTranAsync();
        try
        {
            var document = await database.Queryable<KnowledgeDocumentSchemaEntity>()
                .Where(item => item.Id == documentId)
                .FirstAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Document {documentId} does not exist.");

            int version;
            if (document.ContentVersion > 0 && string.Equals(document.ContentHash, contentHash, StringComparison.Ordinal))
            {
                // 解析结果没变：保留旧版本，仅刷新状态并确保发布事件存在。
                version = document.ContentVersion;
                var resolvedFileHash = fileHash ?? document.FileHash;
                await database.Updateable<KnowledgeDocumentSchemaEntity>()
                    .SetColumns(item => new KnowledgeDocumentSchemaEntity
                    {
                        FileHash = resolvedFileHash,
                        Status = ReadyStatus,
                        Error = null,
                        ProcessingStage = ReadyStatus,
                        Progress = 100,
                        UpdatedAt = SqlFunc.GetDate()
                    })
                    .Where(item => item.Id == documentId)
                    .ExecuteCommandAsync(cancellationToken);
            }
            else
            {
                version = document.ContentVersion + 1;
                var now = DateTimeOffset.UtcNow;

                await database.Updateable<DocumentVersionSchemaEntity>()
                    .SetColumns(item => new DocumentVersionSchemaEntity { Status = "Superseded" })
                    .Where(item => item.DocumentId == documentId && item.Status == "Published")
                    .ExecuteCommandAsync(cancellationToken);

                await database.Insertable(new DocumentVersionSchemaEntity
                {
                    Id = Guid.NewGuid(),
                    DocumentId = documentId,
                    Version = version,
                    ContentHash = contentHash,
                    ChunkCount = parts.Count,
                    Status = "Published",
                    CreatedAt = now
                }).ExecuteCommandAsync(cancellationToken);

                var rows = parts.Select((content, index) => new KnowledgeChunkSchemaEntity
                {
                    DocumentId = documentId,
                    DocumentVersion = version,
                    ChunkIndex = index,
                    Content = content,
                    ContentHash = ComputeSha256(content),
                    LexicalTokens = LexicalTokens.Build(content),
                    CreatedAt = now
                }).ToList();

                foreach (var batch in rows.Chunk(200))
                {
                    // embedding 是历史兼容列，新切片不再写入向量；
                    // 显式忽略可避开 SqlSugar 批量插入对 InsertSql 的错误拼接。
                    await database.Insertable(batch)
                        .IgnoreColumns(chunk => new { chunk.Embedding })
                        .ExecuteCommandAsync(cancellationToken);
                }

                var resolvedFileHash = fileHash ?? document.FileHash;
                await database.Updateable<KnowledgeDocumentSchemaEntity>()
                    .SetColumns(item => new KnowledgeDocumentSchemaEntity
                    {
                        ContentVersion = version,
                        ContentHash = contentHash,
                        FileHash = resolvedFileHash,
                        Status = ReadyStatus,
                        Error = null,
                        ProcessingStage = ReadyStatus,
                        Progress = 100,
                        UpdatedAt = SqlFunc.GetDate()
                    })
                    .Where(item => item.Id == documentId)
                    .ExecuteCommandAsync(cancellationToken);
            }

            var idempotencyKey = $"document:{documentId}:v{version}:published";
            var eventExists = await database.Queryable<OutboxEventSchemaEntity>()
                .AnyAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (!eventExists)
            {
                await database.Insertable(new OutboxEventSchemaEntity
                {
                    EventType = OutboxEventTypes.DocumentPublished,
                    KnowledgeBaseId = knowledgeBaseId,
                    DocumentId = documentId,
                    DocumentVersion = version,
                    ContentHash = contentHash,
                    IdempotencyKey = idempotencyKey,
                    OccurredAt = DateTimeOffset.UtcNow
                }).ExecuteCommandAsync(cancellationToken);
            }

            await database.Ado.CommitTranAsync();
            return version;
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    internal static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task<IReadOnlyList<DocumentListItem>> ListDocumentsAsync(CancellationToken cancellationToken)
        => await ListDocumentsAsync(DefaultKnowledgeBaseId, cancellationToken);

    public async Task<IReadOnlyList<DocumentListItem>> ListDocumentsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await QueryDocumentsAsync(document => document.KnowledgeBaseId == knowledgeBaseId, cancellationToken);
        return rows.Select(ToDocumentListItem).ToList();
    }

    public async Task<DocumentListItem?> GetDocumentAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await QueryDocumentsAsync(document => document.Id == id, cancellationToken);
        return rows.Count == 0 ? null : ToDocumentListItem(rows[0]);
    }

    private async Task<List<DocumentListRow>> QueryDocumentsAsync(
        System.Linq.Expressions.Expression<Func<KnowledgeDocumentSchemaEntity, bool>> predicate,
        CancellationToken cancellationToken) =>
        await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(predicate)
            .OrderBy(document => document.CreatedAt, OrderByType.Desc)
            .Select(document => new DocumentListRow
            {
                Id = document.Id,
                FileName = document.FileName,
                Extension = document.Extension,
                ContentType = document.ContentType,
                SizeBytes = document.SizeBytes,
                Status = document.Status,
                Error = document.Error,
                CreatedAt = document.CreatedAt,
                TopicId = document.TopicId,
                Summary = document.Summary,
                Progress = document.Progress,
                ProcessingStage = document.ProcessingStage,
                ChunkCount = SqlFunc.Subqueryable<KnowledgeChunkSchemaEntity>()
                    .Where(chunk => chunk.DocumentId == document.Id && chunk.DocumentVersion == document.ContentVersion)
                    .Count()
            })
            .ToListAsync(cancellationToken);

    public async Task<int> BackfillLegacyVersionsAsync(CancellationToken cancellationToken)
    {
        // 升级前的资料没有版本号，也没有 Outbox 事件。这里一次性把它们提升为版本 1，
        // 让新索引生命周期能够识别并接管已有内容。
        var documents = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.Status == ReadyStatus && item.ContentVersion == 0)
            .ToListAsync(cancellationToken);

        var migrated = 0;
        foreach (var document in documents)
        {
            // 必须显式投影：kb_chunks.embedding 是 pgvector 的 vector 类型，
            // Npgsql 无法把它读成 object/string，整行读取会抛 InvalidCastException。
            var chunks = await database.Queryable<KnowledgeChunkSchemaEntity>()
                .Where(item => item.DocumentId == document.Id)
                .OrderBy(item => item.ChunkIndex, OrderByType.Asc)
                .Select(item => new LegacyChunkRow
                {
                    Id = item.Id,
                    ChunkIndex = item.ChunkIndex,
                    Content = item.Content,
                    ContentHash = item.ContentHash
                })
                .ToListAsync(cancellationToken);
            if (chunks.Count == 0) continue;

            var contentHash = ComputeSha256(string.Join("\n", chunks.Select(chunk => chunk.Content)));
            await database.Ado.BeginTranAsync();
            try
            {
                foreach (var chunk in chunks)
                {
                    var chunkHash = chunk.ContentHash ?? ComputeSha256(chunk.Content);
                    await database.Updateable<KnowledgeChunkSchemaEntity>()
                        .SetColumns(item => new KnowledgeChunkSchemaEntity
                        {
                            DocumentVersion = 1,
                            ContentHash = chunkHash
                        })
                        .Where(item => item.Id == chunk.Id)
                        .ExecuteCommandAsync(cancellationToken);
                }

                var versionExists = await database.Queryable<DocumentVersionSchemaEntity>()
                    .AnyAsync(item => item.DocumentId == document.Id && item.Version == 1, cancellationToken);
                if (!versionExists)
                {
                    await database.Insertable(new DocumentVersionSchemaEntity
                    {
                        Id = Guid.NewGuid(),
                        DocumentId = document.Id,
                        Version = 1,
                        ContentHash = contentHash,
                        ChunkCount = chunks.Count,
                        Status = "Published",
                        CreatedAt = DateTimeOffset.UtcNow
                    }).ExecuteCommandAsync(cancellationToken);
                }

                await database.Updateable<KnowledgeDocumentSchemaEntity>()
                    .SetColumns(item => new KnowledgeDocumentSchemaEntity
                    {
                        ContentVersion = 1,
                        ContentHash = contentHash,
                        UpdatedAt = SqlFunc.GetDate()
                    })
                    .Where(item => item.Id == document.Id && item.ContentVersion == 0)
                    .ExecuteCommandAsync(cancellationToken);

                var idempotencyKey = $"document:{document.Id}:v1:published";
                var eventExists = await database.Queryable<OutboxEventSchemaEntity>()
                    .AnyAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
                if (!eventExists)
                {
                    await database.Insertable(new OutboxEventSchemaEntity
                    {
                        EventType = OutboxEventTypes.DocumentPublished,
                        KnowledgeBaseId = document.KnowledgeBaseId,
                        DocumentId = document.Id,
                        DocumentVersion = 1,
                        ContentHash = contentHash,
                        IdempotencyKey = idempotencyKey,
                        OccurredAt = DateTimeOffset.UtcNow
                    }).ExecuteCommandAsync(cancellationToken);
                }

                await database.Ado.CommitTranAsync();
                migrated++;
            }
            catch
            {
                await database.Ado.RollbackTranAsync();
                throw;
            }
        }

        return migrated;
    }

    public async Task<IReadOnlyList<Guid>> ListKnowledgeBaseIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await database.Queryable<KnowledgeBaseSchemaEntity>()
            .OrderBy(item => item.CreatedAt, OrderByType.Asc)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        return ids;
    }

    public async Task<IReadOnlyList<IndexSourceDocument>> ListPublishedDocumentsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId
                && item.Status == ReadyStatus
                && item.ContentVersion > 0)
            .OrderBy(item => item.CreatedAt, OrderByType.Asc)
            .Select(item => new IndexSourceDocumentRow
            {
                DocumentId = item.Id,
                KnowledgeBaseId = item.KnowledgeBaseId,
                FileName = item.FileName,
                Extension = item.Extension,
                ContentVersion = item.ContentVersion,
                ContentHash = item.ContentHash,
                Status = item.Status
            })
            .ToListAsync(cancellationToken);
        return rows.Select(ToIndexSourceDocument).ToList();
    }

    public async Task<IndexSourceDocument?> GetPublishedDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.Id == documentId && item.ContentVersion > 0)
            .Select(item => new IndexSourceDocumentRow
            {
                DocumentId = item.Id,
                KnowledgeBaseId = item.KnowledgeBaseId,
                FileName = item.FileName,
                Extension = item.Extension,
                ContentVersion = item.ContentVersion,
                ContentHash = item.ContentHash,
                Status = item.Status
            })
            .FirstAsync(cancellationToken);
        return row is null ? null : ToIndexSourceDocument(row);
    }

    public async Task<IReadOnlyList<IndexSourceChunk>> GetPublishedChunksAsync(Guid documentId, CancellationToken cancellationToken)
    {
        // 标量 FirstAsync 在空结果集上会抛 NullReferenceException，这里统一用列表读取。
        var versions = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.Id == documentId)
            .Select(item => item.ContentVersion)
            .Take(1)
            .ToListAsync(cancellationToken);
        var version = versions.Count == 0 ? 0 : versions[0];
        if (version <= 0) return [];

        var rows = await database.Queryable<KnowledgeChunkSchemaEntity>()
            .Where(item => item.DocumentId == documentId && item.DocumentVersion == version)
            .OrderBy(item => item.ChunkIndex, OrderByType.Asc)
            .Select(item => new IndexSourceChunkRow
            {
                ChunkId = item.Id,
                DocumentId = item.DocumentId,
                DocumentVersion = item.DocumentVersion,
                ChunkIndex = item.ChunkIndex,
                Content = item.Content,
                ContentHash = item.ContentHash
            })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new IndexSourceChunk(row.ChunkId, row.DocumentId, row.DocumentVersion,
            row.ChunkIndex, row.Content, row.ContentHash)).ToList();
    }

    public async Task<int> CountPublishedChunksAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var versions = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.Id == documentId)
            .Select(item => item.ContentVersion)
            .Take(1)
            .ToListAsync(cancellationToken);
        var version = versions.Count == 0 ? 0 : versions[0];
        if (version <= 0) return 0;

        return await database.Queryable<KnowledgeChunkSchemaEntity>()
            .Where(item => item.DocumentId == documentId && item.DocumentVersion == version)
            .CountAsync(cancellationToken);
    }

    private static IndexSourceDocument ToIndexSourceDocument(IndexSourceDocumentRow row) =>
        new(row.DocumentId, row.KnowledgeBaseId, row.FileName, row.Extension, row.ContentVersion, row.ContentHash, row.Status);

    private static KnowledgeBaseInfo ToKnowledgeBase(KnowledgeBaseSchemaEntity entity) =>
        new(entity.Id, entity.Name, entity.CreatedAt, entity.Id == DefaultKnowledgeBaseId);

    private static UploadSession ToUploadSession(KnowledgeUploadSessionSchemaEntity entity) =>
        new(entity.Id, entity.KnowledgeBaseId, entity.FileName, entity.Extension, entity.ContentType, entity.SizeBytes,
            entity.UploadedBytes, entity.StagingPath, entity.Status, entity.DocumentId);

    private static DocumentListItem ToDocumentListItem(DocumentListRow row) =>
        new(row.Id, row.FileName, row.Extension, row.ContentType, row.SizeBytes,
            row.Status, row.Error, row.CreatedAt, row.ChunkCount, row.Summary, row.Progress, row.ProcessingStage, row.TopicId);
}

/// <summary>排除 embedding 列的轻量投影行，用于历史资料回填。</summary>
internal sealed class LegacyChunkRow
{
    public LegacyChunkRow() { }

    public long Id { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
}

internal sealed class IndexSourceDocumentRow
{
    public IndexSourceDocumentRow() { }

    public Guid DocumentId { get; set; }
    public Guid KnowledgeBaseId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public int ContentVersion { get; set; }
    public string? ContentHash { get; set; }
    public string Status { get; set; } = string.Empty;
}

internal sealed class IndexSourceChunkRow
{
    public IndexSourceChunkRow() { }

    public long ChunkId { get; set; }
    public Guid DocumentId { get; set; }
    public int DocumentVersion { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
}

/// <summary>待补分词的切片行（只投影 id 与 content，避开向量列）。</summary>
internal sealed class LexicalBackfillRow
{
    public LexicalBackfillRow() { }

    public long Id { get; set; }
    public string Content { get; set; } = string.Empty;
}

internal sealed class DocumentListRow
{
    public DocumentListRow() { }

    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? TopicId { get; set; }
    public int ChunkCount { get; set; }
    public string? Summary { get; set; }
    public int Progress { get; set; }
    public string? ProcessingStage { get; set; }
}

public sealed record KnowledgeDocument(Guid Id, Guid KnowledgeBaseId, string FileName, string Extension, string ContentType, long SizeBytes, string StoragePath, string Status);

/// <param name="IsDefault">
/// 是否是默认知识库。界面据此隐藏「删除」入口——只靠接口返回 409 提示太晚，
/// 用户点下去才发现不能删是糟糕的交互。
/// </param>
public sealed record KnowledgeBaseInfo(Guid Id, string Name, DateTimeOffset CreatedAt, bool IsDefault = false);
public sealed record KnowledgeChunk(int Index, string Content, float[] Embedding);
public sealed record UploadSession(Guid Id, Guid KnowledgeBaseId, string FileName, string Extension, string ContentType,
    long SizeBytes, long UploadedBytes, string StagingPath, string Status, Guid? DocumentId);
public sealed record QueuedDocument(Guid Id, Guid KnowledgeBaseId, string FileName, string Extension, string ContentType,
    long SizeBytes, string StoragePath);

/// <summary>排队资料的补投信息：<c>UpdatedAt</c> 用来判断它是不是“任务结束之后又被重新排队”的。</summary>
public sealed record QueuedDocumentEntry(Guid DocumentId, Guid KnowledgeBaseId, DateTimeOffset UpdatedAt);
public sealed record DocumentListItem(Guid Id, string FileName, string Extension, string ContentType, long SizeBytes,
    string Status, string? Error, DateTimeOffset CreatedAt, long ChunkCount, string? Summary, int Progress, string? ProcessingStage,
    Guid? TopicId = null);
