using System.Globalization;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// pgvector 相关 SQL 的唯一出口（基础设施层）。
/// SqlSugar 没有 vector 列类型、向量距离运算符以及数据库扩展 DDL 的原生 API，
/// 因此这些无法用表达式表达的部分全部集中在此；其余数据访问均使用 SqlSugar 原生 API。
/// </summary>
internal static class PgVectorSql
{
    /// <summary>
    /// pgvector 列插入时需要显式转换为 vector 类型（参数名即列名）。
    /// 用作 SqlSugar 的 <c>SugarColumn.InsertSql</c> 列级扩展点。
    /// <para>
    /// 仅适用于单行插入：SqlSugar 批量插入（<c>Insertable(List&lt;T&gt;)</c>）不会替换该片段，
    /// 会把 <c>@embedding::vector</c> 原样拼进 SQL 并报 42703。批量写入含 vector 列的表时，
    /// 必须改用 <c>IgnoreColumns</c> 或逐行插入。
    /// </para>
    /// </summary>
    public const string EmbeddingInsertCast = "@embedding::vector";

    /// <summary>
    /// 启用 pgvector 扩展。SqlSugar 的 DbMaintenance 不提供扩展管理 API，只能通过 Ado 执行 DDL。
    /// </summary>
    public static Task EnsureExtensionAsync(SqlSugarScope database, CancellationToken cancellationToken) =>
        database.Ado.ExecuteCommandAsync("CREATE EXTENSION IF NOT EXISTS vector;", null, cancellationToken);

    /// <summary>
    /// 历史库升级：让 kb_chunks.embedding 可为空，并移除旧的「(document_id, chunk_index) 唯一」约束，
    /// 以支持同一资料多版本切片并存。DDL 属于 pgvector 列迁移，集中在此。
    /// </summary>
    public static async Task EnsureChunkCompatibilityAsync(SqlSugarScope database, CancellationToken cancellationToken)
    {
        if (!database.DbMaintenance.IsAnyTable("kb_chunks", false)) return;

        await database.Ado.ExecuteCommandAsync(
            "ALTER TABLE kb_chunks ALTER COLUMN embedding DROP NOT NULL;", null, cancellationToken);

        // 旧版 CodeFirst 生成过两种名字：普通索引 ux_kb_chunks_document_chunk_index
        // 与唯一约束 kb_chunks_document_id_chunk_index_key（约束名不同，必须分别处理）。
        await database.Ado.ExecuteCommandAsync(
            "DROP INDEX IF EXISTS ux_kb_chunks_document_chunk_index;", null, cancellationToken);
        await database.Ado.ExecuteCommandAsync(
            "ALTER TABLE kb_chunks DROP CONSTRAINT IF EXISTS kb_chunks_document_id_chunk_index_key;", null, cancellationToken);
    }

    /// <summary>
    /// 把浮点数组格式化为 pgvector 的向量字面量，例如 <c>[0.1,0.2,...]</c>。
    /// </summary>
    public static string ToVectorLiteral(IEnumerable<float> embedding) =>
        $"[{string.Join(",", embedding.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))}]";

    /// <summary>
    /// 余弦距离表达式。作为 OrderBy 的 SQL 片段使用。
    /// </summary>
    public static string BuildCosineDistance(string embeddingColumn, IEnumerable<float> embedding) =>
        $"{embeddingColumn} <=> '{ToVectorLiteral(embedding)}'::vector";

    /// <summary>
    /// 余弦相似度（0~1，越大越相近）。
    /// <para>
    /// 注意：此前通过 <c>SqlFunc.MappingColumn&lt;double&gt;</c> 投影该表达式时，
    /// SqlSugar 会把结果当作文本读取，触发 "Similarity can't convert string to double"。
    /// 因此检索改为 Ado 原生查询并按列名强类型映射，SQL 中显式转换为 <c>double precision</c>。
    /// </para>
    /// </summary>
    public static string BuildCosineSimilarity(string distanceExpression) =>
        $"(1 - ({distanceExpression}))::double precision";

    /// <summary>构建向量检索 SQL。切片向量字面量由本地模型生成，不包含用户输入。</summary>
    public static string BuildSearchSql(string vectorLiteral) => $"""
        SELECT d.id            AS "DocumentId",
               d.file_name     AS "FileName",
               c.content       AS "Content",
               (1 - (c.embedding <=> '{vectorLiteral}'::vector))::double precision AS "Similarity"
        FROM kb_chunks AS c
        JOIN kb_documents AS d ON d.id = c.document_id
        WHERE d.status = 'Ready'
          AND d.knowledge_base_id = @knowledgeBaseId
          AND c.document_version = d.content_version
          AND c.embedding IS NOT NULL
        ORDER BY c.embedding <=> '{vectorLiteral}'::vector
        LIMIT @limit
        """;

    /// <summary>抽样读取索引向量的维度，用于一致性检查（模型切换后维度不一致会被发现）。</summary>
    public const string DimensionSampleSql = "SELECT vector_dims(embedding) AS \"Value\" FROM kb_index_vectors WHERE index_id = @indexId AND embedding IS NOT NULL LIMIT 200";

    /// <summary>构建索引实例的向量检索 SQL；索引之间通过 index_id 完全隔离。</summary>
    public static string BuildIndexSearchSql(string vectorLiteral) => $"""
        SELECT d.id            AS "DocumentId",
               d.file_name     AS "FileName",
               c.content       AS "Content",
               (1 - (v.embedding <=> '{vectorLiteral}'::vector))::double precision AS "Similarity"
        FROM kb_index_vectors AS v
        JOIN kb_chunks AS c ON c.id = v.chunk_id
        JOIN kb_documents AS d ON d.id = v.document_id
        WHERE v.index_id = @indexId
          AND d.status = 'Ready'
          AND d.knowledge_base_id = @knowledgeBaseId
          AND d.content_version = v.document_version
          AND v.embedding IS NOT NULL
        ORDER BY v.embedding <=> '{vectorLiteral}'::vector
        LIMIT @limit
        """;

    /// <summary>分身记忆的向量检索 SQL；只读 avatar_* 表，与知识库索引完全隔离。</summary>
    public static string BuildAvatarMemorySearchSql(string vectorLiteral) => $"""
        SELECT v.memory_id AS "MemoryId",
               (1 - (v.embedding <=> '{vectorLiteral}'::vector))::double precision AS "Similarity"
        FROM avatar_memory_vectors AS v
        WHERE v.embedding IS NOT NULL
          AND v.embedding_fingerprint = @fingerprint
        ORDER BY v.embedding <=> '{vectorLiteral}'::vector
        LIMIT @limit
        """;
}
