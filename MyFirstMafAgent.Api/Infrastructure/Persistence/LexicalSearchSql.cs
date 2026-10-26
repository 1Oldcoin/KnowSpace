namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 词法检索（全文）的 PostgreSQL DDL 与查询语句。
///
/// <para>
/// 分词在应用侧完成（<see cref="MyFirstMafAgent.Api.Domain.Knowledge.LexicalTokens"/>），
/// 数据库只负责把空格分隔的 token 串转成 <c>tsvector</c>。
/// 因为随包的 PostgreSQL 没有 <c>zhparser</c>，默认解析器会把整段中文当成一个词，
/// 所以索引和查询都必须显式走 <c>to_tsvector('simple', …)</c> + 预分词列。
/// </para>
///
/// <para>
/// 用<b>表达式索引</b>而不是真正的 <c>tsvector</c> 列：写入路径仍是 SqlSugar 的普通批量插入，
/// 不需要为 tsvector 手工拼 SQL；代价是每次查询都要算一遍 <c>to_tsvector</c>（索引已缓存该表达式，开销可控）。
/// </para>
/// </summary>
public static class LexicalSearchSql
{
    /// <summary>GIN 索引名，便于运维直接 drop/reindex。</summary>
    public const string IndexName = "ix_kb_chunks_lexical";

    /// <summary>建表达式索引；已存在时是空操作。</summary>
    public const string EnsureIndexSql = $"""
        CREATE INDEX IF NOT EXISTS {IndexName}
            ON kb_chunks
         USING GIN (to_tsvector('simple', coalesce(lexical_tokens, '')));
        """;

    /// <summary>
    /// 词法检索。查询串由 <c>LexicalTokens.BuildTsQuery</c> 生成（只含字母/数字/CJK 与 <c>|</c>），
    /// 通过参数传入，不拼接用户输入。
    /// </summary>
    public const string SearchSql = """
        SELECT d.id            AS "DocumentId",
               d.file_name     AS "FileName",
               c.content       AS "Content",
               ts_rank(to_tsvector('simple', c.lexical_tokens), to_tsquery('simple', @query))::double precision AS "Similarity",
               c.id            AS "ChunkId",
               c.chunk_index   AS "ChunkIndex"
        FROM kb_chunks AS c
        JOIN kb_documents AS d ON d.id = c.document_id
        WHERE d.status = 'Ready'
          AND d.knowledge_base_id = @knowledgeBaseId
          AND c.document_version = d.content_version
          AND c.lexical_tokens IS NOT NULL
          AND to_tsvector('simple', c.lexical_tokens) @@ to_tsquery('simple', @query)
        ORDER BY "Similarity" DESC, c.id ASC
        LIMIT @limit
        """;

    /// <summary>补分词时按主键推进的游标查询（只取需要的列，避开向量列）。</summary>
    public const string PendingTokensSql = """
        SELECT c.id AS "Id", c.content AS "Content"
        FROM kb_chunks AS c
        WHERE c.lexical_tokens IS NULL
        ORDER BY c.id ASC
        LIMIT @limit
        """;
}
