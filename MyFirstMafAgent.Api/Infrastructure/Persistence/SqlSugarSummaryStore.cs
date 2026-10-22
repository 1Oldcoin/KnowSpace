using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>摘要缓存的 SqlSugar 实现；摘要与向量索引生命周期无关。</summary>
public sealed class SqlSugarSummaryStore(SqlSugarScope database) : ISummaryStore
{
    public async Task<SummaryRecord?> GetLatestAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<DocumentSummarySchemaEntity>()
            .Where(item => item.DocumentId == documentId)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async Task<SummaryRecord?> FindAsync(Guid documentId, string contentHash, string modelFingerprint, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<DocumentSummarySchemaEntity>()
            .Where(item => item.DocumentId == documentId
                && item.ContentHash == contentHash
                && item.ModelFingerprint == modelFingerprint)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async Task SaveAsync(SummaryRecord record, CancellationToken cancellationToken)
    {
        var exists = await database.Queryable<DocumentSummarySchemaEntity>()
            .AnyAsync(item => item.DocumentId == record.DocumentId
                && item.ContentHash == record.ContentHash
                && item.ModelFingerprint == record.ModelFingerprint, cancellationToken);
        if (exists) return;

        await database.Insertable(new DocumentSummarySchemaEntity
        {
            Id = record.Id,
            DocumentId = record.DocumentId,
            ContentHash = record.ContentHash,
            ModelFingerprint = record.ModelFingerprint,
            Summary = record.Summary,
            CreatedAt = record.CreatedAt
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task SetDisplaySummaryAsync(Guid documentId, string summary, CancellationToken cancellationToken)
    {
        await database.Updateable<KnowledgeDocumentSchemaEntity>()
            .SetColumns(item => new KnowledgeDocumentSchemaEntity
            {
                Summary = summary,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == documentId)
            .ExecuteCommandAsync(cancellationToken);
    }

    private static SummaryRecord ToRecord(DocumentSummarySchemaEntity entity) =>
        new(entity.Id, entity.DocumentId, entity.ContentHash, entity.ModelFingerprint, entity.Summary, entity.CreatedAt);
}
