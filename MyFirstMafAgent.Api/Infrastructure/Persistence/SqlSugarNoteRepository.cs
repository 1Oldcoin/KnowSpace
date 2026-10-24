using System.Text.Json;
using MyFirstMafAgent.Api.Domain.Notes;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

[SugarTable("kb_notes")]
[SugarIndex("ix_kb_notes_knowledge_base_updated", nameof(KnowledgeBaseId), OrderByType.Asc, nameof(UpdatedAt), OrderByType.Desc, false)]
public sealed class NoteSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "title", ColumnDataType = "text", IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "category", ColumnDataType = "text", IsNullable = false)]
    public string Category { get; set; } = NoteDefaults.Category;

    [SugarColumn(ColumnName = "color", ColumnDataType = "text", IsNullable = false)]
    public string Color { get; set; } = NoteDefaults.Color;

    /// <summary>标签以 JSON 数组文本保存，避免依赖 PostgreSQL 数组类型映射。</summary>
    [SugarColumn(ColumnName = "tags", ColumnDataType = "text", IsNullable = false, DefaultValue = "'[]'")]
    public string Tags { get; set; } = "[]";

    [SugarColumn(ColumnName = "is_pinned", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "false")]
    public bool IsPinned { get; set; }

    [SugarColumn(ColumnName = "source_document_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? SourceDocumentId { get; set; }

    /// <summary>笔记归属的专题。</summary>
    [SugarColumn(ColumnName = "topic_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? TopicId { get; set; }

    /// <summary>引用资料片段（JSON 数组文本），与 tags 一致地用文本保存。</summary>
    [SugarColumn(ColumnName = "sources", ColumnDataType = "text", IsNullable = true)]
    public string? Sources { get; set; }

    /// <summary>生成这条笔记的问答会话。</summary>
    [SugarColumn(ColumnName = "source_session_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? SourceSessionId { get; set; }

    /// <summary>
    /// 引用到的全部资料 ID（空格分隔的 `guid:N`）。
    ///
    /// <para>
    /// 为什么冗余一列：`sources` 是 JSON 文本，Postgres 里没法建索引也没法简单的等值查询。
    /// 「哪些笔记引用了这份资料」是界面上会点的操作，靠 LIKE 扫 JSON 文本既慢又脆（字段名一改就失效）。
    /// 写入时维护这一列，查询就变成单列 LIKE。
    /// </para>
    /// </summary>
    [SugarColumn(ColumnName = "source_document_ids", ColumnDataType = "text", IsNullable = false, DefaultValue = "''")]
    public string SourceDocumentIds { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqlSugarNoteRepository(SqlSugarScope database, ILogger<SqlSugarNoteRepository>? logger = null) : INoteRepository
{
    private const string TableName = "kb_notes";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<NoteSchemaEntity>();
        EnsureColumns();
        await BackfillSourceDocumentIdsAsync(cancellationToken);
    }

    /// <summary>
    /// CodeFirst 只在建表时生效：旧的 kb_notes 表不会自动多出新列，必须显式补。
    /// 与 kb_documents / knowledge_chat_messages 用的是同一套做法。
    /// </summary>
    private void EnsureColumns()
    {
        if (!database.DbMaintenance.IsAnyTable(TableName, false)) return;

        EnsureColumn("topic_id", "uuid", true, null);
        EnsureColumn("sources", "text", true, null);
        EnsureColumn("source_session_id", "uuid", true, null);
        EnsureColumn("source_document_ids", "text", false, "''");
    }

    private void EnsureColumn(string columnName, string dataType, bool isNullable, string? defaultValue)
    {
        if (database.DbMaintenance.IsAnyColumn(TableName, columnName, false)) return;

        database.DbMaintenance.AddColumn(TableName, new DbColumnInfo
        {
            DbColumnName = columnName,
            DataType = dataType,
            IsNullable = isNullable,
            DefaultValue = defaultValue
        });
    }

    public Task<IReadOnlyList<Note>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        ListAsync(new NoteQuery(knowledgeBaseId), cancellationToken);

    public async Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken cancellationToken)
    {
        var rows = database.Queryable<NoteSchemaEntity>()
            .Where(note => note.KnowledgeBaseId == query.KnowledgeBaseId);

        // 分类用 ILIKE 而不是 =：Postgres 的 = 区分大小写，而旧实现是在内存里 OrdinalIgnoreCase 比较的。
        // ILIKE 不带通配符就是大小写不敏感等值，行为与旧版一致。
        if (query.NormalizedCategory is { } category)
            rows = rows.Where("category ILIKE @category", new { category });

        if (query.EffectiveDocumentId is { } documentId)
        {
            var token = $"%{documentId:N}%";
            rows = rows.Where("source_document_ids LIKE @token", new { token });
        }

        if (query.EffectiveTopicId is { } topicId)
            rows = rows.Where(note => note.TopicId == topicId);

        if (query.NormalizedSearch is { } search)
        {
            var needle = $"%{EscapeLikePattern(search)}%";
            rows = rows.Where("(title ILIKE @needle OR content ILIKE @needle OR tags ILIKE @needle)", new { needle });
        }

        var result = await rows
            .OrderBy(note => note.IsPinned, OrderByType.Desc)
            .OrderBy(note => note.UpdatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return result.Select(ToDomain).ToList();
    }

    /// <summary>
    /// 旧行的 `source_document_ids` 是空的（该列是后加的）。启动时按 `sources` / `source_document_id` 回填一次，
    /// 只投影三列，不碰正文。
    /// </summary>
    private async Task BackfillSourceDocumentIdsAsync(CancellationToken cancellationToken)
    {
        var pending = await database.Queryable<NoteSchemaEntity>()
            .Where(note => note.SourceDocumentIds == string.Empty)
            .Select(note => new NoteSourceBackfillRow
            {
                Id = note.Id,
                Sources = note.Sources,
                SourceDocumentId = note.SourceDocumentId
            })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return;

        var updated = 0;
        foreach (var row in pending)
        {
            var tokens = BuildDocumentTokens(row.SourceDocumentId, DeserializeSources(row.Sources));
            if (tokens.Length == 0) continue;

            await database.Updateable<NoteSchemaEntity>()
                .SetColumns(note => new NoteSchemaEntity { SourceDocumentIds = tokens })
                .Where(note => note.Id == row.Id && note.SourceDocumentIds == string.Empty)
                .ExecuteCommandAsync(cancellationToken);
            updated++;
        }

        if (updated > 0)
            logger?.LogInformation("Note source backfill: {Count} note(s) indexed by source document", updated);
    }

    public async Task<Note?> GetAsync(Guid noteId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<NoteSchemaEntity>()
            .Where(note => note.Id == noteId)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public Task<int> CountAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        database.Queryable<NoteSchemaEntity>()
            .Where(note => note.KnowledgeBaseId == knowledgeBaseId)
            .CountAsync(cancellationToken);

    public async Task AddAsync(Note note, CancellationToken cancellationToken)
    {
        await database.Insertable(ToEntity(note)).ExecuteCommandAsync(cancellationToken);
    }

    public async Task UpdateAsync(Note note, CancellationToken cancellationToken)
    {
        var entity = ToEntity(note);
        await database.Updateable<NoteSchemaEntity>()
            .SetColumns(item => new NoteSchemaEntity
            {
                Title = entity.Title,
                Content = entity.Content,
                Category = entity.Category,
                Color = entity.Color,
                Tags = entity.Tags,
                IsPinned = entity.IsPinned,
                SourceDocumentId = entity.SourceDocumentId,
                TopicId = entity.TopicId,
                Sources = entity.Sources,
                SourceDocumentIds = entity.SourceDocumentIds,
                SourceSessionId = entity.SourceSessionId,
                UpdatedAt = entity.UpdatedAt
            })
            .Where(item => item.Id == note.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid noteId, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var deleted = await database.Deleteable<NoteSchemaEntity>()
            .Where(note => note.Id == noteId && note.KnowledgeBaseId == knowledgeBaseId)
            .ExecuteCommandAsync(cancellationToken);
        return deleted > 0;
    }

    private static Note ToDomain(NoteSchemaEntity entity) =>
        Note.Restore(
            entity.Id,
            entity.KnowledgeBaseId,
            entity.Title,
            entity.Content,
            entity.Category,
            entity.Color,
            DeserializeTags(entity.Tags),
            entity.IsPinned,
            entity.SourceDocumentId,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.TopicId,
            DeserializeSources(entity.Sources),
            entity.SourceSessionId);

    private static NoteSchemaEntity ToEntity(Note note) => new()
    {
        Id = note.Id,
        KnowledgeBaseId = note.KnowledgeBaseId,
        Title = note.Title,
        Content = note.Content,
        Category = note.Category,
        Color = note.Color,
        Tags = JsonSerializer.Serialize(note.Tags),
        IsPinned = note.IsPinned,
        SourceDocumentId = note.SourceDocumentId,
        TopicId = note.TopicId,
        Sources = note.Sources.Count == 0 ? null : JsonSerializer.Serialize(note.Sources),
        SourceDocumentIds = BuildDocumentTokens(note.SourceDocumentId, note.Sources),
        SourceSessionId = note.SourceSessionId,
        CreatedAt = note.CreatedAt,
        UpdatedAt = note.UpdatedAt
    };

    /// <summary>把「首选资料 + 引用里的资料」汇总成空格分隔的 `guid:N`，供反向查询用。</summary>
    private static string BuildDocumentTokens(Guid? primary, IReadOnlyList<NoteSource> sources)
    {
        var ids = new List<Guid>(sources.Count + 1);
        if (primary is { } documentId && documentId != Guid.Empty) ids.Add(documentId);
        ids.AddRange(sources.Select(source => source.DocumentId).Where(id => id != Guid.Empty));

        return ids.Count == 0
            ? string.Empty
            : string.Join(' ', ids.Distinct().Select(id => id.ToString("N")));
    }

    /// <summary>LIKE 通配符转义：用户搜 `100%` 或 `a_b` 时不应该变成任意匹配。</summary>
    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static IReadOnlyList<string> DeserializeTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>引用片段的反序列化同样要防御：一条脏 JSON 不能让整篇笔记打不开。</summary>
    private static IReadOnlyList<NoteSource> DeserializeSources(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<NoteSource>();
        try
        {
            return JsonSerializer.Deserialize<NoteSource[]>(value) ?? Array.Empty<NoteSource>();
        }
        catch (JsonException)
        {
            return Array.Empty<NoteSource>();
        }
    }
}

/// <summary>回填汇总列时的轻量投影：只要这三列，不进正文。</summary>
internal sealed class NoteSourceBackfillRow
{
    public NoteSourceBackfillRow() { }

    public Guid Id { get; set; }
    public string? Sources { get; set; }
    public Guid? SourceDocumentId { get; set; }
}
