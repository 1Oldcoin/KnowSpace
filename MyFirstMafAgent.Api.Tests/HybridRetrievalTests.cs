using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Infrastructure.Persistence;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// P2-1 混合检索：中文分词、RRF 融合与词法 SQL 形状。
/// 这些逻辑不依赖数据库，是最容易被"改坏却没人发现"的部分。
/// </summary>
public sealed class HybridRetrievalTests
{
    private static RetrievedChunk Vector(string fileName, double similarity, long chunkId, int chunkIndex = 0) =>
        new(Guid.NewGuid(), fileName, $"内容-{chunkId}", similarity, chunkId, chunkIndex, ChunkMatchSources.Vector);

    private static RetrievedChunk Lexical(string fileName, double score, long chunkId, int chunkIndex = 0) =>
        new(Guid.NewGuid(), fileName, $"内容-{chunkId}", score, chunkId, chunkIndex, ChunkMatchSources.Lexical);

    // ---- 分词 ----

    [Fact]
    public void Tokenize_ChineseRunBecomesBigrams()
    {
        Assert.Equal(new[] { "插件", "件协", "协议" }, LexicalTokens.Tokenize("插件协议"));
    }

    [Fact]
    public void Tokenize_MixedScriptSplitsLatinWordsAndLowersCase()
    {
        var tokens = LexicalTokens.Tokenize("JSON-RPC 协议 v1");

        Assert.Contains("json", tokens);
        Assert.Contains("rpc", tokens);
        Assert.Contains("v1", tokens);
        Assert.Contains("协议", tokens);
    }

    [Fact]
    public void Tokenize_SingleCjkCharacterSurvives()
    {
        Assert.Equal(new[] { "云" }, LexicalTokens.Tokenize("云"));
    }

    [Fact]
    public void Tokenize_PunctuationOnlyYieldsNothing()
    {
        Assert.Empty(LexicalTokens.Tokenize("   ，。！  "));
        Assert.Empty(LexicalTokens.Tokenize(null));
        Assert.Null(LexicalTokens.BuildTsQuery("？！"));
    }

    [Fact]
    public void Tokenize_RespectsMaxTokens()
    {
        var text = string.Concat(Enumerable.Repeat("插件协议握手流程", 200));
        var tokens = LexicalTokens.Tokenize(text, 50);

        Assert.Equal(50, tokens.Count);
    }

    [Fact]
    public void BuildTsQuery_UsesOrAndDeduplicates()
    {
        // 两个字的词只产生一个二元组，重复出现要在查询里去重
        Assert.Equal("插件", LexicalTokens.BuildTsQuery("插件"));

        var query = LexicalTokens.BuildTsQuery("插件协议，插件协议");

        Assert.Equal("插件 | 件协 | 协议", query);
        // tsquery 语法里不能出现用户输入的引号、括号、& 等字符
        Assert.DoesNotContain('&', query!);
        Assert.DoesNotContain('(', query!);
    }

    // ---- RRF 融合 ----

    [Fact]
    public void Fuse_MarksChunksHitByBothPathsAsHybrid()
    {
        var vectorChunks = new[] { Vector("a.md", 0.9, 11), Vector("b.md", 0.8, 22) };
        var lexicalChunks = new[] { Lexical("b.md", 0.4, 22), Lexical("c.md", 0.3, 33) };

        var fused = ReciprocalRankFusion.Fuse(vectorChunks, lexicalChunks, 5);

        Assert.Equal(3, fused.Count);

        // RRF：两路都命中的切片得分相加，必须排在"只在向量里"的前面。
        Assert.Equal(22, fused[0].ChunkId);
        Assert.Equal(ChunkMatchSources.Hybrid, fused[0].MatchSource);
        Assert.Equal(11, fused[1].ChunkId);
        Assert.Equal(ChunkMatchSources.Vector, fused[1].MatchSource);
        Assert.Equal(33, fused[2].ChunkId);
        Assert.Equal(ChunkMatchSources.Lexical, fused[2].MatchSource);
    }

    [Fact]
    public void Fuse_KeepsVectorSimilarityForDisplay()
    {
        var vectorChunks = new[] { Vector("a.md", 0.8123, 11) };
        var lexicalChunks = new[] { Lexical("a.md", 0.05, 11) };

        var fused = ReciprocalRankFusion.Fuse(vectorChunks, lexicalChunks, 3);

        var chunk = Assert.Single(fused);
        Assert.Equal(0.8123, chunk.Similarity, 4);
    }

    [Fact]
    public void Fuse_DegradesToOnePathWhenOtherSideEmpty()
    {
        var vectorChunks = new[] { Vector("a.md", 0.9, 11), Vector("b.md", 0.7, 22) };

        var onlyVector = ReciprocalRankFusion.Fuse(vectorChunks, Array.Empty<RetrievedChunk>(), 1);
        Assert.Single(onlyVector);
        Assert.Equal(11, onlyVector[0].ChunkId);
        Assert.All(onlyVector, chunk => Assert.Equal(ChunkMatchSources.Vector, chunk.MatchSource));

        var onlyLexical = ReciprocalRankFusion.Fuse(Array.Empty<RetrievedChunk>(), new[] { Lexical("c.md", 0.2, 33) }, 3);
        Assert.Single(onlyLexical);
        Assert.Equal(ChunkMatchSources.Lexical, onlyLexical[0].MatchSource);

        Assert.Empty(ReciprocalRankFusion.Fuse(Array.Empty<RetrievedChunk>(), Array.Empty<RetrievedChunk>(), 3));
    }

    [Fact]
    public void Fuse_RespectsLimitAndNeverDuplicatesChunk()
    {
        var vectorChunks = Enumerable.Range(1, 5).Select(index => Vector("a.md", 0.9 - index * 0.01, index)).ToList();
        var lexicalChunks = Enumerable.Range(1, 5).Select(index => Lexical("a.md", 0.5 - index * 0.01, index)).ToList();

        var fused = ReciprocalRankFusion.Fuse(vectorChunks, lexicalChunks, 3);

        Assert.Equal(3, fused.Count);
        Assert.Equal(fused.Select(chunk => chunk.ChunkId).Distinct().Count(), fused.Count);
    }

    [Fact]
    public void Fuse_DedupesChunksWithoutIdentity()
    {
        // 历史数据没有 chunkId，退化键必须仍然能去重
        var legacyVector = new RetrievedChunk(Guid.NewGuid(), "a.md", "同一段", 0.9);
        var legacyLexical = new RetrievedChunk(legacyVector.DocumentId, "a.md", "同一段", 0.3);

        var fused = ReciprocalRankFusion.Fuse(new[] { legacyVector }, new[] { legacyLexical }, 5);

        var chunk = Assert.Single(fused);
        Assert.Equal(ChunkMatchSources.Hybrid, chunk.MatchSource);
    }

    [Fact]
    public void RetrievalMode_ParsesWireValues()
    {
        Assert.Equal(RetrievalMode.Vector, RetrievalModes.Parse("vector"));
        Assert.Equal(RetrievalMode.Lexical, RetrievalModes.Parse(" LEXICAL "));
        Assert.Equal(RetrievalMode.Hybrid, RetrievalModes.Parse("hybrid"));
        // D-4：没传 / 认不出的值一律返回 null，由服务端配置决定要不要重排，
        // 而不是偷偷当成 hybrid（那会让显式 hybrid 的对比基线失真）。
        Assert.Null(RetrievalModes.Parse(null));
        Assert.Null(RetrievalModes.Parse(""));
        Assert.Null(RetrievalModes.Parse("unknown-mode"));
    }

    // ---- SQL 形状 ----

    /// <summary>回归：查询串必须走参数，不能把分词结果拼进 SQL。</summary>
    [Fact]
    public void LexicalSearchSql_ParameterizesQueryAndProjectsChunkIdentity()
    {
        Assert.Contains("to_tsquery('simple', @query)", LexicalSearchSql.SearchSql, StringComparison.Ordinal);
        Assert.Contains("to_tsvector('simple', c.lexical_tokens)", LexicalSearchSql.SearchSql, StringComparison.Ordinal);
        Assert.Contains("AS \"ChunkId\"", LexicalSearchSql.SearchSql, StringComparison.Ordinal);
        Assert.Contains("AS \"ChunkIndex\"", LexicalSearchSql.SearchSql, StringComparison.Ordinal);
        Assert.Contains("c.document_version = d.content_version", LexicalSearchSql.SearchSql, StringComparison.Ordinal);
        // GIN 表达式索引必须与查询表达式完全一致，否则索引用不上
        Assert.Contains("USING GIN (to_tsvector('simple', coalesce(lexical_tokens, '')))", LexicalSearchSql.EnsureIndexSql, StringComparison.Ordinal);
    }
}
