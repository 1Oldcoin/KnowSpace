using System.Linq.Expressions;
using MyFirstMafAgent.Api.Companions;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

public sealed class CompanionStore(SqlSugarScope database)
{
    private const int RecentSessionMessageLimit = 30;
    private const int InsertBatchSize = 500;

    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<CompanionProfileSchemaEntity>();
        database.CodeFirst.InitTables<CompanionMessageSchemaEntity>();
        database.CodeFirst.InitTables<CompanionSessionSchemaEntity>();
        database.CodeFirst.InitTables<CompanionSessionMessageSchemaEntity>();
        // 独立向量表：伴侣历史不进知识库索引，否则两个人的私聊会被当成"资料"搜出来。
        database.CodeFirst.InitTables<CompanionMessageVectorSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<CompanionProfile> ImportAsync(string name, string targetSender, string platform, IReadOnlyList<ImportedChatMessage> messages, CancellationToken cancellationToken)
    {
        var profileId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        await database.Ado.BeginTranAsync();
        try
        {
            await database.Insertable(new CompanionProfileSchemaEntity
            {
                Id = profileId,
                Name = name,
                TargetSender = targetSender,
                Platform = platform,
                PersonaPrompt = string.Empty,
                CreatedAt = createdAt
            }).ExecuteCommandAsync(cancellationToken);

            foreach (var batch in messages.Chunk(InsertBatchSize))
            {
                var entities = batch.Select(message => new CompanionMessageSchemaEntity
                {
                    ProfileId = profileId,
                    Sender = message.Sender,
                    Content = message.Content,
                    SentAt = message.SentAt,
                    CreatedAt = createdAt
                }).ToList();
                await database.Insertable(entities).ExecuteCommandAsync(cancellationToken);
            }

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }

        return new CompanionProfile(profileId, name, targetSender, platform, string.Empty, messages.Count, createdAt);
    }

    public async Task<IReadOnlyList<CompanionProfile>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<CompanionProfileSchemaEntity>()
            .LeftJoin<CompanionMessageSchemaEntity>((profile, message) => profile.Id == message.ProfileId)
            .GroupBy((profile, message) => new
            {
                profile.Id,
                profile.Name,
                profile.TargetSender,
                profile.Platform,
                profile.PersonaPrompt,
                profile.CreatedAt
            })
            .OrderBy((profile, message) => profile.CreatedAt, OrderByType.Desc)
            .Select((profile, message) => new CompanionProfileRow
            {
                Id = profile.Id,
                Name = profile.Name,
                TargetSender = profile.TargetSender,
                Platform = profile.Platform,
                PersonaPrompt = profile.PersonaPrompt,
                CreatedAt = profile.CreatedAt,
                MessageCount = SqlFunc.AggregateCount(message.Id)
            })
            .ToListAsync(cancellationToken);
        return rows.Select(ToProfile).ToList();
    }

    public async Task<CompanionProfile?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<CompanionProfileSchemaEntity>()
            .Where(profile => profile.Id == profileId)
            .Select(profile => new CompanionProfileRow
            {
                Id = profile.Id,
                Name = profile.Name,
                TargetSender = profile.TargetSender,
                Platform = profile.Platform,
                PersonaPrompt = profile.PersonaPrompt,
                CreatedAt = profile.CreatedAt,
                MessageCount = SqlFunc.Subqueryable<CompanionMessageSchemaEntity>()
                    .Where(message => message.ProfileId == profile.Id)
                    .Count()
            })
            .FirstAsync(cancellationToken);
        return row is null ? null : ToProfile(row);
    }

    public async Task<bool> SetPersonaAsync(Guid profileId, string personaPrompt, CancellationToken cancellationToken)
    {
        var updated = await database.Updateable<CompanionProfileSchemaEntity>()
            .SetColumns(profile => new CompanionProfileSchemaEntity { PersonaPrompt = personaPrompt })
            .Where(profile => profile.Id == profileId)
            .ExecuteCommandAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            var sessionIds = await database.Queryable<CompanionSessionSchemaEntity>()
                .Where(session => session.ProfileId == profileId)
                .Select(session => session.Id)
                .ToListAsync(cancellationToken);

            // 会话与消息原本依赖外键级联删除，这里显式清理。
            if (sessionIds.Count > 0)
                await database.Deleteable<CompanionSessionMessageSchemaEntity>()
                    .Where(message => sessionIds.Contains(message.SessionId))
                    .ExecuteCommandAsync(cancellationToken);

            await database.Deleteable<CompanionSessionSchemaEntity>()
                .Where(session => session.ProfileId == profileId)
                .ExecuteCommandAsync(cancellationToken);

            await database.Deleteable<CompanionMessageSchemaEntity>()
                .Where(message => message.ProfileId == profileId)
                .ExecuteCommandAsync(cancellationToken);

            var deleted = await database.Deleteable<CompanionProfileSchemaEntity>()
                .Where(profile => profile.Id == profileId)
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

    public async Task<CompanionSession> CreateSessionAsync(Guid profileId, string title, CancellationToken cancellationToken)
    {
        var session = new CompanionSession(Guid.NewGuid(), profileId, title, DateTimeOffset.UtcNow);
        await database.Insertable(new CompanionSessionSchemaEntity
        {
            Id = session.Id,
            ProfileId = profileId,
            Title = title,
            CreatedAt = session.CreatedAt
        }).ExecuteCommandAsync(cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<CompanionSession>> ListSessionsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<CompanionSessionSchemaEntity>()
            .Where(session => session.ProfileId == profileId)
            .OrderBy(session => session.CreatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new CompanionSession(row.Id, row.ProfileId, row.Title, row.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<CompanionChatMessage>> ListSessionMessagesAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<CompanionSessionMessageSchemaEntity>()
            .Where(message => message.SessionId == sessionId)
            .OrderBy(message => message.Id, OrderByType.Desc)
            .Take(RecentSessionMessageLimit)
            .Select(message => new CompanionChatMessageRow
            {
                Id = message.Id,
                Role = message.Role,
                Content = message.Content,
                CreatedAt = message.CreatedAt
            })
            .ToListAsync(cancellationToken);
        // 先取最近若干条，再按时间正序返回。
        return rows.OrderBy(row => row.Id)
            .Select(row => new CompanionChatMessage(row.Id, row.Role, row.Content, row.CreatedAt))
            .ToList();
    }

    public async Task<bool> SessionBelongsToProfileAsync(Guid sessionId, Guid profileId, CancellationToken cancellationToken) =>
        await database.Queryable<CompanionSessionSchemaEntity>()
            .AnyAsync(session => session.Id == sessionId && session.ProfileId == profileId, cancellationToken);

    public async Task AddSessionExchangeAsync(Guid sessionId, string userMessage, string assistantMessage, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await database.Insertable(new List<CompanionSessionMessageSchemaEntity>
        {
            new() { SessionId = sessionId, Role = "user", Content = userMessage, CreatedAt = now },
            new() { SessionId = sessionId, Role = "assistant", Content = assistantMessage, CreatedAt = now }
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImportedChatMessage>> FindRelevantMessagesAsync(Guid profileId, string targetSender, string query, int limit, CancellationToken cancellationToken)
    {
        var rows = await FindLexicalMessagesAsync(profileId, targetSender, query, limit, cancellationToken);
        return rows.Select(row => new ImportedChatMessage(row.Sender, row.Content, row.SentAt)).ToList();
    }

    /// <summary>词面（子串）检索，带上消息 ID —— 与向量结果做 RRF 融合时需要一个稳定的标识。</summary>
    public async Task<IReadOnlyList<CompanionRecall>> FindLexicalMessagesAsync(
        Guid profileId,
        string targetSender,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var terms = CreateSearchTerms(query);
        var messages = database.Queryable<CompanionMessageSchemaEntity>()
            .Where(message => message.ProfileId == profileId && message.Sender == targetSender);

        if (terms.Length > 0)
            messages = messages.Where(CreateContentMatch(terms));

        var rows = await messages
            .OrderBy(message => SqlFunc.IsNull(message.SentAt, DateTimeOffset.MaxValue), OrderByType.Desc)
            .OrderBy(message => message.Id, OrderByType.Desc)
            .Take(limit)
            .Select(message => new CompanionRecallRow
            {
                MessageId = message.Id,
                Sender = message.Sender,
                Content = message.Content,
                SentAt = message.SentAt
            })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new CompanionRecall(row.MessageId, row.Sender, row.Content, row.SentAt)).ToList();
    }

    // ─────────────── 向量检索（伴侣历史的独立索引） ─��─────────────

    /// <summary>当前向量模型下已索引的消息数，用于界面显示“要不要重建”。</summary>
    public async Task<int> CountVectorsAsync(Guid profileId, string fingerprint, CancellationToken cancellationToken)
    {
        var sql = """
            SELECT COUNT(*)::int4 AS "Value"
            FROM companion_message_vectors AS v
            JOIN companion_messages AS m ON m.id = v.message_id
            WHERE m.profile_id = @profileId AND v.embedding_fingerprint = @fingerprint
            """;
        var rows = await database.Ado.SqlQueryAsync<ScalarRow>(sql,
            new { profileId, fingerprint }, cancellationToken);
        return rows.Count == 0 ? 0 : rows[0].Value;
    }

    /// <summary>
    /// 取尚未向量化的消息（按时间倒序，优先给最近的）。
    /// <para>
    /// 只看 <paramref name="sender"/>：模拟对话只用对方的话作为记忆，把用户自己的话也建索引会污染“TA 说过什么”。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<CompanionMessageVectorSource>> ListMessagesForVectorAsync(
        Guid profileId,
        string sender,
        string fingerprint,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT m.id      AS "MessageId",
                   m.content AS "Content"
            FROM companion_messages AS m
            WHERE m.profile_id = @profileId
              AND m.sender = @sender
              AND char_length(m.content) >= @minLength
              AND NOT EXISTS (
                  SELECT 1 FROM companion_message_vectors AS v
                  WHERE v.message_id = m.id AND v.embedding_fingerprint = @fingerprint)
            ORDER BY m.sent_at DESC NULLS LAST, m.id DESC
            LIMIT @limit
            """;
        var rows = await database.Ado.SqlQueryAsync<CompanionMessageVectorSourceRow>(sql, new
        {
            profileId,
            sender,
            fingerprint,
            minLength = 4,
            limit = Math.Clamp(limit, 1, 5000)
        }, cancellationToken);
        return rows.Select(row => new CompanionMessageVectorSource(row.MessageId, row.Content)).ToList();
    }

    public async Task UpsertMessageVectorsAsync(
        IReadOnlyList<CompanionMessageVector> vectors,
        CancellationToken cancellationToken)
    {
        if (vectors.Count == 0) return;

        await database.Ado.BeginTranAsync();
        try
        {
            var ids = vectors.Select(vector => vector.MessageId).ToList();
            for (var offset = 0; offset < ids.Count; offset += 500)
            {
                var batch = ids.Skip(offset).Take(500).ToList();
                await database.Deleteable<CompanionMessageVectorSchemaEntity>()
                    .Where(entity => batch.Contains(entity.MessageId))
                    .ExecuteCommandAsync(cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var vector in vectors)
            {
                await database.Insertable(new CompanionMessageVectorSchemaEntity
                {
                    MessageId = vector.MessageId,
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

    /// <summary>删除某个画像的全部向量（重新导入 / 换模型时用）。</summary>
    public async Task<int> ClearVectorsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var sql = """
            DELETE FROM companion_message_vectors AS v
            USING companion_messages AS m
            WHERE m.id = v.message_id AND m.profile_id = @profileId
            """;
        return await database.Ado.ExecuteCommandAsync(sql, new { profileId }, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanionMessageVectorMatch>> SearchMessageVectorsAsync(
        Guid profileId,
        string sender,
        float[] embedding,
        string fingerprint,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = PgVectorSql.BuildCompanionMessageSearchSql(PgVectorSql.ToVectorLiteral(embedding));
        var rows = await database.Ado.SqlQueryAsync<CompanionMessageVectorMatchRow>(sql, new
        {
            fingerprint,
            profileId,
            sender,
            limit = Math.Clamp(limit, 1, 100)
        }, cancellationToken);
        return rows.Select(row => new CompanionMessageVectorMatch(
            row.MessageId, row.Sender, row.Content, row.SentAt, row.Similarity)).ToList();
    }

    private static CompanionProfile ToProfile(CompanionProfileRow profile) =>
        new(profile.Id, profile.Name, profile.TargetSender, profile.Platform, profile.PersonaPrompt, profile.MessageCount, profile.CreatedAt);

    private static Expression<Func<CompanionMessageSchemaEntity, bool>> CreateContentMatch(IReadOnlyList<string> terms)
    {
        var message = Expression.Parameter(typeof(CompanionMessageSchemaEntity), "message");
        var containsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var toLowerMethod = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

        Expression? match = null;
        foreach (var term in terms)
        {
            // LOWER(content) LIKE '%term%' 等价于不可移植的 ILIKE 匹配。
            var lowered = Expression.Call(
                Expression.Property(message, nameof(CompanionMessageSchemaEntity.Content)),
                toLowerMethod);
            var contains = Expression.Call(lowered, containsMethod, Expression.Constant(term.ToLowerInvariant()));
            match = match is null ? contains : Expression.OrElse(match, contains);
        }

        return Expression.Lambda<Func<CompanionMessageSchemaEntity, bool>>(match!, message);
    }

    private static string[] CreateSearchTerms(string query)
    {
        var words = query.Split([' ', '\t', '\r', '\n', ',', '，', '.', '。', '？', '?', '！', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 2);
        var candidates = words.SelectMany(word => ContainsChinese(word) && word.Length > 4
            ? Enumerable.Range(0, word.Length - 1).Select(index => word.Substring(index, 2))
            : [word]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length <= 8) return candidates;
        return Enumerable.Range(0, 8).Select(index => candidates[index * (candidates.Length - 1) / 7]).ToArray();
    }

    private static bool ContainsChinese(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9fff');
}

internal sealed class CompanionProfileRow
{
    public CompanionProfileRow() { }

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetSender { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string PersonaPrompt { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int MessageCount { get; set; }
}

internal sealed class CompanionChatMessageRow
{
    public CompanionChatMessageRow() { }

    public long Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ImportedChatMessageRow
{
    public ImportedChatMessageRow() { }

    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>一条待向量化的消息（只带 id 与正文，不读整行）。</summary>
public sealed record CompanionMessageVectorSource(long MessageId, string Content);

/// <summary>召回的一条历史消息（词面或向量）。</summary>
public sealed record CompanionRecall(long MessageId, string Sender, string Content, DateTimeOffset? SentAt);

internal sealed class CompanionRecallRow
{
    public CompanionRecallRow() { }

    public long MessageId { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>一条待写入的消息向量。</summary>
public sealed record CompanionMessageVector(
    long MessageId,
    Guid EmbeddingSpecId,
    string SpecFingerprint,
    int Dimensions,
    float[] Embedding);

/// <summary>向量检索命中的一条历史消息。</summary>
public sealed record CompanionMessageVectorMatch(
    long MessageId,
    string Sender,
    string Content,
    DateTimeOffset? SentAt,
    double Similarity);

internal sealed class CompanionMessageVectorSourceRow
{
    public CompanionMessageVectorSourceRow() { }

    public long MessageId { get; set; }
    public string Content { get; set; } = string.Empty;
}

internal sealed class CompanionMessageVectorMatchRow
{
    public CompanionMessageVectorMatchRow() { }

    public long MessageId { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset? SentAt { get; set; }
    public double Similarity { get; set; }
}

/// <summary>Ado 单值查询的承载行。</summary>
internal sealed class ScalarRow
{
    public ScalarRow() { }

    public int Value { get; set; }
}
