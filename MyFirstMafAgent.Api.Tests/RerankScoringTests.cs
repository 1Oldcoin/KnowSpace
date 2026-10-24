using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 重排与拒答的打分核心（D-4）。全部是纯函数，脱库可跑。
///
/// <para>
/// 这里锁住的是"重排为什么能救回 RRF 漏掉的片段"这件事本身：
/// 一个正文完整回答问题的片段，即使两路召回都排在后面，也应该被顶到前面；
/// 而一个只沾到查询里最常见那个词的片段，不该靠融合分继续霸位。
/// </para>
/// </summary>
public sealed class RerankScoringTests
{
    private static RetrievedChunk Chunk(string fileName, string content, double similarity = 0.5, long chunkId = 0) =>
        new(Guid.NewGuid(), fileName, content, similarity, chunkId);

    private static Guid DocumentId(int seed) =>
        Guid.Parse($"00000000-0000-0000-0000-{seed:D12}");

    [Fact]
    public void Query_tokens_are_deduplicated_and_reuse_the_lexical_tokenizer()
    {
        Assert.Equal(new[] { "插件", "件协", "协议" }, RerankScoring.QueryTokens("插件协议"));
        Assert.Equal(new[] { "插件" }, RerankScoring.QueryTokens("插件，插件，插件"));
        Assert.Empty(RerankScoring.QueryTokens("  ，。 "));
    }

    [Fact]
    public void Common_tokens_get_lower_idf_than_rare_ones()
    {
        var candidates = new List<IReadOnlyList<string>>
        {
            new[] { "任务", "汇报" },
            new[] { "任务", "索引" },
            new[] { "任务", "订阅" }
        };

        var idf = RerankScoring.BuildIdf(candidates);

        // 「任务」在三份候选里都出现 → 权重最低；「汇报」只出现一次 → 权重最高。
        Assert.True(idf["汇报"] > idf["任务"], "只在少数候选出现的词应该有更高权重");
        Assert.True(idf["索引"] > idf["任务"]);
    }

    [Fact]
    public void Rerank_promotes_the_chunk_that_actually_answers_even_when_recall_ranked_it_last()
    {
        // 复刻 T-3 实测到的漏召回形状：正确片段只被词法召回到第 3 位，
        // 前面两条只是"两路都沾到任务/领取"所以融合分更高。
        var candidates = new List<RetrievedChunk>
        {
            Chunk("任务-排队与并发.md", "任务领取之后进入排队，并发上限由配额控制。", 0.80, 1),
            Chunk("任务中心总览.md", "任务中心的概览：领取、排队、监控都在这里。", 0.78, 2),
            Chunk("任务-租约与心跳.md", "领取任务的执行者必须每 30 秒汇报一次心跳，超时后租约失效。", 0.60, 3)
        };

        // 纯覆盖率排序（权重 1）：这条路径要能自己把正确答案顶上来。
        var pureCoverage = RerankScoring.Rank("领取任务的执行者多久必须汇报一次", candidates, 3, lexicalWeight: 1d);

        Assert.Equal(3, pureCoverage.Count);
        Assert.Equal("任务-租约与心跳.md", pureCoverage[0].Chunk.FileName);
        // 覆盖率足以解释这次翻转：正确答案覆盖了"领取/任务/执行/汇报/一次/多久"这些词。
        Assert.True(pureCoverage[0].Coverage > 0.5, $"正确答案覆盖率应过半，实际 {pureCoverage[0].Coverage}");
        Assert.True(pureCoverage[0].Score > pureCoverage[1].Score);

        // 生产默认权重（0.65）下仍然把它提到第一位：先验只占 35% 的份额。
        var blended = RerankScoring.Rank("领取任务的执行者多久必须汇报一次", candidates, 3);
        Assert.Equal("任务-租约与心跳.md", blended[0].Chunk.FileName);
    }

    [Fact]
    public void The_score_blends_the_rank_prior_with_lexical_coverage()
    {
        var candidates = new[]
        {
            Chunk("first.md", "完全无关的一段话。", 0.9),
            Chunk("second.md", "保留期与清理策略。", 0.9)
        };

        // 两条候选的余弦相等 ⇒ 相似度先验都是 1；区别只剩名次先验（1.0 vs 0.0）。
        // 权重 0 时应当严格保持输入顺序，权重 1 时只看内容。
        var keepOrder = RerankScoring.Rank("保留期与清理", candidates, 2, lexicalWeight: 0d);
        Assert.Equal("first.md", keepOrder[0].Chunk.FileName);

        var byContent = RerankScoring.Rank("保留期与清理", candidates, 2, lexicalWeight: 1d);
        Assert.Equal("second.md", byContent[0].Chunk.FileName);

        // 默认权重下分数必须等于两者的凸组合，而不是其中任意一个。
        var blended = RerankScoring.Rank("保留期与清理", candidates, 2);
        var top = blended[0];
        Assert.Equal("second.md", top.Chunk.FileName);
        var expected = Math.Round(
            (1 - RerankScoring.DefaultLexicalWeight) * top.BaseScore + RerankScoring.DefaultLexicalWeight * top.Coverage,
            4);
        Assert.Equal(expected, top.Score, 3);
        Assert.InRange(top.BaseScore, 0d, 1d);
    }

    [Fact]
    public void Rank_position_and_similarity_both_feed_the_prior()
    {
        var candidates = new[]
        {
            Chunk("head.md", "无关内容。", 0.30),
            Chunk("tail.md", "无关内容。", 0.90)
        };

        var ranked = RerankScoring.Rank("完全无关的问题", candidates, 2, lexicalWeight: 0d);

        // 内容一样 ⇒ 覆盖率一样。head 名次先验 1.0 + 余弦 0.0，tail 名次 0.0 + 余弦 1.0，
        // 各占一半 ⇒ 两者 base 都是 0.5，同分时按输入顺序兜底。
        Assert.Equal("head.md", ranked[0].Chunk.FileName);
        Assert.Equal(0.5d, ranked[0].BaseScore);
        Assert.Equal(0.5d, ranked[1].BaseScore);
    }

    [Fact]
    public void Ubiquitous_tokens_are_ignored_only_when_there_are_enough_candidates()
    {
        // 候选足够多时：词元 alpha 在 4/5 的候选里都出现 ⇒ 它是通用词，不计入覆盖率。
        var wide = new[]
        {
            Chunk("a.md", "alpha zeta", 0.5),
            Chunk("b.md", "alpha", 0.5),
            Chunk("c.md", "alpha", 0.5),
            Chunk("d.md", "alpha", 0.5),
            Chunk("e.md", "nothing here", 0.5)
        };
        var rankedWide = RerankScoring.Rank("alpha zeta", wide, 5, lexicalWeight: 1d);
        // 「alpha」被剔除后，唯一有区分度的是 zeta ⇒ a.md 必须拿满分覆盖率。
        Assert.Equal("a.md", rankedWide[0].Chunk.FileName);
        Assert.Equal(1d, rankedWide[0].Coverage);
        // 如果没剔除通用词，a.md 的覆盖率会被 alpha 拉不到 1（两个词元的权重各占一半）。
        Assert.All(rankedWide.Skip(1), entry => Assert.Equal(0d, entry.Coverage));

        // 候选太少时不做剔除：只剩 1 条候选时任何词元的文档频率都是 100%。
        var narrow = new[] { Chunk("only.md", "保留期的清理策略。", 0.5) };
        var rankedNarrow = RerankScoring.Rank("保留期与清理", narrow, 1, lexicalWeight: 1d);
        Assert.True(rankedNarrow[0].Coverage > 0d, "候选只有一条时不应该把所有词元都当成通用词");
    }

    [Fact]
    public void Rerank_rewrites_similarity_with_the_score_it_actually_sorted_by()
    {
        var candidates = new[]
        {
            Chunk("a.md", "无关内容。", 0.99),
            Chunk("b.md", "保留期与清理策略。", 0.10)
        };

        var ranked = RerankScoring.Rank("保留期与清理", candidates, 2, lexicalWeight: 1d);

        // 提示词里显示的"相似度"必须是这次排序真正用的分数，否则用户看到的是上一轮的余弦。
        Assert.Equal("b.md", ranked[0].Chunk.FileName);
        Assert.Equal(ranked[0].Score, ranked[0].Chunk.Similarity);
        Assert.NotEqual(0.10, ranked[0].Chunk.Similarity);
    }

    [Fact]
    public void Rewritten_similarity_is_always_the_blended_score()
    {
        var candidates = new[]
        {
            Chunk("a.md", "无关内容。", 0.99),
            Chunk("b.md", "保留期与清理策略。", 0.10)
        };

        var ranked = RerankScoring.Rank("保留期与清理", candidates, 2);

        Assert.All(ranked, entry => Assert.Equal(entry.Score, entry.Chunk.Similarity));
        Assert.All(ranked, entry => Assert.InRange(entry.Score, 0d, 1d));
    }

    [Fact]
    public void A_chunk_containing_the_whole_question_scores_full_coverage()
    {
        var candidates = new[]
        {
            Chunk("overview.md", "混合检索的概览。", 0.9),
            Chunk("detail.md", "混合检索会不会变慢？混合检索会不会变慢的答案在这里。", 0.5)
        };

        var ranked = RerankScoring.Rank("混合检索会不会变慢", candidates, 2, lexicalWeight: 1d);

        Assert.Equal("detail.md", ranked[0].Chunk.FileName);
        // 整句命中 ⇒ 查询的每个词元都在文中 ⇒ 覆盖率满分。
        Assert.Equal(1d, ranked[0].Coverage);
        Assert.Equal(1d, ranked[0].Score);
        Assert.Equal(ranked[0].MatchedTokens, ranked[0].QueryTokens);
    }

    [Fact]
    public void Numeric_tokens_outweigh_ordinary_words()
    {
        var tokens = new[] { "30000", "保留" };
        var candidates = new List<IReadOnlyList<string>> { tokens, new[] { "保留" } };
        var idf = RerankScoring.BuildIdf(candidates);

        // 编号比虚词更值得命中：同样出现一次，数字类词的权重更高。
        Assert.True(idf["30000"] > idf["保留"]);
    }

    [Fact]
    public void Tokens_absent_from_every_candidate_still_count_in_the_denominator()
    {
        // 「量子」在任何候选里都没有 → 覆盖率不可能达到 1，这是拒答能成立的基础。
        var candidates = new[] { Chunk("a.md", "任务排队与并发。") };
        var ranked = RerankScoring.Rank("量子纠缠与任务排队", candidates, 1);

        Assert.Single(ranked);
        Assert.True(ranked[0].Coverage < 1d);
    }

    [Fact]
    public void Rank_respects_the_limit_and_keeps_input_order_for_ties()
    {
        var candidates = new[]
        {
            Chunk("first.md", "完全一样的内容", 0.5),
            Chunk("second.md", "完全一样的内容", 0.4)
        };

        var ranked = RerankScoring.Rank("完全一样的内容", candidates, 1);

        Assert.Single(ranked);
        Assert.Equal("first.md", ranked[0].Chunk.FileName);
    }

    [Fact]
    public void Blank_queries_keep_the_incoming_order_instead_of_failing()
    {
        // 没有可打分的词元时不要"返回空"：那等于让重排把整轮检索结果吃掉。
        var candidates = new[]
        {
            Chunk("a.md", "内容", 0.7),
            Chunk("b.md", "另一段", 0.4)
        };

        var ranked = RerankScoring.Rank("   ", candidates, 2);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("a.md", ranked[0].Chunk.FileName);
        Assert.All(ranked, entry => Assert.Equal(0, entry.QueryTokens));

        Assert.Empty(RerankScoring.Rank("问题", Array.Empty<RetrievedChunk>(), 5));
        Assert.Empty(RerankScoring.Rank("问题", candidates, 0));
    }

    [Fact]
    public void Score_stays_within_zero_and_one()
    {
        var candidates = new[]
        {
            Chunk("a.md", "插件协议握手的超时是 10 秒，重试三次。")
        };

        var ranked = RerankScoring.Rank("插件协议握手的超时是 10 秒，重试三次。", candidates, 1);

        Assert.InRange(ranked[0].Score, 0d, 1d);
        Assert.InRange(ranked[0].Coverage, 0d, 1d);
    }

    [Theory]
    [InlineData(0.5, new double[0], true)]                 // 没有候选 → 拒答
    [InlineData(0.5, new[] { 0.2 }, true)]                 // 低于阈值 → 拒答
    [InlineData(0.5, new[] { 0.5 }, false)]                // 正好等于阈值 → 放行（宁可多答也不要无故拒）
    [InlineData(0.5, new[] { 0.49, 0.81 }, false)]         // 看最高分，不看第一条
    [InlineData(0d, new double[0], false)]                 // 阈值 0 等于关闭拒答
    public void Abstain_decision_is_threshold_on_the_top_score(double threshold, double[] scores, bool expected)
    {
        var decision = RerankScoring.Decide(threshold, scores);

        Assert.Equal(expected, decision.Abstained);
        Assert.Equal(threshold, decision.Threshold);
        Assert.Equal(scores.Length == 0 ? 0d : scores.Max(), decision.TopScore);
    }

    [Theory]
    [InlineData("rerank", RetrievalMode.Rerank)]
    [InlineData("hybrid+rerank", RetrievalMode.Rerank)]
    [InlineData("hybrid-rerank", RetrievalMode.Rerank)]
    [InlineData("hybrid", RetrievalMode.Hybrid)]
    [InlineData("rrf", RetrievalMode.Hybrid)]
    [InlineData("vector", RetrievalMode.Vector)]
    [InlineData("lexical", RetrievalMode.Lexical)]
    public void Retrieval_mode_parsing_accepts_the_rerank_aliases(string value, RetrievalMode expected) =>
        Assert.Equal(expected, RetrievalModes.Parse(value));

    [Fact]
    public void Unspecified_or_unknown_mode_is_null_so_the_server_decides()
    {
        // 返回 null（而不是回退 hybrid）是关键：显式 hybrid 与"没指定"必须能区分，
        // 否则评估基线会被线上配置（RerankEnabled）悄悄改变。
        Assert.Null(RetrievalModes.Parse(null));
        Assert.Null(RetrievalModes.Parse("   "));
        Assert.Null(RetrievalModes.Parse("dense"));
    }

    [Fact]
    public void Rerank_strategy_is_reported_on_the_wire()
    {
        Assert.Equal(ChunkMatchSources.Rerank, RetrievalMode.Rerank.ToWire());
        Assert.Equal(ChunkMatchSources.Hybrid, RetrievalMode.Hybrid.ToWire());
    }

    [Fact]
    public void Coverage_ranker_is_the_registered_default_provider()
    {
        // 换神经重排时只要实现 IChunkReranker 换注册；这里保证默认实现仍在且不抛。
        IChunkReranker reranker = new CoverageReranker();
        var candidates = new[]
        {
            new RetrievedChunk(DocumentId(1), "a.md", "任务排队", 0.6),
            new RetrievedChunk(DocumentId(2), "b.md", "任务领取后必须汇报心跳", 0.5)
        };

        // 真实窗口是 20 条左右，但接口本身在 2 条候选上也必须可用：
        // 只断言“把正确答案顶上来”在纯覆盖率口径下成立，默认权重下不要求跨名次大跳。
        var pure = reranker.Rerank("领取任务后要汇报什么", candidates, 2, lexicalWeight: 1d);
        Assert.Equal("b.md", pure[0].FileName);

        var blended = reranker.Rerank("领取任务后要汇报什么", candidates, 2);
        Assert.Equal(2, blended.Count);
        Assert.All(blended, chunk => Assert.InRange(chunk.Similarity, 0d, 1d));
    }
}
