using System.Text.Json;
using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Tests;

public sealed class AvatarFragmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FragmentCreate_DerivesTitleFromContentWhenMissing()
    {
        var fragment = AvatarFragment.Create(
            new AvatarFragmentDraft("日记", null, "今天写了 2000 字，卡在第三章。\n后面还有内容", null, null, null),
            Now);

        Assert.Equal("journal", fragment.Kind);
        Assert.Equal("今天写了 2000 字，卡在第三章。", fragment.Title);
        Assert.False(fragment.IsDigested);
    }

    [Fact]
    public void FragmentCreate_RejectsEmptyContent()
    {
        Assert.Throws<AvatarValidationException>(() =>
            AvatarFragment.Create(new AvatarFragmentDraft("journal", "标题", "   ", null, null, null), Now));
    }

    [Fact]
    public void FragmentCreate_NormalizesUnknownKindToOther()
    {
        var fragment = AvatarFragment.Create(
            new AvatarFragmentDraft("不认识的类型", "标题", "内容", null, "upload", null), Now);

        Assert.Equal("other", fragment.Kind);
        Assert.Equal("upload", fragment.Source);
    }

    [Fact]
    public void InsightCreate_ClampsConfidenceAndTrimsEvidence()
    {
        var insight = AvatarInsight.Create(
            "语言风格",
            "爱用短句",
            "写东西时习惯短句推进，很少用长定语。",
            180,
            ["日记 10-01", " ", "聊天记录 09-20"],
            Now);

        Assert.Equal("language", insight.Dimension);
        Assert.Equal(100, insight.Confidence);
        Assert.Equal(2, insight.EvidenceCount);
    }

    [Fact]
    public void Retriever_RanksMatchingFragmentsFirst()
    {
        var fragments = new List<AvatarFragment>
        {
            AvatarFragment.Create(new AvatarFragmentDraft("journal", "写小说卡文", "第三章卡住了，我想先写大纲再回来。", Now.AddDays(-1), null, null), Now),
            AvatarFragment.Create(new AvatarFragmentDraft("habit", "早起跑步", "每天六点起床去跑五公里。", Now.AddDays(-2), null, null), Now),
            AvatarFragment.Create(new AvatarFragmentDraft("opinion", "关于拖延", "拖延的时候我会把事情拆到最小一步。", Now.AddDays(-3), null, null), Now)
        };

        var ranked = AvatarFragmentRetriever.Rank("写小说卡文了怎么办", fragments, 2);

        Assert.NotEmpty(ranked);
        Assert.Equal("写小说卡文", ranked[0].Fragment.Title);
        Assert.True(ranked[0].Score > 0);
    }

    [Fact]
    public void Retriever_ReturnsEmptyWhenNothingMatches()
    {
        var fragments = new List<AvatarFragment>
        {
            AvatarFragment.Create(new AvatarFragmentDraft("habit", "早起跑步", "每天六点起床去跑五公里。", Now, null, null), Now)
        };

        Assert.Empty(AvatarFragmentRetriever.Rank("量子力学", fragments, 5));
    }

    [Fact]
    public void DigestParse_ReadsStructuredInsights()
    {
        const string raw = """
            ```json
            {
              "insights": [
                {
                  "dimension": "personality",
                  "title": "先想清楚再动手",
                  "content": "遇到新任务时倾向先拆解结构，再开始执行。",
                  "confidence": 80,
                  "evidence": ["日记 10-01"]
                },
                { "title": "", "content": "缺少标题会被跳过" }
              ]
            }
            ```
            """;

        var insights = AvatarDigestService.ParseInsights(raw, Now);

        Assert.Single(insights);
        Assert.Equal("personality", insights[0].Dimension);
        Assert.Equal(80, insights[0].Confidence);
    }

    [Fact]
    public void DigestParse_ReturnsEmptyForNonJson()
    {
        Assert.Empty(AvatarDigestService.ParseInsights("这不是 JSON", Now));
    }

    [Fact]
    public void ChatSession_TitleIsNormalizedAndTruncated()
    {
        var session = AvatarChatSession.Create("   ", Now);
        Assert.Equal("新的对话", session.Title);

        var longTitle = AvatarChatSession.Create(new string('字', 200), Now);
        Assert.Equal(AvatarChatSession.MaxTitleLength, longTitle.Title.Length);
    }

    [Fact]
    public void FragmentKindsAndDimensions_CoverExpectedLabels()
    {
        Assert.Equal("日记", AvatarFragmentKinds.Label("journal"));
        Assert.Equal("语言风格", AvatarInsightDimensions.Label("language"));
        Assert.Equal("unknown-x", AvatarInsightDimensions.Label("unknown-x"));
    }
}
