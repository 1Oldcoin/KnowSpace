using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// P2-5 问答引用元数据：引用必须能定位到“哪份资料的哪一段”，
/// 并且能落库、读回、导出，历史会话重开时不会丢。
/// </summary>
public sealed class KnowledgeChatCitationTests
{
    private static RetrievedChunk Chunk(string fileName, double similarity, long chunkId, int chunkIndex, string content) =>
        new(Guid.NewGuid(), fileName, content, similarity, chunkId, chunkIndex);

    [Fact]
    public void FromChunks_AssignsRankAndChunkProvenance()
    {
        var chunks = new[]
        {
            Chunk("架构说明.md", 0.81234, 101, 0, "第一段内容"),
            Chunk("部署手册.md", 0.6234, 202, 3, "第二段内容"),
        };

        var citations = KnowledgeChatJson.FromChunks(chunks);

        Assert.Equal(2, citations.Count);
        Assert.Equal(1, citations[0].Rank);
        Assert.Equal(101, citations[0].ChunkId);
        Assert.Equal(0, citations[0].ChunkIndex);
        Assert.Equal(0.8123, citations[0].Similarity, 4);
        Assert.Equal(chunks[0].DocumentId, citations[0].DocumentId);
        Assert.Equal("架构说明.md", citations[0].FileName);
        Assert.Equal(2, citations[1].Rank);
        Assert.Equal(3, citations[1].ChunkIndex);
    }

    [Fact]
    public void FromChunks_TruncatesSnippetAndFlattensWhitespace()
    {
        var longContent = "第一行\n\n第二行   " + new string('填', 500);
        var citations = KnowledgeChatJson.FromChunks(new[] { Chunk("a.md", 0.5, 1, 0, longContent) });

        var snippet = citations[0].Snippet;
        Assert.NotNull(snippet);
        Assert.DoesNotContain('\n', snippet);
        Assert.True(snippet!.Length <= KnowledgeChatJson.MaxSnippetLength + 1);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void FromChunks_EmptyContentYieldsNullSnippet()
    {
        var citations = KnowledgeChatJson.FromChunks(new[] { Chunk("a.md", 0.5, 1, 0, "   ") });

        Assert.Null(citations[0].Snippet);
    }

    [Fact]
    public void Citations_RoundTripThroughJson()
    {
        var original = KnowledgeChatJson.FromChunks(new[]
        {
            Chunk("资料 A.pdf", 0.91, 7, 2, "片段 A"),
        });

        var json = KnowledgeChatJson.SerializeCitations(original);
        var restored = KnowledgeChatJson.DeserializeCitations(json);

        Assert.NotNull(json);
        Assert.Single(restored);
        Assert.Equal(original[0], restored[0]);
    }

    [Fact]
    public void DeserializeCitations_InvalidJsonDegradesToEmpty()
    {
        Assert.Empty(KnowledgeChatJson.DeserializeCitations(null));
        Assert.Empty(KnowledgeChatJson.DeserializeCitations("  "));
        Assert.Empty(KnowledgeChatJson.DeserializeCitations("{ not json"));
        Assert.Empty(KnowledgeChatJson.DeserializeCitations("\"a string\""));
    }

    [Fact]
    public void SerializeMeta_SkipsEmptyPayload()
    {
        Assert.Null(KnowledgeChatJson.SerializeMeta(null));
        Assert.Null(KnowledgeChatJson.SerializeMeta(new KnowledgeChatAnswerMeta()));
        Assert.Null(KnowledgeChatJson.SerializeMeta(new KnowledgeChatAnswerMeta(ToolCalls: [], Sources: [])));
    }

    [Fact]
    public void Meta_RoundTripKeepsRetrievalToolCallsAndSources()
    {
        var meta = new KnowledgeChatAnswerMeta(
            new KnowledgeChatRetrieval(Guid.NewGuid(), "bge-m3@1024", "索引回退到旧版", false),
            new[] { new AgentToolCallTrace("kb_search", "{\"q\":\"x\"}", "命中 3 条", true, 42) },
            new[] { "https://example.com/a" });

        var json = KnowledgeChatJson.SerializeMeta(meta);
        var restored = KnowledgeChatJson.DeserializeMeta(json);

        Assert.NotNull(restored);
        Assert.Equal(meta.Retrieval, restored!.Retrieval);
        Assert.Equal(meta.Sources, restored.Sources);
        var toolCall = Assert.Single(restored.ToolCalls!);
        Assert.Equal("kb_search", toolCall.Name);
        Assert.Equal(42, toolCall.ElapsedMilliseconds);
    }

    [Fact]
    public void DeserializeMeta_InvalidJsonDegradesToNull()
    {
        Assert.Null(KnowledgeChatJson.DeserializeMeta(null));
        Assert.Null(KnowledgeChatJson.DeserializeMeta("[] oops"));
    }

    /// <summary>
    /// 回归：引用必须带上切片标识，否则“引用到哪一段”无从回溯。
    /// 两条检索 SQL（新版索引 / 旧版直查）都要投影 ChunkId 与 ChunkIndex。
    /// </summary>
    [Fact]
    public void RetrievalSql_ProjectsChunkIdentity()
    {
        var literal = PgVectorSql.ToVectorLiteral(new float[] { 0.1f, 0.2f });

        foreach (var sql in new[] { PgVectorSql.BuildSearchSql(literal), PgVectorSql.BuildIndexSearchSql(literal) })
        {
            Assert.Contains("AS \"ChunkId\"", sql, StringComparison.Ordinal);
            Assert.Contains("AS \"ChunkIndex\"", sql, StringComparison.Ordinal);
            Assert.Contains("c.chunk_index", sql, StringComparison.Ordinal);
        }
    }
}
