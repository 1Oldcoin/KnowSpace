using MyFirstMafAgent.Api.Domain.Topics;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

[SugarTable("kb_topics")]
[SugarIndex("ix_kb_topics_knowledge_base_id", nameof(KnowledgeBaseId), OrderByType.Asc, nameof(CreatedAt), OrderByType.Asc, false)]
public sealed class TopicSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "uuid", IsPrimaryKey = true, IsNullable = false)]
    public Guid Id { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = false)]
    public Guid KnowledgeBaseId { get; set; }

    [SugarColumn(ColumnName = "name", ColumnDataType = "text", IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "color", ColumnDataType = "text", IsNullable = false, DefaultValue = "'lavender'")]
    public string Color { get; set; } = "lavender";

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqlSugarTopicRepository(SqlSugarScope database) : ITopicRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<TopicSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Topic>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<TopicSchemaEntity>()
            .Where(topic => topic.KnowledgeBaseId == knowledgeBaseId)
            .OrderBy(topic => topic.CreatedAt, OrderByType.Asc)
            .Select(topic => new TopicRow
            {
                Id = topic.Id,
                KnowledgeBaseId = topic.KnowledgeBaseId,
                Name = topic.Name,
                Color = topic.Color,
                CreatedAt = topic.CreatedAt,
                DocumentCount = SqlFunc.Subqueryable<KnowledgeDocumentSchemaEntity>()
                    .Where(document => document.TopicId == topic.Id)
                    .Count()
            })
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<Topic?> GetAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<TopicSchemaEntity>()
            .Where(topic => topic.KnowledgeBaseId == knowledgeBaseId && topic.Id == topicId)
            .FirstAsync(cancellationToken);
        if (row is null) return null;

        var documentCount = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.TopicId == topicId)
            .CountAsync(cancellationToken);
        return new Topic(row.Id, row.KnowledgeBaseId, row.Name, row.Color, row.CreatedAt, documentCount);
    }

    public Task<bool> NameExistsAsync(Guid knowledgeBaseId, string name, Guid? excludeTopicId, CancellationToken cancellationToken) =>
        database.Queryable<TopicSchemaEntity>()
            .AnyAsync(topic => topic.KnowledgeBaseId == knowledgeBaseId
                && (excludeTopicId == null || topic.Id != excludeTopicId)
                && topic.Name.ToLower() == name.ToLower(), cancellationToken);

    public Task AddAsync(Topic topic, CancellationToken cancellationToken) =>
        database.Insertable(new TopicSchemaEntity
        {
            Id = topic.Id,
            KnowledgeBaseId = topic.KnowledgeBaseId,
            Name = topic.Name,
            Color = topic.Color,
            CreatedAt = topic.CreatedAt,
            UpdatedAt = topic.CreatedAt
        }).ExecuteCommandAsync(cancellationToken);

    public Task UpdateAsync(Topic topic, CancellationToken cancellationToken) =>
        database.Updateable<TopicSchemaEntity>()
            .SetColumns(item => new TopicSchemaEntity
            {
                Name = topic.Name,
                Color = topic.Color,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == topic.Id && item.KnowledgeBaseId == topic.KnowledgeBaseId)
            .ExecuteCommandAsync(cancellationToken);

    public async Task<bool> DeleteAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            var exists = await database.Queryable<TopicSchemaEntity>()
                .AnyAsync(topic => topic.Id == topicId && topic.KnowledgeBaseId == knowledgeBaseId, cancellationToken);
            if (!exists)
            {
                await database.Ado.RollbackTranAsync();
                return false;
            }

            // 专题删除后只解除资料归属，不删除资料本身。
            await database.Updateable<KnowledgeDocumentSchemaEntity>()
                .SetColumns(document => new KnowledgeDocumentSchemaEntity
                {
                    TopicId = null,
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(document => document.TopicId == topicId)
                .ExecuteCommandAsync(cancellationToken);

            await database.Deleteable<TopicSchemaEntity>()
                .Where(topic => topic.Id == topicId && topic.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            await database.Ado.CommitTranAsync();
            return true;
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<DocumentTopicAssignResult> AssignDocumentAsync(Guid documentId, Guid? topicId, CancellationToken cancellationToken)
    {
        // 空结果集上对标量 Guid 使用 FirstAsync 会抛 NullReferenceException。
        var knowledgeBaseIds = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Id == documentId)
            .Select(document => document.KnowledgeBaseId)
            .Take(1)
            .ToListAsync(cancellationToken);
        if (knowledgeBaseIds.Count == 0) return DocumentTopicAssignResult.DocumentNotFound;
        var knowledgeBaseId = knowledgeBaseIds[0];

        if (topicId is not null)
        {
            var topicExists = await database.Queryable<TopicSchemaEntity>()
                .AnyAsync(topic => topic.Id == topicId && topic.KnowledgeBaseId == knowledgeBaseId, cancellationToken);
            if (!topicExists) return DocumentTopicAssignResult.TopicNotFound;
        }

        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(document => new KnowledgeDocumentSchemaEntity
            {
                TopicId = topicId,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(document => document.Id == documentId)
            .ExecuteCommandAsync(cancellationToken);
        return DocumentTopicAssignResult.Success;
    }

    public async Task<Guid?> GetDocumentTopicAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var topics = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(document => document.Id == documentId)
            .Select(document => document.TopicId)
            .Take(1)
            .ToListAsync(cancellationToken);
        return topics.Count == 0 ? null : topics[0];
    }

    private static Topic ToDomain(TopicRow row) =>
        new(row.Id, row.KnowledgeBaseId, row.Name, row.Color, row.CreatedAt, row.DocumentCount);
}

internal sealed class TopicRow
{
    public TopicRow()
    {
    }

    public Guid Id { get; set; }
    public Guid KnowledgeBaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "lavender";
    public DateTimeOffset CreatedAt { get; set; }
    public int DocumentCount { get; set; }
}
