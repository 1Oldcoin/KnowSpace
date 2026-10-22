using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>知识库向量检索的 SqlSugar 实现。</summary>
public sealed class SqlSugarKnowledgeSearchRepository(SqlSugarScope database) : IDocumentSearchRepository
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid knowledgeBaseId,
        float[] embedding,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = PgVectorSql.BuildSearchSql(PgVectorSql.ToVectorLiteral(embedding));
        var rows = await database.Ado.SqlQueryAsync<RetrievedChunkRow>(sql, new
        {
            knowledgeBaseId,
            limit = Math.Clamp(limit, 1, 50)
        }, cancellationToken);

        return rows
            .Select(row => new RetrievedChunk(row.DocumentId, row.FileName, row.Content, row.Similarity))
            .ToList();
    }
}

internal sealed class RetrievedChunkRow
{
    public RetrievedChunkRow()
    {
    }

    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Similarity { get; set; }
}

/// <summary>资料原文读取的 SqlSugar 实现。</summary>
public sealed class SqlSugarDocumentTextRepository(SqlSugarScope database) : IDocumentTextRepository
{
    public async Task<DocumentText?> GetTextAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await database.Queryable<KnowledgeDocumentSchemaEntity>()
            .Where(item => item.Id == documentId)
            .Select(item => new DocumentMetaRow
            {
                Id = item.Id,
                FileName = item.FileName,
                Extension = item.Extension,
                ContentVersion = item.ContentVersion
            })
            .FirstAsync(cancellationToken);
        if (document is null) return null;

        var rows = await database.Queryable<KnowledgeChunkSchemaEntity>()
            .Where(chunk => chunk.DocumentId == documentId && chunk.DocumentVersion == document.ContentVersion)
            .OrderBy(chunk => chunk.ChunkIndex, OrderByType.Asc)
            .Select(chunk => new ChunkRow
            {
                ChunkIndex = chunk.ChunkIndex,
                Content = chunk.Content
            })
            .ToListAsync(cancellationToken);

        var chunks = rows
            .Select(row => new DocumentChunk(row.ChunkIndex, row.Content))
            .ToList();
        return new DocumentText(document.Id, document.FileName, document.Extension, chunks);
    }
}

internal sealed class DocumentMetaRow
{
    public DocumentMetaRow()
    {
    }

    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public int ContentVersion { get; set; }
}

internal sealed class ChunkRow
{
    public ChunkRow()
    {
    }

    public int ChunkIndex { get; set; }
    public string Content { get; set; } = string.Empty;
}

/// <summary>知识库存在性校验的 SqlSugar 实现。</summary>
public sealed class SqlSugarKnowledgeBaseDirectory(SqlSugarScope database) : IKnowledgeBaseDirectory
{
    public Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        database.Queryable<KnowledgeBaseSchemaEntity>()
            .AnyAsync(item => item.Id == knowledgeBaseId, cancellationToken);
}
