using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Tests;

public sealed class KnowledgeChunkInsertTests
{
    /// <summary>
    /// 回归：切片批量插入不得引用 kb_chunks.embedding。
    /// 该列只是历史兼容列；若使用 SugarColumn.InsertSql，SqlSugar 在批量插入时
    /// 会把片段原样拼进 SQL，PostgreSQL 报 42703: 字段 "embedding" 不存在。
    /// </summary>
    [Fact]
    public void PublishChunkBatchSql_DoesNotReferenceLegacyEmbeddingColumn()
    {
        var database = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused",
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });

        var rows = new List<KnowledgeChunkSchemaEntity>
        {
            new()
            {
                DocumentId = Guid.NewGuid(),
                DocumentVersion = 2,
                ChunkIndex = 0,
                Content = "第一段",
                ContentHash = "hash-0",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new()
            {
                DocumentId = Guid.NewGuid(),
                DocumentVersion = 2,
                ChunkIndex = 1,
                Content = "第二段",
                ContentHash = "hash-1",
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        var sql = database.Insertable(rows)
            .IgnoreColumns(chunk => new { chunk.Embedding })
            .ToSqlString();

        Assert.DoesNotContain("embedding", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO \"kb_chunks\"", sql, StringComparison.Ordinal);
    }
}
