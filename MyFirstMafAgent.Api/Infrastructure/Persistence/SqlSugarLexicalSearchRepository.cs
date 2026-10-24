using MyFirstMafAgent.Api.Domain.Knowledge;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 词法检索的 SqlSugar 实现。与向量检索共用 <see cref="RetrievedChunkRow"/> 的映射形状，
/// 因此两条链路返回的引用元数据（documentId / chunkId / chunkIndex）完全一致。
/// </summary>
public sealed class SqlSugarLexicalSearchRepository(SqlSugarScope database) : ILexicalSearchRepository
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid knowledgeBaseId,
        string tsQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tsQuery)) return Array.Empty<RetrievedChunk>();

        var rows = await database.Ado.SqlQueryAsync<RetrievedChunkRow>(
            LexicalSearchSql.SearchSql,
            new
            {
                knowledgeBaseId,
                query = tsQuery,
                limit = Math.Clamp(limit, 1, 100)
            },
            cancellationToken);

        return rows
            .Select(row => new RetrievedChunk(
                row.DocumentId,
                row.FileName,
                row.Content,
                row.Similarity,
                row.ChunkId,
                row.ChunkIndex,
                ChunkMatchSources.Lexical))
            .ToList();
    }
}
