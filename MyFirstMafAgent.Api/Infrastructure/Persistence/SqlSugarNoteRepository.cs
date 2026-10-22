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

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqlSugarNoteRepository(SqlSugarScope database) : INoteRepository
{
    private const string TableName = "kb_notes";

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<NoteSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Note>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<NoteSchemaEntity>()
            .Where(note => note.KnowledgeBaseId == knowledgeBaseId)
            .OrderBy(note => note.IsPinned, OrderByType.Desc)
            .OrderBy(note => note.UpdatedAt, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
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
            entity.UpdatedAt);

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
        CreatedAt = note.CreatedAt,
        UpdatedAt = note.UpdatedAt
    };

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
}
