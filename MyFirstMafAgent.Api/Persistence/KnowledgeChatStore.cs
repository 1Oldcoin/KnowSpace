using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

public sealed class KnowledgeChatStore(SqlSugarScope database)
{
    private const string DefaultSessionTitle = "新的对话";
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<KnowledgeChatSessionSchemaEntity>();
        database.CodeFirst.InitTables<KnowledgeChatMessageSchemaEntity>();
        EnsureMessageColumns();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 引用元数据是后加的列：CodeFirst 只在建表时生效，已存在的旧库不会自动补列，
    /// 因此像 kb_documents 那样显式检查并补列。
    /// </summary>
    private void EnsureMessageColumns()
    {
        if (!database.DbMaintenance.IsAnyTable("knowledge_chat_messages", false)) return;

        EnsureMessageColumn("citations", "text", true, null);
        EnsureMessageColumn("answer_meta", "text", true, null);
    }

    private void EnsureMessageColumn(string columnName, string dataType, bool isNullable, string? defaultValue)
    {
        if (database.DbMaintenance.IsAnyColumn("knowledge_chat_messages", columnName, false)) return;

        database.DbMaintenance.AddColumn("knowledge_chat_messages", new DbColumnInfo
        {
            DbColumnName = columnName,
            DataType = dataType,
            IsNullable = isNullable,
            DefaultValue = defaultValue
        });
    }

    public async Task<KnowledgeChatSession> CreateSessionAsync(Guid knowledgeBaseId, string title, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new KnowledgeChatSession(Guid.NewGuid(), knowledgeBaseId, title, now, now);
        await database.Insertable(new KnowledgeChatSessionSchemaEntity
        {
            Id = session.Id,
            KnowledgeBaseId = knowledgeBaseId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now
        }).ExecuteCommandAsync(cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<KnowledgeChatSession>> ListSessionsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<KnowledgeChatSessionSchemaEntity>()
            .Where(session => session.KnowledgeBaseId == knowledgeBaseId)
            .OrderBy(session => session.UpdatedAt, OrderByType.Desc)
            .OrderBy(session => session.Id, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToSession).ToList();
    }

    public async Task<KnowledgeChatSession?> GetSessionAsync(Guid sessionId, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<KnowledgeChatSessionSchemaEntity>()
            .Where(session => session.Id == sessionId && session.KnowledgeBaseId == knowledgeBaseId)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToSession(row);
    }

    public async Task<bool> RenameSessionAsync(Guid sessionId, Guid knowledgeBaseId, string title, CancellationToken cancellationToken)
    {
        var updated = await database.Updateable<KnowledgeChatSessionSchemaEntity>()
            .SetColumns(session => new KnowledgeChatSessionSchemaEntity
            {
                Title = title,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(session => session.Id == sessionId && session.KnowledgeBaseId == knowledgeBaseId)
            .ExecuteCommandAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<bool> DeleteSessionAsync(Guid sessionId, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            var deleted = await database.Deleteable<KnowledgeChatSessionSchemaEntity>()
                .Where(session => session.Id == sessionId && session.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            // 会话消息原本依赖外键级联删除，这里显式清理。
            if (deleted > 0)
                await database.Deleteable<KnowledgeChatMessageSchemaEntity>()
                    .Where(message => message.SessionId == sessionId)
                    .ExecuteCommandAsync(cancellationToken);

            await database.Ado.CommitTranAsync();
            return deleted > 0;
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<int> DeleteSessionsAsync(Guid knowledgeBaseId, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken)
    {
        var ids = sessionIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return 0;

        await database.Ado.BeginTranAsync();
        try
        {
            var ownedIds = await database.Queryable<KnowledgeChatSessionSchemaEntity>()
                .Where(session => session.KnowledgeBaseId == knowledgeBaseId && ids.Contains(session.Id))
                .Select(session => session.Id)
                .ToListAsync(cancellationToken);

            var deleted = await database.Deleteable<KnowledgeChatSessionSchemaEntity>()
                .Where(session => session.KnowledgeBaseId == knowledgeBaseId && ownedIds.Contains(session.Id))
                .ExecuteCommandAsync(cancellationToken);

            if (ownedIds.Count > 0)
                await database.Deleteable<KnowledgeChatMessageSchemaEntity>()
                    .Where(message => ownedIds.Contains(message.SessionId))
                    .ExecuteCommandAsync(cancellationToken);

            await database.Ado.CommitTranAsync();
            return deleted;
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<bool> SessionBelongsToKnowledgeBaseAsync(Guid sessionId, Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        await database.Queryable<KnowledgeChatSessionSchemaEntity>()
            .AnyAsync(session => session.Id == sessionId && session.KnowledgeBaseId == knowledgeBaseId, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeChatMessage>> ListSessionMessagesAsync(Guid sessionId, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await QuerySessionMessagesAsync(sessionId, knowledgeBaseId, null, cancellationToken);
        return rows.Select(ToMessage).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeChatMessage>> ListRecentSessionMessagesAsync(Guid sessionId, Guid knowledgeBaseId, int limit, CancellationToken cancellationToken)
    {
        var rows = await QuerySessionMessagesAsync(sessionId, knowledgeBaseId, Math.Max(1, limit), cancellationToken);
        // 先取最近若干条，再按时间正序返回。
        return rows.OrderBy(row => row.Id).Select(ToMessage).ToList();
    }

    public Task AddSessionExchangeAsync(Guid sessionId, Guid knowledgeBaseId, string userMessage, string assistantMessage, CancellationToken cancellationToken) =>
        AddSessionExchangeAsync(sessionId, knowledgeBaseId, userMessage, assistantMessage, null, null, cancellationToken);

    /// <summary>
    /// 写入一轮问答。助手消息带上引用与答案元数据落库，历史会话重开时才能还原“答到了哪几段资料”。
    /// </summary>
    public async Task AddSessionExchangeAsync(
        Guid sessionId,
        Guid knowledgeBaseId,
        string userMessage,
        string assistantMessage,
        IReadOnlyList<KnowledgeChatCitation>? citations,
        KnowledgeChatAnswerMeta? answerMeta,
        CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            var title = userMessage.Trim();
            if (title.Length > 100) title = title[..100];
            var updated = await database.Updateable<KnowledgeChatSessionSchemaEntity>()
                .SetColumns(session => new KnowledgeChatSessionSchemaEntity
                {
                    Title = SqlFunc.IIF(session.Title == DefaultSessionTitle, title, session.Title),
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(session => session.Id == sessionId && session.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            if (updated == 0) throw new InvalidOperationException("The selected knowledge chat session does not exist.");

            var now = DateTimeOffset.UtcNow;
            await database.Insertable(new List<KnowledgeChatMessageSchemaEntity>
            {
                new() { SessionId = sessionId, Role = UserRole, Content = userMessage, CreatedAt = now },
                new()
                {
                    SessionId = sessionId,
                    Role = AssistantRole,
                    Content = assistantMessage,
                    Citations = KnowledgeChatJson.SerializeCitations(citations),
                    AnswerMeta = KnowledgeChatJson.SerializeMeta(answerMeta),
                    CreatedAt = now
                }
            }).ExecuteCommandAsync(cancellationToken);

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    private async Task<List<KnowledgeChatMessageRow>> QuerySessionMessagesAsync(
        Guid sessionId,
        Guid knowledgeBaseId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var query = database.Queryable<KnowledgeChatMessageSchemaEntity, KnowledgeChatSessionSchemaEntity>(
                (message, session) => message.SessionId == session.Id)
            .Where((message, session) => message.SessionId == sessionId && session.KnowledgeBaseId == knowledgeBaseId);

        if (limit is not null)
            query = query.OrderBy((message, session) => message.Id, OrderByType.Desc).Take(limit.Value);
        else
            query = query.OrderBy((message, session) => message.Id, OrderByType.Asc);

        return await query
            .Select((message, session) => new KnowledgeChatMessageRow
            {
                Id = message.Id,
                Role = message.Role,
                Content = message.Content,
                Citations = message.Citations,
                AnswerMeta = message.AnswerMeta,
                CreatedAt = message.CreatedAt
            })
            .ToListAsync(cancellationToken);
    }

    private static KnowledgeChatSession ToSession(KnowledgeChatSessionSchemaEntity entity) =>
        new(entity.Id, entity.KnowledgeBaseId, entity.Title, entity.CreatedAt, entity.UpdatedAt);

    private static KnowledgeChatMessage ToMessage(KnowledgeChatMessageRow row) =>
        new(row.Id, row.Role, row.Content, row.CreatedAt,
            KnowledgeChatJson.DeserializeCitations(row.Citations),
            KnowledgeChatJson.DeserializeMeta(row.AnswerMeta));
}

internal sealed class KnowledgeChatMessageRow
{
    public KnowledgeChatMessageRow() { }

    public long Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Citations { get; set; }
    public string? AnswerMeta { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record KnowledgeChatSession(Guid Id, Guid KnowledgeBaseId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// 一条问答消息。<paramref name="Citations"/> 与 <paramref name="AnswerMeta"/> 只对助手消息有值，
/// 旧数据与用户消息为空集合 / null。
/// </summary>
public sealed record KnowledgeChatMessage(
    long Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    IReadOnlyList<KnowledgeChatCitation>? Citations = null,
    KnowledgeChatAnswerMeta? AnswerMeta = null);
