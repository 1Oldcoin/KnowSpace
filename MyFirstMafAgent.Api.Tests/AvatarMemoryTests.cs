using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Tests;

public sealed class AvatarMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MemoryCreate_KeepsFreeFormKindAndDerivesTitle()
    {
        var memory = AvatarMemory.Create(
            new AvatarMemoryDraft(
                null,
                "  bedtime-routine ",
                null,
                "我习惯在睡前把第二天要做的事写下来，否则会反复想。",
                null,
                null,
                null,
                ["习惯", "睡眠"],
                "平静",
                4,
                "chat"),
            Now);

        // 类型不限定为固定枚举：模型给出的自定义类型原样保留。
        Assert.Equal("bedtime-routine", memory.Kind);
        Assert.Equal("bedtime-routine", AvatarMemoryKinds.Label(memory.Kind));
        Assert.StartsWith("我习惯在睡前", memory.Title);
        Assert.Equal(AvatarMemory.VectorPending, memory.VectorState);
        Assert.Equal(2, memory.Tags.Count);
        Assert.Equal(4, memory.Importance);
    }

    [Fact]
    public void MemoryCreate_NormalizesChineseKindsAndClampsImportance()
    {
        var memory = AvatarMemory.Create(
            new AvatarMemoryDraft(null, "偏好", "喜欢短句", "我写东西偏爱短句。", null, null, null, null, null, 99, "chat"),
            Now);

        Assert.Equal("preference", memory.Kind);
        Assert.Equal("偏好", AvatarMemoryKinds.Label(memory.Kind));
        Assert.Equal(5, memory.Importance);
    }

    [Fact]
    public void MemoryCreate_RejectsEmptyContent()
    {
        Assert.Throws<AvatarValidationException>(() =>
            AvatarMemory.Create(new AvatarMemoryDraft(null, null, "标题", "   ", null, null, null, null, null, 3, "chat"), Now));
    }

    [Fact]
    public void OrganizerParse_ReadsMemoriesAndInsights()
    {
        const string raw = """
            ```json
            {
              "reply": "我记下了，你遇到争执时会先停一下。",
              "memories": [
                {
                  "kind": "identity",
                  "title": "争执时先沉默",
                  "summary": "遇到争执时我不会立刻反驳，会先沉默几秒，再挑最关键的一句说。",
                  "content": "先沉默几秒，再挑最关键的一句说。",
                  "occurredAt": "2026-05-01",
                  "people": ["老王"],
                  "tags": ["沟通", "情绪"],
                  "emotion": "克制",
                  "importance": 5
                },
                { "kind": "preference", "title": "喜欢短句", "summary": "写作偏爱短句推进。" },
                { "title": "", "summary": "" }
              ],
              "insights": [
                {
                  "dimension": "language",
                  "title": "短句推进",
                  "content": "表达时偏爱短句，很少使用长定语。",
                  "confidence": 78,
                  "evidence": ["争执时先沉默"]
                }
              ]
            }
            """;

        var result = AvatarOrganizer.Parse(raw, Guid.NewGuid(), null, "chat", Now);

        Assert.Equal(2, result.Memories.Count);
        Assert.Single(result.Insights);
        Assert.Contains("先停一下", result.Reply);

        var first = result.Memories[0];
        Assert.Equal("identity", first.Kind);
        Assert.Equal("争执时先沉默", first.Title);
        Assert.Equal(5, first.Importance);
        Assert.Single(first.People);
        Assert.Equal(5, first.OccurredAt.Month);

        Assert.Equal("language", result.Insights[0].Dimension);
        Assert.Equal(78, result.Insights[0].Confidence);
    }

    [Fact]
    public void OrganizerParse_ReturnsEmptyWhenModelOutputIsNotJson()
    {
        var result = AvatarOrganizer.Parse("抱歉，我无法整理。", null, null, "chat", Now);

        Assert.Empty(result.Memories);
        Assert.Empty(result.Insights);
    }

    [Fact]
    public void TextSplitter_SplitsByBlankLinesAndCapsSegments()
    {
        var text = "第一段内容\n\n第二段内容\n\n第三段内容";

        var segments = AvatarTextSplitter.Split(text, maxSegments: 2);

        Assert.Equal(2, segments.Count);
        Assert.Equal("第一段内容", segments[0]);
        Assert.Equal("第二段内容", segments[1]);
    }

    [Fact]
    public void TextSimilarity_PrefersTitleMatches()
    {
        var query = AvatarTextSimilarity.BuildGrams("写作卡文");

        var titleHit = AvatarTextSimilarity.Score(query, "写作卡文", "随便写点别的。");
        var contentHit = AvatarTextSimilarity.Score(query, "今天", "写作卡文的时候我会先写大纲。");

        Assert.True(titleHit > 0);
        Assert.True(contentHit > 0);
        Assert.True(titleHit > contentHit);
    }
}
